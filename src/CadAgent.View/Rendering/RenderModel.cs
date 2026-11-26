using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CSMath;

namespace CadAgent.View.Rendering;

/// <summary>Flat render primitive kinds produced by <see cref="RenderModelBuilder"/>.</summary>
public enum RenderKind { Line, Circle, Arc, Polyline, Text, Hatch }

/// <summary>渲染图元所属空间：布局幅面下纸面实体=PaperSpace，视口内模型=ModelSpace。</summary>
public enum RenderSpace { ModelSpace, PaperSpace }

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
/// 空间/来源（项2 F）：<see cref="Space"/> 区分纸面/模型图元；<see cref="SourceHandle"/>
/// 为原始实体句柄（Insert 子图元不含子索引后缀，溯源用），<see cref="BlockName"/>/
/// <see cref="ParentInsertChain"/> 记录块展开路径，<see cref="AttributeTag"/> 标属性
/// 值文本的 ATTRIB 标签；<see cref="Locked"/> 为将来编辑锁定位（默认 false）。
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
    bool Fill = false,
    bool Locked = false,
    RenderSpace Space = RenderSpace.ModelSpace,
    string? SourceHandle = null,
    string? BlockName = null,
    IReadOnlyList<string>? ParentInsertChain = null,
    string? AttributeTag = null);

/// <summary>
/// DrawingSession -> flat render-item list (plan 4 task 2). Pure logic: no Avalonia
/// dependency, safe to unit-test headless. Supported entities: Line, Circle, Arc,
/// LwPolyline, Polyline2D, TextEntity, MText, Insert (block expansion + ATTRIB
/// attributes as Text items), Dimension
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
    /// 纸面空间读图（项2 F）：布局关联块内实体（排除 Viewport——视口内容与框线分别由
    /// <see cref="BuildViewport"/>/<see cref="BuildViewportFrame"/> 表达）以 Space=PaperSpace
    /// 展开，复用同一 Append 管线（块展开/属性/线型口径不变）。未知布局名→空（不抛）。
    /// </summary>
    public static IReadOnlyList<RenderItem> BuildPaperSpace(CadDocument doc, string layoutName)
    {
        var layout = doc.Layouts?.FirstOrDefault(l => string.Equals(l.Name, layoutName, StringComparison.Ordinal));
        if (layout?.AssociatedBlock is null) return Array.Empty<RenderItem>();
        var items = new List<RenderItem>();
        int skipped = 0;
        var ctx = new EmitContext(RenderSpace.PaperSpace, null, null, null);
        foreach (Entity e in layout.AssociatedBlock.Entities)
        {
            if (e is Viewport) continue;
            Append(e, items, ref skipped, Affine.Identity, rotationDeg: 0, heightScale: 1,
                byBlockColor: null, depth: 0, ctx: ctx);
        }
        return items;
    }

    /// <summary>
    /// 锁定视口框（项2 F）：视口纸面 bounds 的 4 条边线（底/右/顶/左顺序），
    /// Space=PaperSpace、Locked=true（框线**可选中**但编辑被拒——Route Locked 守卫）、
    /// Handle=SourceHandle=视口句柄（溯源到视口实体）。
    /// </summary>
    public static IReadOnlyList<RenderItem> BuildViewportFrame(Viewport vp)
    {
        double minX = vp.Center.X - vp.Width / 2, maxX = vp.Center.X + vp.Width / 2;
        double minY = vp.Center.Y - vp.Height / 2, maxY = vp.Center.Y + vp.Height / 2;
        string h = vp.Handle.ToString(CultureInfo.InvariantCulture);
        RenderItem Seg(double x1, double y1, double x2, double y2) => new(
            RenderKind.Line, new[] { (x1, y1), (x2, y2) }, "0", 0x808080, null, 0, 0, h,
            Space: RenderSpace.PaperSpace, SourceHandle: h, Locked: true);
        return new[] { Seg(minX, minY, maxX, minY), Seg(maxX, minY, maxX, maxY),
                       Seg(maxX, maxY, minX, maxY), Seg(minX, maxY, minX, minY) };
    }

    /// <summary>
    /// 布局合成（项2 F）：纸面图元 → 视口内模型图元（纸面坐标）→ 视口框（框最后，
    /// 画布按序绘制→框覆盖在模型之上）。vp=null 时仅纸面（退化布局诚实降级）。
    /// </summary>
    public static IReadOnlyList<RenderItem> ComposeLayout(CadDocument doc, string layoutName, Viewport? vp)
    {
        var list = new List<RenderItem>();
        list.AddRange(BuildPaperSpace(doc, layoutName));
        if (vp is not null)
        {
            list.AddRange(BuildViewport(doc, vp));
            list.AddRange(BuildViewportFrame(vp));
        }
        return list;
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

    /// <summary>
    /// Append 来源上下文（项2 F）：空间 + 来源句柄（不含子索引后缀，溯源用）+
    /// 块展开路径（最内层块名 / Insert 句柄链）。null=顶层实体（模型空间、无块路径）。
    /// 属性标签不进上下文：ATTRIB 图元在 EmitAttribute 内直接携带自身上下文。
    /// </summary>
    private readonly record struct EmitContext(
        RenderSpace Space, string? SourceHandle, string? BlockName,
        IReadOnlyList<string>? InsertChain);

    private static void Append(
        Entity e, List<RenderItem> items, ref int skipped,
        Affine t, double rotationDeg, double heightScale, uint? byBlockColor, int depth,
        string? handle = null, EmitContext? ctx = null)
    {
        EmitContext c = ctx ?? new EmitContext(RenderSpace.ModelSpace, null, null, null);
        // 本次调用新增图元的起点：switch 之后统一戳记（子递归已自带来源，仅补空间）。
        int startIndex = items.Count;
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
            // Ruling R8：ATTDEF 是块模板定义（不可见），实例值仅经 Insert.Attributes 渲染；
            // 须先于 TextEntity 分支（ACadSharp AttributeBase 派生自 TextEntity）。
            // 不计数 skipped——定义不属于渲染对象，不是"不支持的实体"。
            case AttributeDefinition:
                break;

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
                // 来源戳记：块子图元带最内层块名 + 完整 insert 句柄链（嵌套逐层追加）。
                string insertHandle = handle;
                string insertHandleStr = insert.Handle.ToString(CultureInfo.InvariantCulture);
                var chain = (c.InsertChain ?? Array.Empty<string>()).Append(insertHandleStr).ToArray();
                int childIndex = 0;
                foreach (Entity child in insert.Block.Entities)
                {
                    Append(child, items, ref skipped, composed, rot, hs, color, depth + 1,
                        handle + ":" + childIndex.ToString(CultureInfo.InvariantCulture),
                        new EmitContext(c.Space, child.Handle.ToString(CultureInfo.InvariantCulture),
                            insert.Block.Name, chain));
                    childIndex++;
                }
                // 属性（ATTRIB）为 Insert 自身附属文本，非块内容：Handle 取 "{insert}:attr:{tag}"、
                // SourceHandle 为 ATTRIB 句柄（编辑逆映射定位用）。
                foreach (AttributeEntity attr in insert.Attributes)
                {
                    // DXF ATTRIB 的 10/11 组码是"已放置"坐标（INSERT 的 OCS 最终空间），不是块局部
                    // 坐标——只承接父仿射 t，绝不能套 insert 自身的 composed 变换（全实例偏移上千单位的实证缺陷）。
                    EmitAttribute(attr, items, t, rotationDeg, heightScale, color,
                        insertHandle + ":attr:" + attr.Tag, c.Space);
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
                var chain = (c.InsertChain ?? Array.Empty<string>())
                    .Append(dim.Handle.ToString(CultureInfo.InvariantCulture)).ToArray();
                int childIndex = 0;
                foreach (Entity child in dim.Block.Entities)
                    Append(child, items, ref skipped, t, rotationDeg, heightScale, byBlockColor, depth + 1,
                        handle + ":" + childIndex++.ToString(CultureInfo.InvariantCulture),
                        new EmitContext(c.Space, child.Handle.ToString(CultureInfo.InvariantCulture),
                            dim.Block.Name, chain));
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

        // 统一戳记本次新产图元：来源上下文优先，SourceHandle 缺失回落自身 Handle
        // （顶层实体=自身句柄，溯源/编辑逆映射同口径）。已带来源（子递归/属性）的图元
        // 其空间与归属由自身上下文负责，此处只跳过、不改写（祖先不得覆盖后代 Space）。
        for (int i = startIndex; i < items.Count; i++)
        {
            RenderItem it = items[i];
            if (it.SourceHandle is not null) continue;
            items[i] = it with
            {
                Space = c.Space,
                SourceHandle = c.SourceHandle ?? it.Handle,
                BlockName = it.BlockName ?? c.BlockName,
                ParentInsertChain = it.ParentInsertChain ?? c.InsertChain,
            };
        }
    }

    /// <summary>
    /// Insert 属性（ATTRIB）发射：坐标是"已放置"坐标，只接父仿射 <paramref name="t"/>；
    /// 对齐锚点——默认（左+基线）用 InsertPoint，其余对齐（真图标题栏多为居中）用
    /// AlignmentPoint。不改 plain TextEntity 的既有口径（恒用 InsertPoint）。
    /// </summary>
    private static void EmitAttribute(
        AttributeEntity attr, List<RenderItem> items, Affine t, double rotationDeg,
        double heightScale, uint? byBlockColor, string handle, RenderSpace space)
    {
        bool defaultAlignment = attr.HorizontalAlignment == TextHorizontalAlignment.Left
            && attr.VerticalAlignment == TextVerticalAlignmentType.Baseline;
        XYZ anchor = defaultAlignment ? attr.InsertPoint : attr.AlignmentPoint;
        var (ocs, _) = OcsMapper(attr.Normal);
        (double ox, double oy) = ocs.Apply(anchor.X, anchor.Y);
        items.Add(new RenderItem(RenderKind.Text,
            new[] { t.Apply(ox, oy) },
            attr.Layer?.Name ?? "0", ResolveColor(attr, byBlockColor), attr.Value,
            Math.Abs(attr.Height) * heightScale,
            NormDeg(OcsRotation(ToDeg(attr.Rotation), ocs) + rotationDeg),
            handle, DashPattern: ResolveDash(attr),
            Space: space,
            SourceHandle: attr.Handle.ToString(CultureInfo.InvariantCulture),
            AttributeTag: attr.Tag));
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

    /// <summary>图层表颜色→0xRRGGBB（空图层色样用；null→白 fallback）。</summary>
    public static uint LayerColorRgb(ACadSharp.Tables.Layer? layer) => LayerColor(layer);

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
    /// <summary>OCS→WCS XY 投影仿射（内部暴露给块编辑逆解：须与渲染复合同口径）。</summary>
    internal static Affine OcsAffine(XYZ normal) => OcsMapper(normal).Map;

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
}
