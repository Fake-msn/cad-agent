using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// Door heuristic: an Arc with radius in [minRadius, maxRadius] and a sweep
/// angle of ~90° (±sweepToleranceDeg) is annotated Door/GeometryInference/0.5.
/// ACadSharp 3.8.35 Arc.StartAngle/EndAngle are in radians (verified against
/// the package XML docs); sweep = (End - Start) normalized to [0,360), which
/// handles arcs wrapping across 0° (e.g. 350°→80° yields 90°).
/// Existing annotations with confidence &gt; 0.5 (GeometryInference 0.6/0.8,
/// HumanConfirmed 1.0) are never overwritten.
/// </summary>
public static class DoorArcDetector
{
    private const double Confidence = 0.5;

    public static void Detect(DrawingSession session, SemanticOverlay overlay,
        double minRadius = 500, double maxRadius = 1500, double sweepToleranceDeg = 15)
    {
        foreach (var e in session.Doc.Entities)
        {
            if (e is not Arc arc) continue;
            if (arc.Radius < minRadius || arc.Radius > maxRadius) continue;

            double sweep = (arc.EndAngle - arc.StartAngle) * (180.0 / Math.PI);
            sweep = ((sweep % 360.0) + 360.0) % 360.0; // normalize to [0,360), wrap across 0
            if (Math.Abs(sweep - 90.0) > sweepToleranceDeg) continue;

            string evidence = $"门弧 r={arc.Radius:F0} sweep={sweep:F0}°";
            if (overlay.Annotations.Any(x => x.TargetHandle == e.Handle.ToString() && x.Confidence > Confidence))
                continue;
            overlay.UpsertAnnotation(new SemanticAnnotation(
                e.Handle.ToString(), SemanticClass.Door, Provenance.GeometryInference, Confidence, evidence));
        }
    }
}