using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core.Cad;
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
/// <see cref="Handle"/> is the source entity's DXF handle as the repo-wide decimal
/// string (e.Handle.ToString(), same convention as audit/semantic TargetHandle).
/// Insert-expanded children carry the parent Insert's handle plus a child-index
/// suffix "{insertHandle}:{childIndex}" (nested: "{top}:{i}:{j}") for uniqueness.
/// </summary>
public sealed record RenderItem(
    RenderKind Kind,
    IReadOnlyList<(double X, double Y)> Points,
    string Layer,
    uint ColorRgb,
    string? Text,
    double TextHeight,
    double RotationDeg,
    string Handle);

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
    /// Fallback for ACI indices outside the valid 0..255 range (brief convention).
    /// Valid indices are resolved through ACadSharp's Color.GetIndexRGB, which
    /// covers the full 256-entry standard AutoCAD color index table.
    /// </summary>
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
    /// 布局视口读图（幅面感知 T5）：模型空间实体经视口模型区域裁剪 + 映射到纸面坐标。
    /// 视口模型区域 = ViewTarget ± (ViewHeight/2 × aspect, ViewHeight/2)，aspect=纸面视口宽高比
    /// (Width/Height)；锚点（实体首点，与 SheetIdForPoint/BlockWalker 同口径）落在区域内者入视口。
    /// 映射仿射 = 缩放 s(paperHeight/ViewHeight) + 平移(ViewTarget→Center)，与 Core
    /// ViewportTransform.ModelToPaper 同一数学；复用既有 Append 管线（字高/半径随 s 缩放，
    /// Insert 经 InsertAffine 复合展开，句柄口径不变）。TwistAngle/非矩形裁剪暂不参与（文档化局限）。
    /// </summary>
    public static IReadOnlyList<RenderItem> BuildViewport(CadDocument doc, Viewport vp)
    {
        var items = new List<RenderItem>();
        int skipped = 0;
        double viewHeight = vp.ViewHeight;
        double paperHeight = vp.Height;
        double s = viewHeight > 1e-12 ? paperHeight / viewHeight : 1;
        double aspect = paperHeight > 1e-12 ? vp.Width / paperHeight : 1;
        double halfH = viewHeight / 2;
        double halfW = halfH * aspect;
        double tx = vp.ViewTarget.X, ty = vp.ViewTarget.Y;
        double minX = tx - halfW, maxX = tx + halfW, minY = ty - halfH, maxY = ty + halfH;
        // 模型→纸面：p' = s·(p - ViewTarget) + Center（缩放+平移复合 Core Affine）
        var t = new Affine(s, 0, 0, s, vp.Center.X - tx * s, vp.Center.Y - ty * s);
        foreach (Entity e in doc.Entities)
        {
            (double ax, double ay) = AnchorOf(e);
            if (ax < minX || ax > maxX || ay < minY || ay > maxY) continue; // 锚点区域外→不入视口
            Append(e, items, ref skipped, t, rotationDeg: 0, heightScale: s, byBlockColor: null, depth: 0);
        }
        return items;
    }

    /// <summary>
    /// 模型幅面裁剪（幅面感知 T5）：保留锚点（Points[0]）落在图框 bounds 内的图元，
    /// 与 SheetIdForPoint/BlockWalker 锚点归属同口径。无图框单幅面 bounds=ModelBounds →
    /// 全部锚点在内 → 全保留（legacy 单幅面行为无感，R4）。
    /// </summary>
    public static IReadOnlyList<RenderItem> CropToFrame(
        IReadOnlyList<RenderItem> items, (double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        var result = new List<RenderItem>();
        foreach (RenderItem item in items)
        {
            if (item.Points.Count == 0) continue;
            (double x, double y) = item.Points[0];
            if (x >= bounds.MinX && x <= bounds.MaxX && y >= bounds.MinY && y <= bounds.MaxY)
                result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// 瓦片级几何相交裁剪（视觉接线 T3，R6）：保留几何 bounds 与 rect 相交的图元——
    /// 穿越瓦片的长导线锚点在瓦片外但几何穿过，锚点裁剪（CropToFrame）会整体缺失
    /// （vision 电缆可见性缺口），相交裁剪保留。加性 public，不改 CropToFrame 锚点语义
    /// （幅面级裁剪/幅面归属仍锚点口径）。Circle/Arc bounds=圆心±半径（Points[1..] 是
    /// 半径/角度元数据非坐标，与 Bounds 同口径）；Text 仅插入点（无字形度量）。
    /// 边界相接算相交（闭区间，与 CropToFrame 含边界同口径）。
    /// </summary>
    public static IReadOnlyList<RenderItem> CropToFrameIntersect(
        IReadOnlyList<RenderItem> items, (double MinX, double MinY, double MaxX, double MaxY) rect)
    {
        var result = new List<RenderItem>();
        foreach (RenderItem item in items)
        {
            if (item.Points.Count == 0) continue;
            double minX, minY, maxX, maxY;
            if (item.Kind is RenderKind.Circle or RenderKind.Arc)
            {
                if (item.Points.Count < 2) continue;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                minX = cx - r; maxX = cx + r; minY = cy - r; maxY = cy + r;
            }
            else
            {
                minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
                foreach ((double x, double y) in item.Points)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
            if (maxX >= rect.MinX && minX <= rect.MaxX && maxY >= rect.MinY && minY <= rect.MaxY)
                result.Add(item);
        }
        return result;
    }

    /// <summary>实体锚点（模型空间首点）：Line=起点，Polyline=首顶点，Text/MText=插入点，
    /// Circle/Arc=圆心，Insert=插入点（与 BlockWalker.AnchorOf 同口径）。</summary>
    private static (double X, double Y) AnchorOf(Entity e) => e switch
    {
        Line l => (l.StartPoint.X, l.StartPoint.Y),
        LwPolyline lw when lw.Vertices.Count > 0 => (lw.Vertices[0].Location.X, lw.Vertices[0].Location.Y),
        Polyline2D p2 when p2.Vertices.Count > 0 => (p2.Vertices[0].Location.X, p2.Vertices[0].Location.Y),
        TextEntity t => (t.InsertPoint.X, t.InsertPoint.Y),
        MText mt => (mt.InsertPoint.X, mt.InsertPoint.Y),
        Circle c => (c.Center.X, c.Center.Y), // Arc : Circle，同取圆心
        Insert ins => (ins.InsertPoint.X, ins.InsertPoint.Y),
        _ => (0, 0)
    };

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
        Affine t, double rotationDeg, double heightScale, uint? byBlockColor, int depth,
        string? handle = null)
    {
        string layer = e.Layer?.Name ?? "0";
        uint color = ResolveColor(e, byBlockColor);
        // Top-level entities use their own DXF handle; insert children receive the
        // parent insert's handle + child-index suffix (computed in the Insert case).
        handle ??= e.Handle.ToString(CultureInfo.InvariantCulture);

        switch (e)
        {
            case Line line:
                items.Add(new RenderItem(RenderKind.Line,
                    new[] { t.Apply(line.StartPoint.X, line.StartPoint.Y), t.Apply(line.EndPoint.X, line.EndPoint.Y) },
                    layer, color, null, 0, 0, handle));
                break;

            // Arc : Circle — must precede the Circle case.
            case Arc arc:
            {
                (double cx, double cy) = t.Apply(arc.Center.X, arc.Center.Y);
                double r = arc.Radius * t.RadiusScale;
                items.Add(new RenderItem(RenderKind.Arc,
                    new[] { (cx, cy), (r, NormDeg(ToDeg(arc.StartAngle) + rotationDeg)), (NormDeg(ToDeg(arc.EndAngle) + rotationDeg), 0.0) },
                    layer, color, null, 0, 0, handle));
                break;
            }

            case Circle circle:
            {
                (double cx, double cy) = t.Apply(circle.Center.X, circle.Center.Y);
                double r = circle.Radius * t.RadiusScale;
                items.Add(new RenderItem(RenderKind.Circle,
                    new[] { (cx, cy), (r, 0.0) },
                    layer, color, null, 0, 0, handle));
                break;
            }

            case LwPolyline lw:
            {
                var pts = new List<(double X, double Y)>(lw.Vertices.Count + 1);
                foreach (LwPolyline.Vertex v in lw.Vertices) pts.Add(t.Apply(v.Location.X, v.Location.Y));
                if (lw.IsClosed && pts.Count > 0) pts.Add(pts[0]);
                // Bulge (arc segments) approximated as straight chords — canvas task
                // may tessellate later.
                items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0, handle));
                break;
            }

            case Polyline2D p2:
            {
                var pts = new List<(double X, double Y)>(p2.Vertices.Count + 1);
                foreach (Vertex2D v in p2.Vertices) pts.Add(t.Apply(v.Location.X, v.Location.Y));
                if (p2.IsClosed && pts.Count > 0) pts.Add(pts[0]);
                items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0, handle));
                break;
            }

            case TextEntity text:
                items.Add(new RenderItem(RenderKind.Text,
                    new[] { t.Apply(text.InsertPoint.X, text.InsertPoint.Y) },
                    layer, color, text.Value, text.Height * heightScale, NormDeg(ToDeg(text.Rotation) + rotationDeg), handle));
                break;

            case MText mtext:
                // PlainText strips inline formatting codes (\P, \f, ...); MText.Rotation
                // is read-only (derived from the direction vector).
                items.Add(new RenderItem(RenderKind.Text,
                    new[] { t.Apply(mtext.InsertPoint.X, mtext.InsertPoint.Y) },
                    layer, color, mtext.PlainText, mtext.Height * heightScale, NormDeg(ToDeg(mtext.Rotation) + rotationDeg), handle));
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
                int childIndex = 0;
                foreach (Entity child in insert.Block.Entities)
                {
                    Append(child, items, ref skipped, composed, rot, hs, color, depth + 1,
                        handle + ":" + childIndex.ToString(CultureInfo.InvariantCulture));
                    childIndex++;
                }
                break;
            }

            default:
                skipped++;
                break;
        }
    }

    /// <summary>
    /// Local insert transform: p' = R(rotation) * S(xScale,yScale) * (p - blockBasePoint)
    /// + insertPoint. R1 数学 consolidation：实现移入 Core（Affine.InsertAffine），此处委托。
    /// </summary>
    private static Affine InsertTransform(Insert insert) => Affine.InsertAffine(insert);

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
        if (idx < 0 || idx > 255) return FallbackWhite;
        // ACadSharp Color.GetIndexRGB(byte) returns ReadOnlySpan<byte> {R,G,B}
        // from the full 256-entry standard ACI table (verified via package XML docs).
        ReadOnlySpan<byte> rgb = Color.GetIndexRGB((byte)idx);
        return ((uint)rgb[0] << 16) | ((uint)rgb[1] << 8) | rgb[2];
    }

    private static double ToDeg(double radians) => radians * 180.0 / Math.PI;

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;
}
