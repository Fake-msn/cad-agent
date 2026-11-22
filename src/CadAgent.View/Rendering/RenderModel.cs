using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Data;
using CSMath;

namespace CadAgent.View.Rendering;

/// <summary>Flat render primitive kinds produced by <see cref="RenderModelBuilder"/>.</summary>
public enum RenderKind { Line, Circle, Arc, Polyline, Text, Hatch }

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
/// <see cref="Closed"/> 仅对 Polyline 有意义：true 表示画布须将末点连回首点闭合
/// （整圆 tessellation 产 72 唯一点、不重复首点，靠此标志闭合）；既有闭合多段线
/// （LwPolyline.IsClosed 重复首点）保持 Closed=false，重复首点自闭合，行为不变。
/// Hatch: Points 为各边界路径的顶点序列（GetPoints(16) 弧段插值），**各路径之间以
/// (NaN,NaN) 分隔**——画布按分隔符拆分 figure 并以 EvenOdd 填充规则跨路径挖岛；
/// 分隔符不是坐标，Bounds 等消费方必须跳过。
/// <see cref="DashPattern"/> 为线型虚实（Task 3）：虚线段长序列（drawing 单位
/// on/off 交替，LineTypeScale 已乘入；空/null=实线），画布 DrawItem 入口换算
/// DashStyle；<see cref="Fill"/> 仅对 Hatch 有意义：true=实心
/// 填充（IsSolid），false=仅描边界轮廓（图案填充的图案线生成 deferred）。
/// 可选参置于 Handle 之后（Closed→DashPattern→Fill 顺序追加，位置构造兼容）。
/// </summary>
public sealed record RenderItem(
    RenderKind Kind,
    IReadOnlyList<(double X, double Y)> Points,
    string Layer,
    uint ColorRgb,
    string? Text,
    double TextHeight,
    double RotationDeg,
    string Handle,
    bool Closed = false,
    IReadOnlyList<double>? DashPattern = null,
    bool Fill = false);

/// <summary>
/// DrawingSession -> flat render-item list (plan 4 task 2). Pure logic: no Avalonia
/// dependency, safe to unit-test headless. Supported entities: Line, Circle, Arc,
/// LwPolyline, Polyline2D, TextEntity, MText, Insert (block expansion), Dimension
/// (anonymous-block expansion), Hatch (boundary paths + solid fill). Anything
/// else is skipped and counted via the <c>out skippedCount</c> overload.
/// ACadSharp 3.8.35 API verified via package XML docs + runtime probe:
/// Insert.Block (BlockRecord) / InsertPoint / XScale / YScale / Rotation (radians);
/// Arc.StartAngle / EndAngle (radians); LwPolyline.Vertices (Vertex.Location XY);
/// Polyline2D.Vertices (Vertex2D.Location XYZ); Color.Index (short, 256=ByLayer,
/// -1=true color) / IsTrueColor / R / G / B; BlockRecord.BlockEntity.BasePoint.
/// OCS（任意轴算法，计划4 延迟项 Task 1）：非默认法向的 Circle/Arc/LwPolyline/
/// TextEntity/MText/Insert 经 OcsMapper 投影到 WCS XY；默认法向 (0,0,1) 走原路径。
/// Hatch 边界顶点同为 OCS 坐标，经同一 OcsMapper 投影（Task 2）。
/// 线型虚实（Task 3）：LineType 段→DashPattern（ByLayer 回退+LineTypeScale；
/// 复杂线型实线降级，shapes/text deferred）。
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
    /// overflow on deeply nested / self-referential block chains. Dimension
    /// block expansion shares the same guard (a Dimension inside a block may
    /// point back to an ancestor block — same overflow/exponential-DAG hazard).
    /// </summary>
    private const int MaxInsertDepth = 2;

    /// <summary>Builds the flat render list; unsupported entities are skipped silently.</summary>
    public static IReadOnlyList<RenderItem> Build(DrawingSession session) => Build(session, out _);

    /// <summary>
    /// Builds the flat render list. <paramref name="skippedCount"/> reports entities
    /// the builder does not support (unknown types, unsupported primitives, and
    /// Inserts/Dimensions beyond the depth guard), plus honest skips: Dimensions
    /// without a block (no generated geometry) and Hatches without boundary points.
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
                // Hatch 与 Polyline 同路径遍历顶点；Hatch 的 (NaN,NaN) 路径分隔符
                // 不是坐标，显式跳过（NaN 比较恒 false，守卫使约定显式化）。
                foreach ((double x, double y) in item.Points)
                {
                    if (double.IsNaN(x)) continue;
                    Expand(x, y);
                }
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
        // 线型虚实（Task 3）：入口统一解析一次，各产出 case 传入；Insert/Dimension
        // 子图元经递归 Append 各自解析（child 的 LineType/图层独立生效）。
        IReadOnlyList<double>? dash = ResolveDash(e);

        switch (e)
        {
            case Line line:
                // Line 端点为 WCS（DXF 约定：LINE 不受 OCS 法向影响）——不接 OCS。
                items.Add(new RenderItem(RenderKind.Line,
                    new[] { t.Apply(line.StartPoint.X, line.StartPoint.Y), t.Apply(line.EndPoint.X, line.EndPoint.Y) },
                    layer, color, null, 0, 0, handle, DashPattern: dash));
                break;

            // Arc : Circle — must precede the Circle case.
            case Arc arc:
            {
                var (ocs, ocsId) = OcsMapper(arc.Normal);
                if (!ocsId)
                {
                    // 非默认法向：OCS 内 tessellate 后逐点映射（椭圆投影诚实近似，72 段）。
                    // sweep 为 CCW 正角差；半径缩放由 AppendTessellatedArc 内 t.Apply 施加。
                    // 起始角不预加 rotationDeg：insert 旋转已含于 t，点集经 t.Apply
                    // 几何变换一次即可（预加会二次旋转——与 identity 路径的角度元数据
                    // 口径不同，见 Arc_MirroredNormalInsideRotatedInsert 测试）。
                    AppendTessellatedArc(items, layer, color, handle, t, ocs,
                        arc.Center.X, arc.Center.Y, arc.Radius,
                        ToDeg(arc.StartAngle), NormDeg(ToDeg(arc.EndAngle - arc.StartAngle)), dash);
                    break;
                }
                (double cx, double cy) = t.Apply(arc.Center.X, arc.Center.Y);
                double r = arc.Radius * t.RadiusScale;
                items.Add(new RenderItem(RenderKind.Arc,
                    new[] { (cx, cy), (r, NormDeg(ToDeg(arc.StartAngle) + rotationDeg)), (NormDeg(ToDeg(arc.EndAngle) + rotationDeg), 0.0) },
                    layer, color, null, 0, 0, handle, DashPattern: dash));
                break;
            }

            case Circle circle:
            {
                var (ocs, ocsId) = OcsMapper(circle.Normal);
                if (!ocsId)
                {
                    // 非默认法向：整圆 tessellation（startDeg 0 / sweep 360）。
                    AppendTessellatedArc(items, layer, color, handle, t, ocs,
                        circle.Center.X, circle.Center.Y, circle.Radius,
                        startDeg: 0, sweepDeg: 360, dash: dash);
                    break;
                }
                (double cx, double cy) = t.Apply(circle.Center.X, circle.Center.Y);
                double r = circle.Radius * t.RadiusScale;
                items.Add(new RenderItem(RenderKind.Circle,
                    new[] { (cx, cy), (r, 0.0) },
                    layer, color, null, 0, 0, handle, DashPattern: dash));
                break;
            }

            case LwPolyline lw:
            {
                // LwPolyline 顶点为 OCS 坐标：先 OCS→WCS XY 投影再走 insert 仿射。
                // 默认法向时 ocs 为 identity，双 Apply 恒等于原路径（无分支，行为保持）。
                var (ocs, _) = OcsMapper(lw.Normal);
                var pts = new List<(double X, double Y)>(lw.Vertices.Count + 1);
                foreach (LwPolyline.Vertex v in lw.Vertices)
                {
                    (double ox, double oy) = ocs.Apply(v.Location.X, v.Location.Y);
                    pts.Add(t.Apply(ox, oy));
                }
                if (lw.IsClosed && pts.Count > 0) pts.Add(pts[0]);
                // Bulge (arc segments) approximated as straight chords — canvas task
                // may tessellate later.
                items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0, handle, DashPattern: dash));
                break;
            }

            case Polyline2D p2:
            {
                // Vertex2D.Location 为 WCS（3D 多段线约定）——不接 OCS。
                var pts = new List<(double X, double Y)>(p2.Vertices.Count + 1);
                foreach (Vertex2D v in p2.Vertices) pts.Add(t.Apply(v.Location.X, v.Location.Y));
                if (p2.IsClosed && pts.Count > 0) pts.Add(pts[0]);
                items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0, handle, DashPattern: dash));
                break;
            }

            case TextEntity text:
            {
                // 插入点为 OCS 坐标；旋转取 OCS 方向向量经映射后的投影角。
                var (ocs, _) = OcsMapper(text.Normal);
                (double ox, double oy) = ocs.Apply(text.InsertPoint.X, text.InsertPoint.Y);
                items.Add(new RenderItem(RenderKind.Text,
                    new[] { t.Apply(ox, oy) },
                    layer, color, text.Value, text.Height * heightScale, NormDeg(OcsRotation(ToDeg(text.Rotation), ocs) + rotationDeg), handle, DashPattern: dash));
                break;
            }

            case MText mtext:
            {
                // PlainText strips inline formatting codes (\P, \f, ...); MText.Rotation
                // is read-only (derived from the direction vector). OCS 处理同 TextEntity。
                var (ocs, _) = OcsMapper(mtext.Normal);
                (double ox, double oy) = ocs.Apply(mtext.InsertPoint.X, mtext.InsertPoint.Y);
                items.Add(new RenderItem(RenderKind.Text,
                    new[] { t.Apply(ox, oy) },
                    layer, color, mtext.PlainText, mtext.Height * heightScale, NormDeg(OcsRotation(ToDeg(mtext.Rotation), ocs) + rotationDeg), handle, DashPattern: dash));
                break;
            }

            case Insert insert:
            {
                if (depth >= MaxInsertDepth || insert.Block is null)
                {
                    skipped++;
                    break;
                }
                // DXF INSERT 插入点为 OCS 坐标（ACadSharp 往返保持原值，不转 WCS）：
                // canonical 变换 = M·T(ip)·R·S —— ocs 包在 InsertTransform（含 OCS
                // 插入点平移）之外，再与父变换 t 复合。镜像法向（如 (0,0,-1)）精确
                // （插入点与块内容一并镜像）；倾斜法向 = XY 投影近似（丢弃 WCS Z）。
                // identity 时 Compose 为精确恒等，行为保持。
                var (ocs, _) = OcsMapper(insert.Normal);
                Affine composed = t.Compose(ocs.Compose(InsertTransform(insert)));
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

            case Dimension dim:
            {
                // 尺寸标注经匿名块（*Dnn）展开——块内即厂商绘制的标注几何（延伸线/尺寸线/箭头/文本），
                // 探针核定 DXF 往返保留。块缺失（未生成标注几何）→诚实 skip+计数。
                // 深度守卫与 Insert 对齐：病态 DXF 中块内 Dimension 可回指祖先块（自指环）
                // →无限递归 StackOverflow；共享块 Dimension DAG 可指数展开。顶层 depth=0 不受影响。
                if (depth >= MaxInsertDepth || dim.Block is null) { skipped++; break; }
                int childIndex = 0;
                foreach (Entity child in dim.Block.Entities)
                    Append(child, items, ref skipped, t, rotationDeg, heightScale, byBlockColor, depth + 1,
                        handle + ":" + childIndex++.ToString(CultureInfo.InvariantCulture));
                break;
            }

            case Hatch hatch:
            {
                var (ocs, _) = OcsMapper(hatch.Normal);
                var pts = new List<(double X, double Y)>();
                foreach (Hatch.BoundaryPath path in hatch.Paths)
                {
                    if (pts.Count > 0) pts.Add((double.NaN, double.NaN)); // 路径分隔（EvenOdd 挖岛）
                    foreach (XY p in path.GetPoints(16)) // 弧段 16 点插值（探针核定）
                    {
                        // 边界顶点为 OCS 坐标：先 OCS→WCS XY 投影再走父仿射（同 LwPolyline 双 Apply）。
                        (double ox, double oy) = ocs.Apply(p.X, p.Y);
                        pts.Add(t.Apply(ox, oy));
                    }
                }
                if (pts.Count == 0) { skipped++; break; }
                // 图案填充（非 solid）渲染边界轮廓；图案线生成 deferred（spec 注记，不假装）
                items.Add(new RenderItem(RenderKind.Hatch, pts, layer, color, null, 0, 0, handle, DashPattern: dash, Fill: hatch.IsSolid));
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
        if (idx < 0 || idx > 255) return FallbackWhite;
        // ACadSharp Color.GetIndexRGB(byte) returns ReadOnlySpan<byte> {R,G,B}
        // from the full 256-entry standard ACI table (verified via package XML docs).
        ReadOnlySpan<byte> rgb = Color.GetIndexRGB((byte)idx);
        return ((uint)rgb[0] << 16) | ((uint)rgb[1] << 8) | rgb[2];
    }

    private static double ToDeg(double radians) => radians * 180.0 / Math.PI;

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;

    /// <summary>
    /// OCS→WCS XY 投影（AutoCAD 任意轴算法）：法向接近 +Z 时 identity 快路径；
    /// 否则 Ax = (|Nx|,|Ny| 均 &lt; 1/64 ? (0,1,0)×N : (0,0,1)×N) 归一，Ay = N×Ax 归一。
    /// 返回 2D 仿射（丢弃 WCS Z——视图为 XY 投影）。倾斜法向的圆/弧投影为椭圆，
    /// 经 tessellation 诚实近似（72 段）；文字旋转取 OCS 方向向量投影角。
    /// </summary>
    private static (Affine Map, bool IsIdentity) OcsMapper(XYZ normal)
    {
        XYZ n = Normalize(normal);
        if (Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64 && n.Z > 0)
            return (Affine.Identity, true);
        XYZ ax = Normalize(Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64
            ? Cross(new XYZ(0, 1, 0), n) : Cross(new XYZ(0, 0, 1), n));
        XYZ ay = Normalize(Cross(n, ax));
        return (new Affine(ax.X, ay.X, ax.Y, ay.Y, 0, 0), false);
    }

    private static XYZ Normalize(XYZ v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-12 ? new XYZ(0, 0, 1) : new XYZ(v.X / len, v.Y / len, v.Z / len);
    }

    private static XYZ Cross(XYZ a, XYZ b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>OCS 内旋转角（度）→ WCS XY 投影角：方向向量经 OCS 映射后 atan2。</summary>
    private static double OcsRotation(double rotDeg, Affine ocs)
    {
        double r = rotDeg * Math.PI / 180.0;
        (double x, double y) = ocs.Apply(Math.Cos(r), Math.Sin(r));
        return NormDeg(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    /// <summary>非默认法向圆/弧 → OCS 内 tessellate 后逐点映射（椭圆投影诚实近似）。</summary>
    private static void AppendTessellatedArc(
        List<RenderItem> items, string layer, uint color, string handle,
        Affine t, Affine ocs, double cx, double cy, double radius,
        double startDeg, double sweepDeg, IReadOnlyList<double>? dash, int segments = 72)
    {
        // 整圆（|sweep|≥360）取 segments 个均匀采样点、不重复首点：点集均值即圆心、
        // 最大偏移即半径（测试断言口径）；若重复闭合点，均值会偏移 radius/(segments+1)。
        // 闭合改由 Closed:true 表达（画布 EndFigure 连回首点，消除 5° 弦缺口）。
        // 圆弧需含末端点，取 segments+1 个采样，保持开放（Closed:false）。radius 为
        // OCS 半径，缩放由 t.Apply 统一施加（勿预乘 t.RadiusScale，否则双重缩放；
        // 非均匀缩放下自然得椭圆投影）。
        bool fullCircle = Math.Abs(sweepDeg) >= 360.0;
        int samples = fullCircle ? segments : segments + 1;
        var pts = new List<(double X, double Y)>(samples);
        for (int i = 0; i < samples; i++)
        {
            double a = (startDeg + sweepDeg * i / segments) * Math.PI / 180.0;
            (double ox, double oy) = ocs.Apply(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
            pts.Add(t.Apply(ox, oy));
        }
        items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0, handle, Closed: fullCircle, DashPattern: dash));
    }

    /// <summary>
    /// 线型图案解析（drawing 单位 on/off 交替）：实体 LineType 优先，ByLayer 回退图层；
    /// Continuous/ByLayer/ByBlock/空段→null（实线）。段 Length 正=划、负=空、0=点
    /// （点划近似 0.1×scale）。复杂线型（HasShapes/含 Shape/Text 段）→null 实线
    /// 降级——形/文字嵌入 deferred（不假装）。LineTypeScale 乘入。
    /// 奇数段→补末段凑偶（Avalonia DashStyle 要求 on/off 对）。
    /// 注意：ACadSharp 3.8.35 的 IsComplex 语义为"含任意段"（探针核定：纯划空
    /// 线型亦为 true），不能作复杂判据——复杂=HasShapes 或含 Shape/Text 段。
    /// </summary>
    private static IReadOnlyList<double>? ResolveDash(Entity e)
    {
        LineType? lt = e.LineType;
        if (lt is null || lt.Name == "ByLayer") lt = e.Layer?.LineType;
        if (lt is null || lt.Name is "Continuous" or "ByLayer" or "ByBlock") return null;
        if (lt.HasShapes) return null;
        var segs = lt.Segments.ToList();
        if (segs.Count == 0 || segs.Any(s => s.IsShape || s.IsText)) return null;
        double scale = e.LineTypeScale <= 0 ? 1 : e.LineTypeScale;
        var pattern = new List<double>();
        foreach (LineType.Segment s in segs)
            pattern.Add(s.Length == 0 ? 0.1 * scale : Math.Max(Math.Abs(s.Length) * scale, 0.01));
        if (pattern.Count % 2 == 1) pattern.Add(pattern[^1]);
        return pattern;
    }

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
