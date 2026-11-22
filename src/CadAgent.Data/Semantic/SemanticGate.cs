using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 真实语义闸口：每个所需类须在叠加层存在 Confidence≥minConfidence 的标注，
/// 否则拒绝并提示先 semantic scan/map 确认（诚实降级：低置信推断不放行）。
/// </summary>
public sealed class SemanticGate : ISemanticContextGate
{
    private readonly SemanticOverlay _overlay;
    private readonly double _minConfidence;

    public SemanticGate(SemanticOverlay overlay, double minConfidence = 0.8)
    {
        _overlay = overlay;
        _minConfidence = minConfidence;
    }

    public GateDecision Check(IReadOnlyList<SemanticClass> required)
    {
        foreach (var cls in required)
            if (!_overlay.Annotations.Any(a => a.Class == cls && a.Confidence >= _minConfidence))
                return new GateDecision(false,
                    $"缺少语义 {cls}（需置信度≥{_minConfidence}，请先 semantic scan/map 确认）");
        return new GateDecision(true, "");
    }

    /// <summary>
    /// 实体级（计划 3a）：目标句柄须携带每个所需类的 ≥minConfidence 标注（禁触区精确到实体）；
    /// targetHandle null → 操作级退化。两参委托三参 sheetId=null（legacy 无作用域，行为保持）。
    /// </summary>
    public GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle)
        => CheckContext(required, targetHandle, sheetId: null);

    /// <summary>
    /// 幅面作用域（T4）：sheetId 非 null 时标注须 SheetId==sheetId（同幅确认）
    /// 或 SheetId==null（legacy 全局标注兼容放行）且置信≥min——A 幅面确认不得放行 B 幅面操作。
    /// targetHandle null → 操作级同幅过滤；sheetId null → 与两参完全一致（legacy）。
    /// </summary>
    public GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle, string? sheetId)
    {
        // 作用域谓词：sheetId null=不限制；标注 SheetId null=旧数据全局（legacy 兼容）
        bool InScope(SemanticAnnotation a) => sheetId == null || a.SheetId == null || a.SheetId == sheetId;
        // legacy 路径（sheetId null）拒绝消息保持原样；幅面路径追加作用域注记
        string scope = sheetId == null ? "" : $"（幅面 {sheetId} 作用域）";
        if (targetHandle == null)
        {
            foreach (var cls in required)
                if (!_overlay.Annotations.Any(a => a.Class == cls && a.Confidence >= _minConfidence && InScope(a)))
                    return new GateDecision(false,
                        $"缺少语义 {cls}{scope}（需置信度≥{_minConfidence}，请先 semantic scan/map 确认）");
            return new GateDecision(true, "");
        }
        foreach (var cls in required)
            if (!_overlay.Annotations.Any(a => a.TargetHandle == targetHandle && a.Class == cls && a.Confidence >= _minConfidence && InScope(a)))
                return new GateDecision(false,
                    $"实体 {targetHandle} 缺少语义 {cls}{scope}（需置信度≥{_minConfidence}，请先 semantic scan/map 确认）");
        return new GateDecision(true, "");
    }
}
