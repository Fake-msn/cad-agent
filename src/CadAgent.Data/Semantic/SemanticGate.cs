using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 真实语义闸口：每个所需类须在叠加层存在 Confidence≥minConfidence 的标注，
/// 否则拒绝并提示先 semantic scan/map 确认（诚实降级：低置信推断不放行）。
/// </summary>
public sealed class SemanticGate : ISemanticGate
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
}