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
    {
        var texts = new List<(string Handle, double X, double Y, string Value)>();
        foreach (var e in session.Doc.Entities)
        {
            if (e is not TextEntity text || string.IsNullOrEmpty(text.Value)) continue;
            string value = text.Value.Trim();
            if (!NumberPattern.IsMatch(value)) continue;
            texts.Add((e.Handle.ToString(), text.InsertPoint.X, text.InsertPoint.Y, value));
        }
        if (texts.Count == 0) return;

        foreach (var e in session.Doc.Entities)
        {
            if (e is not Circle circle || e is Arc) continue;
            if (circle.Radius < minRadius || circle.Radius > maxRadius) continue;

            // Nearest qualifying number text within radius×1.5 (2D distance).
            double maxDist = circle.Radius * 1.5;
            (string Handle, double X, double Y, string Value)? best = null;
            double bestDist = double.MaxValue;
            foreach (var t in texts)
            {
                double dx = t.X - circle.Center.X;
                double dy = t.Y - circle.Center.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= maxDist && dist < bestDist)
                {
                    best = t;
                    bestDist = dist;
                }
            }
            if (best is null) continue;

            string evidence = $"轴号圆圈 r={circle.Radius:F0} 编号={best.Value.Value}";
            Annotate(overlay, e.Handle.ToString(), evidence);
            Annotate(overlay, best.Value.Handle, evidence);
        }
    }

    private static void Annotate(SemanticOverlay overlay, string handle, string evidence)
    {
        if (overlay.Annotations.Any(x => x.TargetHandle == handle && x.Confidence > Confidence)) return;
        overlay.UpsertAnnotation(new SemanticAnnotation(
            handle, SemanticClass.Axis, Provenance.GeometryInference, Confidence, evidence));
    }
}
