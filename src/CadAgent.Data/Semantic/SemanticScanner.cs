using CadAgent.Core;
using CadAgent.Data.Vision;

namespace CadAgent.Data.Semantic;

/// <summary>
/// Outcome of one composed scan: the (possibly pre-existing) overlay enriched
/// with inference annotations, plus the evidence list of layers that neither
/// the convention library, a human mapping nor a skill alias can classify.
/// T6：末尾可选 VisionAmbiguous（视觉声称歧义清单）——legacy 两参构造默认空列表，
/// 既有调用点/消费端全兼容（null 归一为空，消费端无需判空）。
/// </summary>
public sealed record ScanResult(SemanticOverlay Overlay, IReadOnlyList<AmbiguousLayer> Ambiguous,
    IReadOnlyList<VisionAmbiguous>? VisionAmbiguous = null)
{
    // 位置 record 允许手写属性接管合成：构造参数喂入，属性把 null 归一为空清单。
    public IReadOnlyList<VisionAmbiguous> VisionAmbiguous { get; init; } =
        VisionAmbiguous ?? Array.Empty<VisionAmbiguous>();
}

/// <summary>
/// Single entry point composing all deterministic reading signals:
/// BlockAttributeSeeder (T3 NativeObject) → LayerInference (human mapping first)
/// → WallPair → Axis → DoorArc, then the ambiguous-layer list. Passing an
/// existing overlay preserves human mappings and prior annotations for living
/// entities; detectors never downgrade higher-confidence entries. Annotations
/// whose target handle no longer exists
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
        IReadOnlyDictionary<string, SemanticClass>? skillAliases = null, VisionReadResult? vision = null)
    {
        var overlay = existing ?? new SemanticOverlay();

        // T2：单次块遍历供全部信号共用（含 bound xref 嵌套实体；世界坐标+后缀句柄+归一层名）
        var walked = BlockWalker.Walk(session.Doc, session.Sheets).ToList();
        // T3：块属性种子先于图层推断（NativeObject 0.95 确定性原生数据优先；
        // Upsert 键=handle+provenance，与 LayerInference 0.8 共存，闸口 Any(conf≥min) 取高置信）
        BlockAttributeSeeder.SeedWalked(walked, overlay);
        LayerInference.AnnotateWalked(walked, overlay, skillAliases);
        WallPairDetector.DetectWalked(walked, overlay);
        AxisDetector.DetectWalked(walked, overlay);
        DoorArcDetector.DetectWalked(walked, overlay);
        RoomDetector.AnnotateRoomsWalked(walked, overlay);

        // 剪枝（最终审查裁定）：已删除实体（或换图后不存在）的标注必须移除，
        // 否则操作级闸口的存在性检查会被幻影实体满足。图层级 Mappings 保留。
        // 存活口径=遍历句柄路径（顶层句柄+嵌套 "{parent}:{i}" 后缀）。
        var liveHandles = new HashSet<string>(walked.Select(w => w.HandlePath));
        overlay.Annotations.RemoveAll(a => !liveHandles.Contains(a.TargetHandle));

        // T6 视觉管道（可选，第五级来源）：逐幅面交叉验证——声称只以本幅面矢量 GT 佐证
        //（跨幅跑偏拒绝），种子 Upsert（键=handle+provenance，与 NativeObject/LayerInference
        // 共存，闸口 Any(conf≥min) 取高置信）；歧义清单按 (Claim,Reason) 去重并入 ScanResult。
        // vision=null → legacy 行为不变（VisionAmbiguous 空）。
        var visionAmbiguous = new List<VisionAmbiguous>();
        if (vision is not null)
        {
            foreach (var sheet in session.Sheets)
            {
                var cross = VisionCrossChecker.Check(vision, overlay, walked, sheet.SheetId);
                foreach (var a in cross.Seeded) overlay.UpsertAnnotation(a);
                visionAmbiguous.AddRange(cross.Ambiguous);
            }
        }

        return new ScanResult(overlay, CollectAmbiguousLayers(walked, overlay, skillAliases),
            visionAmbiguous.Distinct().ToList());
    }

    /// <summary>
    /// A layer is ambiguous iff it misses the convention library, has no
    /// human mapping AND no skill-alias hit — the exact inverse of
    /// LayerInference's precedence, so a mapped or alias-covered layer is never
    /// reported. Case-insensitive on all checks;
    /// entities are grouped in document order, samples capped at 5 handles,
    /// results sorted by entity count descending.
    /// T2：遍历口径（含嵌套实体）；约定/别名匹配用归一名，分组键与样本句柄保留原名/HandlePath。
    /// </summary>
    private static IReadOnlyList<AmbiguousLayer> CollectAmbiguousLayers(
        IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay,
        IReadOnlyDictionary<string, SemanticClass>? skillAliases = null)
    {
        var byLayer = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in walked)
        {
            string layer = w.Entity.Layer?.Name ?? "0";
            string canon = w.CanonicalLayer;
            if (ConventionLibrary.ClassForLayer(canon) != SemanticClass.Unknown) continue;
            if (LayerInference.FindMapping(overlay, layer, canon) is not null) continue;
            if (LayerInference.TryGetSkillClassForLayer(skillAliases, layer, canon, out _)) continue;

            if (!byLayer.TryGetValue(layer, out var handles))
                byLayer[layer] = handles = new List<string>();
            handles.Add(w.HandlePath);
        }

        return byLayer
            .Select(kv => new AmbiguousLayer(
                kv.Key, kv.Value.Count, kv.Value.Take(5).ToList(),
                ConventionLibrary.HintForLayer(kv.Key)))
            .OrderByDescending(a => a.EntityCount)
            .ToList();
    }
}
