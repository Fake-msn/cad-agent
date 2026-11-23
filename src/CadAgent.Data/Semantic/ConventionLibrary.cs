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
                // T3：NativeObject（块属性原生数据）除外——不得被图层推断覆盖删除，
                // 与 HumanConfirmed 共存（闸口 Any(conf≥min) 取高置信，共存无害）。
                overlay.Annotations.RemoveAll(x => x.TargetHandle == handle && x.Provenance != Provenance.NativeObject);
                // T4：派生标注经实体世界坐标归属 stamp SheetId；Mappings 本身保持全局 null
                //（层映射跨幅通用——同一层名在各幅面语义一致，不逐幅登记）
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    handle, mapping.Class, Provenance.HumanConfirmed, 1.0, $"人工确认图层映射: {layer}", w.SheetId));
                continue;
            }

            if (TryGetSkillClassForLayer(skillAliases, layer, canon, out var skillCls))
            {
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    handle, skillCls, Provenance.LayerInference, 0.8, $"skill别名: {layer}", w.SheetId));
                continue;
            }

            var cls = ConventionLibrary.ClassForLayer(canon);
            if (cls == SemanticClass.Unknown) continue;
            overlay.UpsertAnnotation(new SemanticAnnotation(
                handle, cls, Provenance.LayerInference, 0.8, $"图层约定: {layer}", w.SheetId));
        }
    }

    /// <summary>
    /// 人工映射查找（T4 两段式）：先精确原名扫描再归一名扫描——原名优先，
    /// 冲突时精确登记（如 "x$0$WALL"）胜过归一兜底（"WALL"）；
    /// 无精确原名时归一名兜底（人工映射 "WALL" 亦覆盖 "x$0$WALL"；映射清单本身保留原名）。
    /// 与 Scanner 歧义判定共用，保持精确互逆。
    /// </summary>
    internal static LayerMapping? FindMapping(SemanticOverlay overlay, string layer, string canon) =>
        overlay.Mappings.FirstOrDefault(m =>
            string.Equals(m.LayerName, layer, StringComparison.OrdinalIgnoreCase))
        ?? overlay.Mappings.FirstOrDefault(m =>
            string.Equals(m.LayerName, canon, StringComparison.OrdinalIgnoreCase));

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
