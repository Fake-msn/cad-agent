using System.Text.RegularExpressions;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// Axis-grid heuristic: a Circle with radius in [minRadius, maxRadius] and a
/// nearby TextEntity whose value is a 1-2 digit number (insertion point within
/// radius×1.5 of the center) annotates both entities Axis/GeometryInference/0.6.
/// Arc derives from Circle in ACadSharp and is explicitly excluded.
/// Existing annotations with confidence &gt; 0.6 (HumanConfirmed 1.0,
/// LayerInference 0.8) are never overwritten.
/// </summary>
public static class AxisDetector
{
    private const double Confidence = 0.6;
    private static readonly Regex NumberPattern = new(@"^\d{1,2}$", RegexOptions.Compiled);

    public static void Detect(DrawingSession session, SemanticOverlay overlay,
        double minRadius = 100, double maxRadius = 1000)
        => DetectWalked(BlockWalker.Walk(session.Doc, session.Sheets).ToList(), overlay, minRadius, maxRadius);

    /// <summary>块遍历口径核心（扫描器复用同一次 Walk）：圆心/文本插入点/半径均用世界坐标。
    /// T4：幅面归属随实体携带（WalkedEntity.SheetId），产出标注 stamp。</summary>
    internal static void DetectWalked(IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay,
        double minRadius = 100, double maxRadius = 1000)
    {
        var texts = new List<(string Handle, double X, double Y, string Value, string? SheetId)>();
        foreach (var w in walked)
        {
            if (w.Entity is not TextEntity text || string.IsNullOrEmpty(text.Value)) continue;
            string value = text.Value.Trim();
            if (!NumberPattern.IsMatch(value)) continue;
            texts.Add((w.HandlePath, w.WorldX, w.WorldY, value, w.SheetId));
        }
        if (texts.Count == 0) return;

        foreach (var w in walked)
        {
            if (w.Entity is not Circle circle || w.Entity is Arc) continue;
            // 世界半径：WorldPoints[1]=(半径,0)（Walk 内已按 RadiusScale 缩放；顶层=本地半径）
            double radius = w.WorldPoints.Count >= 2 ? w.WorldPoints[1].X : circle.Radius;
            if (radius < minRadius || radius > maxRadius) continue;

            // Nearest qualifying number text within radius×1.5 (2D distance).
            double maxDist = radius * 1.5;
            (string Handle, double X, double Y, string Value, string? SheetId)? best = null;
            double bestDist = double.MaxValue;
            foreach (var t in texts)
            {
                double dx = t.X - w.WorldX;
                double dy = t.Y - w.WorldY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= maxDist && dist < bestDist)
                {
                    best = t;
                    bestDist = dist;
                }
            }
            if (best is null) continue;

            string evidence = $"轴号圆圈 r={radius:F0} 编号={best.Value.Value}";
            Annotate(overlay, w.HandlePath, evidence, w.SheetId);
            Annotate(overlay, best.Value.Handle, evidence, best.Value.SheetId);
        }
    }

    private static void Annotate(SemanticOverlay overlay, string handle, string evidence, string? sheetId)
    {
        if (overlay.Annotations.Any(x => x.TargetHandle == handle && x.Confidence > Confidence)) return;
        // T4：stamp 幅面归属（框外=null legacy 全局）
        overlay.UpsertAnnotation(new SemanticAnnotation(
            handle, SemanticClass.Axis, Provenance.GeometryInference, Confidence, evidence, sheetId));
    }
}
