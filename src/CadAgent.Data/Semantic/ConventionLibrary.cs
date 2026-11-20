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

    public static IReadOnlyDictionary<string, SemanticClass> KnownLayers => Layers;

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
/// (HumanConfirmed 1.0) > convention library hit (LayerInference 0.8).
/// Unknown layers get no annotation; the Scanner owns the ambiguous-layer list.
/// </summary>
public static class LayerInference
{
    public static void Annotate(DrawingSession session, SemanticOverlay overlay)
    {
        var humanConfirmedHandles = overlay.Annotations
            .Where(a => a.Provenance == Provenance.HumanConfirmed)
            .Select(a => a.TargetHandle)
            .ToHashSet();

        foreach (var e in session.Doc.Entities)
        {
            string handle = e.Handle.ToString();
            if (humanConfirmedHandles.Contains(handle)) continue;

            string layer = e.Layer?.Name ?? "0";
            var mapping = overlay.Mappings.FirstOrDefault(m =>
                string.Equals(m.LayerName, layer, StringComparison.OrdinalIgnoreCase));
            if (mapping is not null)
            {
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    handle, mapping.Class, Provenance.HumanConfirmed, 1.0, $"人工确认图层映射: {layer}"));
                continue;
            }

            var cls = ConventionLibrary.ClassForLayer(layer);
            if (cls == SemanticClass.Unknown) continue;
            overlay.UpsertAnnotation(new SemanticAnnotation(
                handle, cls, Provenance.LayerInference, 0.8, $"图层约定: {layer}"));
        }
    }
}