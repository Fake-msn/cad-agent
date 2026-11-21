using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Data;

namespace CadAgent.View.Rendering;

/// <summary>Flat render primitive kinds produced by <see cref="RenderModelBuilder"/>.</summary>
public enum RenderKind { Line, Circle, Arc, Polyline, Text }

/// <summary>
/// One flat render primitive. Point conventions (all coordinates already transformed
/// into drawing space by the builder):
/// Line: 2 endpoints. Circle: Points[0]=center, Points[1]=(radius,0).
/// Arc: Points[0]=center, Points[1]=(radius,startDeg), Points[2]=(endDeg,0) — angles in
/// degrees, CCW from +X, normalized to [0,360). Polyline: vertex sequence (closed
/// polylines repeat the first vertex at the end). Text: Points[0]=insertion point,
/// plus <see cref="Text"/>, <see cref="TextHeight"/> and <see cref="RotationDeg"/>.
/// <see cref="ColorRgb"/> is 0xRRGGBB (ByLayer resolved to the layer color).
/// </summary>
public sealed record RenderItem(
    RenderKind Kind,
    IReadOnlyList<(double X, double Y)> Points,
    string Layer,
    uint ColorRgb,
    string? Text,
    double TextHeight,
    double RotationDeg);

/// <summary>
/// DrawingSession -> flat render-item list (plan 4 task 2). Pure logic: no Avalonia
/// dependency, safe to unit-test headless. Supported entities: Line, Circle, Arc,
/// LwPolyline, Polyline2D, TextEntity, MText, Insert (block expansion). Anything
/// else is skipped and counted via the <c>out skippedCount</c> overload.
/// ACadSharp 3.8.35 API verified via package XML docs + runtime probe:
/// Insert.Block (BlockRecord) / InsertPoint / XScale / YScale / Rotation (radians);
/// Arc.StartAngle / EndAngle (radians); LwPolyline.Vertices (Vertex.Location XY);
/// Polyline2D.Vertices (Vertex2D.Location XYZ); Color.Index (short, 256=ByLayer,
/// -1=true color) / IsTrueColor / R / G / B; BlockRecord.BlockEntity.BasePoint.
/// </summary>
public static class RenderModelBuilder
{
    /// <summary>
    /// Standard AutoCAD color index (ACI) table, first 10 entries (0=ByBlock/black,
    /// 1 red, 2 yellow, 3 green, 4 cyan, 5 blue, 6 magenta, 7 white, 8 dark gray,
    /// 9 light gray). Indices outside the table fall back to white (brief convention;
    /// ACadSharp's Color.GetIndexRGB covers the full 256-entry table if richer
    /// fidelity is needed later).
    /// </summary>
    private static readonly uint[] AciRgb =
    {
        0x000000, 0xFF0000, 0xFFFF00, 0x00FF00, 0x00FFFF,
        0x0000FF, 0xFF00FF, 0xFFFFFF, 0x808080, 0xC0C0C0
    };

    private const uint FallbackWhite = 0xFFFFFF;

    /// <summary>
    /// Depth guard: a top-level Insert expands its block (depth 0 -> children at
    /// depth 1); an Insert inside that block expands ONE more level (depth 1 ->
    /// depth 2); any Insert at depth 2 is skipped and counted. Prevents stack
    /// overflow on deeply nested / self-referential block chains.
    /// </summary>
    private const int MaxInsertDepth = 2;

    /// <summary>Builds the flat render list; unsupported entities are skipped silently.</summary>
    public static IReadOnlyList<RenderItem> Build(DrawingSession session) => Build(session, out _);

    /// <summary>
    /// Builds the flat render list. <paramref name="skippedCount"/> reports entities
    /// the builder does not support (unknown types, unsupported primitives, and
    /// Inserts beyond the depth guard).
    /// </summary>
    public static IReadOnlyList<RenderItem> Build(DrawingSession session, out int skippedCount)
    {
        var items = new List<RenderItem>();
        int skipped = 0;
        foreach (Entity e in session.Doc.Entities)
            Append(e, items, ref skipped, Affine.Identity, rotationDeg: 0, heightScale: 1, byBlockColor: null, depth: 0);
        skippedCount = skipped;
        return items;
    }

    /// <summary>
    /// Axis-aligned bounds over the render list. Circle/Arc extents use center +/-
    /// radius (arc box is conservative: full circle, exact arc-extent math deferred
    /// to the canvas task); Text contributes its insertion point only (glyph metrics
    /// unavailable here). Empty list -> (0,0,1,1) unit fallback.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<RenderItem> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Expand(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (RenderItem item in items)
        {
            if (item.Kind is RenderKind.Circle or RenderKind.Arc)
            {
                // Points[1..] encode radius/angles, NOT coordinates.
                if (item.Points.Count < 2) continue;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                Expand(cx - r, cy - r);
                Expand(cx + r, cy + r);
            }
            else
            {
                foreach ((double x, double y) in item.Points) Expand(x, y);
            }
        }

        return minX > maxX || minY > maxY ? (0, 0, 1, 1) : (minX, minY, maxX, maxY);
    }

    private static void Append(
        Entity e, List<RenderItem> items, ref int skipped,
        Affine t, double rotationDeg, double heightScale, uint? byBlockColor, int depth)
    {
        string layer = e.Layer?.Name ?? "0";
        uint color = ResolveColor(e, byBlockColor);

        switch (e)
        {
            case Line line:
                items.Add(new RenderItem(RenderKind.Line,
                    new[] { t.Apply(line.StartPoint.X, line.StartPoint.Y), t.Apply(line.EndPoint.X, line.EndPoint.Y) },
                    layer, color, null, 0, 0));
                break;

            // Arc : Circle — must precede the Circle case.
            case Arc arc:
            {
                (double cx, double cy) = t.Apply(arc.Center.X, arc.Center.Y);
                double r = arc.Radius * t.RadiusScale;
                items.Add(new RenderItem(RenderKind.Arc,
                    new[] { (cx, cy), (r, NormDeg(ToDeg(arc.StartAngle) + rotationDeg)), (NormDeg(ToDeg(arc.EndAngle) + rotationDeg), 0.0) },
                    layer, color, null, 0, 0));
                break;
            }

            case Circle circle:
            {
                (double cx, double cy) = t.Apply(circle.Center.X, circle.Center.Y);
                double r = circle.Radius * t.RadiusScale;
                items.Add(new RenderItem(RenderKind.Circle,
                    new[] { (cx, cy), (r, 0.0) },
                    layer, color, null, 0, 0));
                break;
            }

            case LwPolyline lw:
            {
                var pts = new List<(double X, double Y)>(lw.Vertices.Count + 1);
                foreach (LwPolyline.Vertex v in lw.Vertices) pts.Add(t.Apply(v.Location.X, v.Location.Y));
                if (lw.IsClosed && pts.Count > 0) pts.Add(pts[0]);
                // Bulge (arc segments) approximated as straight chords — canvas task
                // may tessellate later.
                items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0));
                break;
            }

            case Polyline2D p2:
            {
                var pts = new List<(double X, double Y)>(p2.Vertices.Count + 1);
                foreach (Vertex2D v in p2.Vertices) pts.Add(t.Apply(v.Location.X, v.Location.Y));
                if (p2.IsClosed && pts.Count > 0) pts.Add(pts[0]);
                items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0));
                break;
            }

            case TextEntity text:
                items.Add(new RenderItem(RenderKind.Text,
                    new[] { t.Apply(text.InsertPoint.X, text.InsertPoint.Y) },
                    layer, color, text.Value, text.Height * heightScale, NormDeg(ToDeg(text.Rotation) + rotationDeg)));
                break;

            case MText mtext:
                // PlainText strips inline formatting codes (\P, \f, ...); MText.Rotation
                // is read-only (derived from the direction vector).
                items.Add(new RenderItem(RenderKind.Text,
                    new[] { t.Apply(mtext.InsertPoint.X, mtext.InsertPoint.Y) },
                    layer, color, mtext.PlainText, mtext.Height * heightScale, NormDeg(ToDeg(mtext.Rotation) + rotationDeg)));
                break;

            case Insert insert:
            {
                if (depth >= MaxInsertDepth || insert.Block is null)
                {
                    skipped++;
                    break;
                }
                Affine composed = t.Compose(InsertTransform(insert));
                double rot = rotationDeg + ToDeg(insert.Rotation);
                double hs = heightScale * Math.Abs(insert.YScale);
                foreach (Entity child in insert.Block.Entities)
                    Append(child, items, ref skipped, composed, rot, hs, color, depth + 1);
                break;
            }

            default:
                skipped++;
                break;
        }
    }

    /// <summary>
    /// Local insert transform: p' = R(rotation) * S(xScale,yScale) * (p - blockBasePoint)
    /// + insertPoint. Scale is applied BEFORE rotation (matches Insert.GetTransform's
    /// matrix, verified by probe). The block base point is subtracted first so blocks
    /// authored around a non-zero origin land correctly.
    /// </summary>
    private static Affine InsertTransform(Insert insert)
    {
        double cos = Math.Cos(insert.Rotation), sin = Math.Sin(insert.Rotation);
        double a = cos * insert.XScale, b = -sin * insert.YScale;
        double c = sin * insert.XScale, d = cos * insert.YScale;
        CSMath.XYZ? bp = insert.Block?.BlockEntity?.BasePoint;
        double bx = bp?.X ?? 0, by = bp?.Y ?? 0;
        double tx = insert.InsertPoint.X - (a * bx + b * by);
        double ty = insert.InsertPoint.Y - (c * bx + d * by);
        return new Affine(a, b, c, d, tx, ty);
    }

    private static uint ResolveColor(Entity e, uint? byBlockColor)
    {
        Color c = e.Color;
        if (c.IsByLayer) return LayerColor(e.Layer);
        // ByBlock inside an expansion inherits the Insert's resolved color; at top
        // level (no parent insert) fall back to the layer color.
        if (c.IsByBlock) return byBlockColor ?? LayerColor(e.Layer);
        return ToRgb(c);
    }

    private static uint LayerColor(ACadSharp.Tables.Layer? layer) => layer is null ? FallbackWhite : ToRgb(layer.Color);

    private static uint ToRgb(Color c)
    {
        if (c.IsTrueColor) return ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        int idx = c.Index;
        return idx >= 0 && idx < AciRgb.Length ? AciRgb[idx] : FallbackWhite;
    }

    private static double ToDeg(double radians) => radians * 180.0 / Math.PI;

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;

    /// <summary>
    /// 2D affine map x' = A*x + B*y + Tx, y' = C*x + D*y + Ty. Composed through
    /// nested insert expansion so block geometry lands in drawing space.
    /// </summary>
    private readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);

        /// <summary>this(inner(p)) — inner transform applied first, then this one.</summary>
        public Affine Compose(Affine inner) => new(
            A * inner.A + B * inner.C,
            A * inner.B + B * inner.D,
            C * inner.A + D * inner.C,
            C * inner.B + D * inner.D,
            A * inner.Tx + B * inner.Ty + Tx,
            C * inner.Tx + D * inner.Ty + Ty);

        /// <summary>
        /// Radius scale approximation: geometric mean of the column norms (exact
        /// singular values for a single R*S; circles under non-uniform scale become
        /// ellipses in true CAD — approximated here, documented limitation).
        /// </summary>
        public double RadiusScale => Math.Sqrt(Math.Sqrt(A * A + C * C) * Math.Sqrt(B * B + D * D));
    }
}
