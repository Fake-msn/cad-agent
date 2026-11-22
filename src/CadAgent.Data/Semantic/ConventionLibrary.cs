using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// TArch (天正) standard layer names plus common custom aliases, mapped to
/// semantic classes. Lookup is case-insensitive; misses return Unknown.
/// </summary>
public static class ConventionLibrary
{
    private static readonly Dictionary<string, SemanticClass> Layers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WALL"] = SemanticClass.Wall,
        ["WALL_NEW"] = SemanticClass.Wall,
        ["墙"] = SemanticClass.Wall,
        ["墙体"] = SemanticClass.Wall,
        ["W-墙"] = SemanticClass.Wall,
        ["WINDOW"] = SemanticClass.Window,
        ["窗"] = SemanticClass.Window,
        ["DOOR"] = SemanticClass.Door,
        ["门"] = SemanticClass.Door,
        ["COLUMN"] = SemanticClass.Column,
        ["柱"] = SemanticClass.Column,
        ["AXIS"] = SemanticClass.Axis,
        ["轴网"] = SemanticClass.Axis,
        ["AXIS_TEXT"] = SemanticClass.Axis,
        ["PUB_DIM"] = SemanticClass.Annotation,
        ["PUB_TEXT"] = SemanticClass.Annotation,
        ["DIM"] = SemanticClass.Annotation,
        ["标注"] = SemanticClass.Annotation,
        ["STAIR"] = SemanticClass.Auxiliary,
        ["楼梯"] = SemanticClass.Auxiliary,
        ["FURNITURE"] = SemanticClass.Auxiliary,
        ["家具"] = SemanticClass.Auxiliary,
    };

    public static IReadOnlyDictionary<string, SemanticClass> KnownLayers =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, SemanticClass>(Layers);

    public static SemanticClass ClassForLayer(string layerName) =>
        !string.IsNullOrEmpty(layerName) && Layers.TryGetValue(layerName, out var cls)
            ? cls
            : SemanticClass.Unknown;

    private static readonly (string Keyword, string Hint)[] NameClues =
    {
        ("wall", "疑似墙体"),
        ("墙", "疑似墙体"),
        ("door", "疑似门"),
        ("门", "疑似门"),
        ("window", "疑似窗"),
        ("窗", "疑似窗"),
        ("axis", "疑似轴网"),
        ("轴", "疑似轴网"),
        ("dim", "疑似标注"),
        ("标注", "疑似标注"),
    };

    /// <summary>
    /// Name-based clue for layers outside the convention library (e.g. "W-新墙").
    /// Returns "" when no keyword matches; a hint is never an annotation.
    /// </summary>
    public static string HintForLayer(string layerName)
    {
        if (string.IsNullOrEmpty(layerName)) return "";
        foreach (var (keyword, hint) in NameClues)
            if (layerName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return hint;
        return "";
    }
}

/// <summary>
/// First deterministic "reading" signal: annotate entities from their layer.
/// Precedence: existing HumanConfirmed annotation (skip) > human layer mapping
/// (HumanConfirmed 1.0) > skill alias hit (LayerInference 0.8) > convention
/// library hit (LayerInference 0.8).
/// Unknown layers get no annotation; the Scanner owns the ambiguous-layer list.
/// T2：实体源改 BlockWalker（含 bound xref 嵌套实体，句柄=HandlePath）；
/// 约定匹配用归一层名（"x$0$WALL"→WALL），映射/证据保留原名。
/// </summary>
public static class LayerInference
{
    public static void Annotate(DrawingSession session, SemanticOverlay overlay) =>
        Annotate(session, overlay, skillAliases: null);

    public static void Annotate(DrawingSession session, SemanticOverlay overlay,
        IReadOnlyDictionary<string, SemanticClass>? skillAliases = null) =>
        AnnotateWalked(BlockWalker.Walk(session.Doc, session.Sheets).ToList(), overlay, skillAliases);

    /// <summary>块遍历口径核心（扫描器复用同一次 Walk）。</summary>
    internal static void AnnotateWalked(IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay,
        IReadOnlyDictionary<string, SemanticClass>? skillAliases = null)
    {
        var humanConfirmedHandles = overlay.Annotations
            .Where(a => a.Provenance == Provenance.HumanConfirmed)
            .Select(a => a.TargetHandle)
            .ToHashSet();

        foreach (var w in walked)
        {
            string handle = w.HandlePath;
            if (humanConfirmedHandles.Contains(handle)) continue;

            string layer = w.Entity.Layer?.Name ?? "0";
            string canon = w.CanonicalLayer;
            var mapping = FindMapping(overlay, layer, canon);
            if (mapping is not null)
            {
                // Mirror Task 1 ApplyMappingToAnnotations: a human mapping override
                // clears any stale inference annotation for this handle first.
                overlay.Annotations.RemoveAll(x => x.TargetHandle == handle);
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    handle, mapping.Class, Provenance.HumanConfirmed, 1.0, $"人工确认图层映射: {layer}"));
                continue;
            }

            if (TryGetSkillClassForLayer(skillAliases, layer, canon, out var skillCls))
            {
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    handle, skillCls, Provenance.LayerInference, 0.8, $"skill别名: {layer}"));
                continue;
            }

            var cls = ConventionLibrary.ClassForLayer(canon);
            if (cls == SemanticClass.Unknown) continue;
            overlay.UpsertAnnotation(new SemanticAnnotation(
                handle, cls, Provenance.LayerInference, 0.8, $"图层约定: {layer}"));
        }
    }

    /// <summary>
    /// 人工映射查找：原名优先，其次归一名（人工映射 "WALL" 亦覆盖 "x$0$WALL"；
    /// 映射清单本身保留原名）。与 Scanner 歧义判定共用，保持精确互逆。
    /// </summary>
    internal static LayerMapping? FindMapping(SemanticOverlay overlay, string layer, string canon) =>
        overlay.Mappings.FirstOrDefault(m =>
            string.Equals(m.LayerName, layer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.LayerName, canon, StringComparison.OrdinalIgnoreCase));

    /// <summary>skill 别名查找：原名优先，其次归一名（与约定匹配同口径）。</summary>
    internal static bool TryGetSkillClassForLayer(
        IReadOnlyDictionary<string, SemanticClass>? skillAliases, string layer, string canon, out SemanticClass cls) =>
        TryGetSkillClass(skillAliases, layer, out cls) || TryGetSkillClass(skillAliases, canon, out cls);

    /// <summary>
    /// Shared skill-alias lookup for Annotate and the Scanner's ambiguous list,
    /// so both stay exact inverses. Honors the dictionary's own comparer first
    /// (SkillSet.MergedAliases is OrdinalIgnoreCase), then falls back to a
    /// case-insensitive scan for caller-built ordinal dictionaries — matching
    /// the case-insensitive convention of mappings and the convention library.
    /// An alias mapped to Unknown is not a hit (no annotation, stays ambiguous).
    /// </summary>
    internal static bool TryGetSkillClass(
        IReadOnlyDictionary<string, SemanticClass>? skillAliases, string layer, out SemanticClass cls)
    {
        cls = SemanticClass.Unknown;
        if (skillAliases is null) return false;
        if (!skillAliases.TryGetValue(layer, out var found))
        {
            found = SemanticClass.Unknown;
            foreach (var kv in skillAliases)
                if (string.Equals(kv.Key, layer, StringComparison.OrdinalIgnoreCase))
                {
                    found = kv.Value;
                    break;
                }
        }
        if (found == SemanticClass.Unknown) return false;
        cls = found;
        return true;
    }
}
