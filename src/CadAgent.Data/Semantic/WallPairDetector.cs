using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// Wall heuristic: pairs of parallel lines spaced at wall thickness with
/// sufficient projection overlap are annotated Wall/GeometryInference/0.6.
/// O(n²) per direction bucket — spatial indexing is deferred to a later slice.
/// Existing annotations with confidence &gt; 0.6 (HumanConfirmed 1.0,
/// LayerInference 0.8) are never overwritten.
/// </summary>
public static class WallPairDetector
{
    private const double AngleToleranceDeg = 1.0;

    public static void Detect(DrawingSession session, SemanticOverlay overlay,
        double minThickness = 100, double maxThickness = 400, double minOverlap = 500)
    {
        var lines = CollectLines(session);
        foreach (var bucket in BucketByAngle(lines))
            DetectInBucket(bucket, overlay, minThickness, maxThickness, minOverlap);
    }

    private sealed record Segment(
        string Handle, double X1, double Y1, double X2, double Y2,
        double Ux, double Uy, double Angle);

    /// <summary>
    /// Collect Line entities with a normalized direction and an angle in [0,180).
    /// Zero-length lines have no direction and are skipped.
    /// </summary>
    private static List<Segment> CollectLines(DrawingSession session)
    {
        var result = new List<Segment>();
        foreach (var e in session.Doc.Entities)
        {
            if (e is not Line line) continue;
            double dx = line.EndPoint.X - line.StartPoint.X;
            double dy = line.EndPoint.Y - line.StartPoint.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            double ux = dx / len, uy = dy / len;
            double angle = Math.Atan2(dy, dx) * (180.0 / Math.PI);
            if (angle < 0) angle += 180.0;
            if (angle >= 180.0) angle -= 180.0; // Atan2(0,-x)=180 wraps to 0
            result.Add(new Segment(e.Handle.ToString(),
                line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y,
                ux, uy, angle));
        }
        return result;
    }

    /// <summary>
    /// Sort by angle and merge adjacent lines within 1°; the buckets touching
    /// 0° and 180° are merged as well (direction is mod 180).
    /// </summary>
    private static List<List<Segment>> BucketByAngle(List<Segment> lines)
    {
        var buckets = new List<List<Segment>>();
        foreach (var s in lines.OrderBy(s => s.Angle))
        {
            if (buckets.Count == 0 || s.Angle - buckets[^1][^1].Angle > AngleToleranceDeg)
                buckets.Add(new List<Segment> { s });
            else
                buckets[^1].Add(s);
        }
        if (buckets.Count > 1)
        {
            var first = buckets[0];
            var last = buckets[^1];
            if (first[0].Angle + 180.0 - last[^1].Angle <= AngleToleranceDeg)
            {
                first.AddRange(last);
                buckets.RemoveAt(buckets.Count - 1);
            }
        }
        return buckets;
    }

    private static void DetectInBucket(List<Segment> bucket, SemanticOverlay overlay,
        double minThickness, double maxThickness, double minOverlap)
    {
        for (int i = 0; i < bucket.Count; i++)
        for (int j = i + 1; j < bucket.Count; j++)
        {
            var a = bucket[i];
            var b = bucket[j];
            // Parallel within 1° mod 180 (bucket chaining may drift, re-check pairwise).
            double diff = Math.Abs(a.Angle - b.Angle);
            if (diff > AngleToleranceDeg && diff < 180.0 - AngleToleranceDeg) continue;

            // Perpendicular distance: |cross(unitDirA, B.start - A.start)|.
            double d = Math.Abs(a.Ux * (b.Y1 - a.Y1) - a.Uy * (b.X1 - a.X1));
            if (d < minThickness || d > maxThickness) continue;

            // Overlap = intersection length of the projections on A's direction axis.
            double a0 = a.Ux * a.X1 + a.Uy * a.Y1;
            double a1 = a.Ux * a.X2 + a.Uy * a.Y2;
            double b0 = a.Ux * b.X1 + a.Uy * b.Y1;
            double b1 = a.Ux * b.X2 + a.Uy * b.Y2;
            double overlap = Math.Min(Math.Max(a0, a1), Math.Max(b0, b1))
                           - Math.Max(Math.Min(a0, a1), Math.Min(b0, b1));
            if (overlap < minOverlap) continue;

            string evidence = $"平行线对 d={d:F0} overlap={overlap:F0}";
            Annotate(overlay, a.Handle, evidence);
            Annotate(overlay, b.Handle, evidence);
        }
    }

    private static void Annotate(SemanticOverlay overlay, string handle, string evidence)
    {
        if (overlay.Annotations.Any(x => x.TargetHandle == handle && x.Confidence > 0.6)) return;
        overlay.UpsertAnnotation(new SemanticAnnotation(
            handle, SemanticClass.Wall, Provenance.GeometryInference, 0.6, evidence));
    }
}