using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// Door heuristic: an Arc with radius in [minRadius, maxRadius] and a sweep
/// angle of ~90° (±sweepToleranceDeg) is annotated Door/GeometryInference/0.5.
/// ACadSharp 3.8.35 Arc.StartAngle/EndAngle are in radians (verified against
/// the package XML docs); sweep = (End - Start) normalized to [0,360), which
/// handles arcs wrapping across 0° (e.g. 350°→80° yields 90°).
/// Existing annotations with confidence &gt; 0.5 (GeometryInference 0.6,
/// LayerInference 0.8, HumanConfirmed 1.0) are never overwritten.
/// </summary>
public static class DoorArcDetector
{
    private const double Confidence = 0.5;

    public static void Detect(DrawingSession session, SemanticOverlay overlay,
        double minRadius = 500, double maxRadius = 1500, double sweepToleranceDeg = 15)
        => DetectWalked(BlockWalker.Walk(session.Doc, session.Sheets).ToList(), overlay,
            minRadius, maxRadius, sweepToleranceDeg);

    /// <summary>
    /// 块遍历口径核心（扫描器复用同一次 Walk）：半径用世界值（WorldPoints[1].X，
    /// Walk 内已按 RadiusScale 缩放）；扫角取本地 Start/EndAngle——旋转/均匀缩放不改变扫角。
    /// </summary>
    internal static void DetectWalked(IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay,
        double minRadius = 500, double maxRadius = 1500, double sweepToleranceDeg = 15)
    {
        foreach (var w in walked)
        {
            if (w.Entity is not Arc arc) continue;
            double radius = w.WorldPoints.Count >= 2 ? w.WorldPoints[1].X : arc.Radius;
            if (radius < minRadius || radius > maxRadius) continue;

            double sweep = (arc.EndAngle - arc.StartAngle) * (180.0 / Math.PI);
            sweep = ((sweep % 360.0) + 360.0) % 360.0; // normalize to [0,360), wrap across 0
            if (Math.Abs(sweep - 90.0) > sweepToleranceDeg) continue;

            string evidence = $"门弧 r={radius:F0} sweep={sweep:F0}°";
            if (overlay.Annotations.Any(x => x.TargetHandle == w.HandlePath && x.Confidence > Confidence))
                continue;
            overlay.UpsertAnnotation(new SemanticAnnotation(
                w.HandlePath, SemanticClass.Door, Provenance.GeometryInference, Confidence, evidence));
        }
    }
}
