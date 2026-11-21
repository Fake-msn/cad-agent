using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// Outcome of one composed scan: the (possibly pre-existing) overlay enriched
/// with inference annotations, plus the evidence list of layers that neither
/// the convention library, a human mapping nor a skill alias can classify.
/// </summary>
public sealed record ScanResult(SemanticOverlay Overlay, IReadOnlyList<AmbiguousLayer> Ambiguous);

/// <summary>
/// Single entry point composing all deterministic reading signals:
/// LayerInference (human mapping first) → WallPair → Axis → DoorArc, then the
/// ambiguous-layer list. Passing an existing overlay preserves human mappings
/// and prior annotations for living entities; detectors never downgrade
/// higher-confidence entries. Annotations whose target handle no longer exists
/// in the document are pruned (stale semantics must not satisfy the gate).
/// </summary>
public static class SemanticScanner
{
    // 原两参入口保留并委托新 overload（null 别名）。注意：existing 的 = null
    // 默认值只能留在三参 overload 上——两处都带默认值会让 Scan(session) 产生
    // CS0121 二义性。既有调用 Scan(session) / Scan(session, overlay) 全部照旧。
    public static ScanResult Scan(DrawingSession session, SemanticOverlay? existing) =>
        Scan(session, existing, skillAliases: null);

    public static ScanResult Scan(DrawingSession session, SemanticOverlay? existing = null,
        IReadOnlyDictionary<string, SemanticClass>? skillAliases = null)
    {
        var overlay = existing ?? new SemanticOverlay();

        LayerInference.Annotate(session, overlay, skillAliases);
        WallPairDetector.Detect(session, overlay);
        AxisDetector.Detect(session, overlay);
        DoorArcDetector.Detect(session, overlay);

        // 剪枝（最终审查裁定）：已删除实体（或换图后不存在）的标注必须移除，
        // 否则操作级闸口的存在性检查会被幻影实体满足。图层级 Mappings 保留。
        var liveHandles = new HashSet<string>(session.Doc.Entities.Select(e => e.Handle.ToString()));
        overlay.Annotations.RemoveAll(a => !liveHandles.Contains(a.TargetHandle));

        return new ScanResult(overlay, CollectAmbiguousLayers(session, overlay, skillAliases));
    }

    /// <summary>
    /// A layer is ambiguous iff it misses the convention library, has no
    /// human mapping AND no skill-alias hit — the exact inverse of
    /// LayerInference's precedence, so a mapped or alias-covered layer is never
    /// reported. Case-insensitive on all checks;
    /// entities are grouped in document order, samples capped at 5 handles,
    /// results sorted by entity count descending.
    /// </summary>
    private static IReadOnlyList<AmbiguousLayer> CollectAmbiguousLayers(
        DrawingSession session, SemanticOverlay overlay,
        IReadOnlyDictionary<string, SemanticClass>? skillAliases = null)
    {
        var byLayer = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in session.Doc.Entities)
        {
            string layer = e.Layer?.Name ?? "0";
            if (ConventionLibrary.ClassForLayer(layer) != SemanticClass.Unknown) continue;
            if (overlay.Mappings.Any(m =>
                    string.Equals(m.LayerName, layer, StringComparison.OrdinalIgnoreCase))) continue;
            if (LayerInference.TryGetSkillClass(skillAliases, layer, out _)) continue;

            if (!byLayer.TryGetValue(layer, out var handles))
                byLayer[layer] = handles = new List<string>();
            handles.Add(e.Handle.ToString());
        }

        return byLayer
            .Select(kv => new AmbiguousLayer(
                kv.Key, kv.Value.Count, kv.Value.Take(5).ToList(),
                ConventionLibrary.HintForLayer(kv.Key)))
            .OrderByDescending(a => a.EntityCount)
            .ToList();
    }
}
