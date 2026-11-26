using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;

namespace CadAgent.View.Rendering;

/// <summary>渲染质量档（项3 §3.1）：交互期粗绘 / 静止精绘。Task 3 接入计时切换。</summary>
public enum RenderQuality { Interactive, Refined }

/// <summary>单图元 LOD 决策：全细节/简化（1px 点或轮廓条）/仅包围盒/跳过。</summary>
public enum RenderDecision { Full, Simplified, Bbox, Skip }

/// <summary>
/// 画笔解析器（项3 T2）：按颜色缓存 1px 实线笔；虚线笔按
/// (colorRgb, thickness, dash 签名) 缓存，上限 <see cref="DashCacheCapacity"/>，
/// 超出整体清空（简单容量守卫替代 LRU）。非线程安全（UI 线程调用）。
/// </summary>
public sealed class PenResolver
{
    /// <summary>虚线笔缓存上限（超出清空）。</summary>
    public const int DashCacheCapacity = 512;

    /// <summary>亚像素图案守卫（任一段 &lt;0.25px → 实线降级防摩尔纹，沿用旧画布口径）。</summary>
    private const double MinDashSegmentPx = 0.25;

    /// <summary>超长图案总量守卫（防性能退化；沿用旧画布口径）。</summary>
    private const double MaxDashTotalPx = 1e6;

    private readonly Dictionary<uint, Pen> _colorPens = new();
    private readonly Dictionary<(uint Rgb, double Thickness, string Sig), Pen> _dashPens = new();

    internal int ColorPenCount => _colorPens.Count;
    internal int DashPenCount => _dashPens.Count;

    /// <summary>图元颜色 1px 实线笔（按 rgb 复用实例）。</summary>
    public Pen ColorPen(uint rgb)
    {
        if (_colorPens.TryGetValue(rgb, out Pen? pen)) return pen;
        pen = new Pen(new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), 1);
        _colorPens[rgb] = pen;
        return pen;
    }

    /// <summary>
    /// 图元笔：无 DashPattern → basePen 原样；有则按设备像素段长克隆虚线笔（缓存）。
    /// 亚像素段/超长图案 → 实线降级（返回 basePen，不入缓存）。
    /// </summary>
    public Pen ForItem(RenderItem item, Pen basePen, double scale)
    {
        if (item.DashPattern is not { Count: > 0 } dash || basePen.Thickness <= 0) return basePen;
        var device = new double[dash.Count];
        double total = 0;
        for (int i = 0; i < dash.Count; i++)
        {
            double d = dash[i] * scale;
            if (!(d >= MinDashSegmentPx)) return basePen;
            device[i] = d;
            total += d;
        }
        if (total >= MaxDashTotalPx) return basePen;

        uint rgb = RgbOf(basePen.Brush);
        string sig = DashSignature(device);
        var key = (rgb, basePen.Thickness, sig);
        if (_dashPens.TryGetValue(key, out Pen? cached)) return cached;
        if (_dashPens.Count >= DashCacheCapacity) _dashPens.Clear();
        var pen = new Pen(basePen.Brush, basePen.Thickness)
        {
            DashStyle = new DashStyle(device.Select(d => d / basePen.Thickness), 0)
        };
        _dashPens[key] = pen;
        return pen;
    }

    /// <summary>画笔颜色的 0xRRGGBB（非纯色画刷回退 0xFFFFFF）。</summary>
    internal static uint RgbOf(IBrush? brush) => brush is ISolidColorBrush solid
        ? ((uint)solid.Color.R << 16) | ((uint)solid.Color.G << 8) | solid.Color.B
        : 0xFFFFFFu;

    private static string DashSignature(double[] device)
    {
        var sb = new StringBuilder(device.Length * 8);
        for (int i = 0; i < device.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(device[i].ToString("F3", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

/// <summary>
/// 文本 LRU 缓存（项3 §3.1）：容量 <see cref="Capacity"/>，键
/// (text, fontBucket=round(px*2)/2, bold, colorRgb)——0.5px 字高桶 + 颜色/粗细。
/// 命中移至最近端；超容淘汰最久未用。非线程安全（UI 线程调用）。
/// </summary>
public sealed class TextCache
{
    /// <summary>缓存容量（spec：≤512）。</summary>
    public const int Capacity = 512;

    private readonly record struct Key(string Text, double FontBucket, bool Bold, uint Rgb);

    private readonly Dictionary<Key, LinkedListNode<(Key Key, FormattedText Text)>> _map = new();
    private readonly LinkedList<(Key Key, FormattedText Text)> _lru = new();

    internal int Count => _map.Count;
    internal long Hits { get; private set; }
    internal long Misses { get; private set; }
    internal long Evictions { get; private set; }

    /// <summary>取缓存文本；未命中用 factory 构造并插入（超容淘汰最久未用）。</summary>
    public FormattedText GetOrAdd(string text, double fontPx, bool bold, uint colorRgb, Func<FormattedText> factory)
    {
        var key = new Key(text, Math.Round(fontPx * 2) / 2, bold, colorRgb);
        if (_map.TryGetValue(key, out LinkedListNode<(Key Key, FormattedText Text)>? node))
        {
            Hits++;
            _lru.Remove(node);
            _lru.AddLast(node);
            return node.Value.Text;
        }

        Misses++;
        FormattedText created = factory();
        if (_map.Count >= Capacity && _lru.First is { } oldest)
        {
            _map.Remove(oldest.Value.Key);
            _lru.RemoveFirst();
            Evictions++;
        }
        LinkedListNode<(Key Key, FormattedText Text)> added = _lru.AddLast((key, created));
        _map[key] = added;
        return created;
    }

    /// <summary>清空缓存（Items/DPI 等失效场景复用；Task 3 起调用）。</summary>
    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}

/// <summary>
/// 统一绘制器（项3 T2）：从 DrawingCanvas 抽出的图元绘制 + LOD 决策 + 选中/高亮叠加。
/// 只依赖 Avalonia <see cref="DrawingContext"/>（无控件依赖；快照/缓存层复用）。
/// 语义边界：LOD 仅视觉降级，不改数据/命中/选择；图元按 Items 序绘制（遮挡顺序不变），
/// 选中/高亮最后重绘（受预算：投影包围盒 &gt;25% 视口或稠密点集 → 仅 bbox + 2px 描边）。
/// 无有效坐标的图元（Index.BoundsOf 为 NaN 角）直接跳过。
/// </summary>
public sealed class RenderItemDrawer
{
    /// <summary>选中/高亮画笔（黄 2px，延续旧画布语义）。</summary>
    public static readonly Pen SelectionPen = new(Brushes.Yellow, 2);

    /// <summary>文本命中盒/简化条宽高比（每字符 0.6×字高，与命中数学一致）。</summary>
    private const double TextWidthPerHeight = 0.6;

    /// <summary>选中/高亮全细节预算：Hatch/Polyline 点数上限（spec §3.2）。</summary>
    internal const int MaxBudgetPoints = 2000;

    /// <summary>选中/高亮全细节预算：投影包围盒 ≤ 视口面积 25%。</summary>
    private const double MaxBudgetViewportFraction = 0.25;

    private readonly PenResolver _fallbackPens = new(); // DrawOne（diff）用：无调用方缓存
    private readonly List<int> _simplifyScratch = new();

    /// <summary>本帧跳过文本数（LOD Skip；测试/状态栏观测）。</summary>
    internal int LastTextSkipped { get; private set; }

    /// <summary>本帧 Hatch 降级数（Simplified/Bbox；测试/状态栏观测）。</summary>
    internal int LastHatchDowngraded { get; private set; }

    /// <summary>本帧是否有选中/高亮被降级为 bbox（测试/状态栏注记）。</summary>
    internal bool LastSelectionDowngraded { get; private set; }

    // ---- LOD 决策（阈值唯一来源）----

    /// <summary>文本：Refined &lt;2 跳过 / 2–6 简化条 / ≥6 字形；Interactive &lt;4 / 4–10 / ≥10。</summary>
    public static RenderDecision DecideText(double screenFontPx, RenderQuality quality)
    {
        if (!double.IsFinite(screenFontPx) || screenFontPx <= 0) return RenderDecision.Skip;
        return quality == RenderQuality.Refined
            ? screenFontPx < 2 ? RenderDecision.Skip
                : screenFontPx < 6 ? RenderDecision.Simplified : RenderDecision.Full
            : screenFontPx < 4 ? RenderDecision.Skip
                : screenFontPx < 10 ? RenderDecision.Simplified : RenderDecision.Full;
    }

    /// <summary>Hatch：Refined 面积&lt;4px² 或点数&gt;2000 → Simplified；Interactive 点数&gt;500 或面积&lt;16px² → Bbox。</summary>
    public static RenderDecision DecideHatch(int points, double screenAreaPx2, RenderQuality quality)
    {
        if (!double.IsFinite(screenAreaPx2)) screenAreaPx2 = 0;
        return quality == RenderQuality.Refined
            ? screenAreaPx2 < 4 || points > 2000 ? RenderDecision.Simplified : RenderDecision.Full
            : points > 500 || screenAreaPx2 < 16 ? RenderDecision.Bbox : RenderDecision.Full;
    }

    /// <summary>微图元：屏幕包围盒最长边 &lt;0.5px（Refined）/&lt;1px（Interactive）→ 1px 点。</summary>
    public static RenderDecision DecideTiny(double screenExtentPx, RenderQuality quality)
    {
        double threshold = quality == RenderQuality.Refined ? 0.5 : 1.0;
        return double.IsFinite(screenExtentPx) && screenExtentPx < threshold
            ? RenderDecision.Simplified
            : RenderDecision.Full;
    }

    /// <summary>
    /// 折线抽稀：屏幕间距 &lt;minScreenStepPx 的中间顶点合并，首末端点恒保留
    /// （闭合多段线重复首点惯例下闭合性不丢）。closed=首末顶点重合（几何闭合）。
    /// 返回保留顶点索引（升序）。
    /// </summary>
    public static IReadOnlyList<int> Simplify(
        IReadOnlyList<(double X, double Y)> pts, double minScreenStepPx, double scale, out bool closed)
    {
        var kept = new List<int>(pts.Count);
        closed = SimplifyInto(pts, minScreenStepPx, scale, kept);
        return kept;
    }

    /// <summary>抽稀本体（复用调用方缓冲；drawer 每帧零分配）。</summary>
    internal static bool SimplifyInto(
        IReadOnlyList<(double X, double Y)> pts, double minScreenStepPx, double scale, List<int> kept)
    {
        kept.Clear();
        int n = pts.Count;
        if (n == 0) return false;
        if (n == 1) { kept.Add(0); return false; }

        (double X, double Y) first = pts[0], last = pts[n - 1];
        bool closed = first.X == last.X && first.Y == last.Y; // 重复首点=闭合惯例（精确相等）

        double threshold = minScreenStepPx > 0 && scale > 0 ? minScreenStepPx / scale : 0;
        kept.Add(0);
        if (threshold <= 0)
        {
            for (int i = 1; i < n; i++) kept.Add(i);
            return closed;
        }
        double lastX = first.X, lastY = first.Y;
        for (int i = 1; i < n - 1; i++)
        {
            double dx = pts[i].X - lastX, dy = pts[i].Y - lastY;
            if (Math.Sqrt(dx * dx + dy * dy) < threshold) continue; // 屏幕间距不足 → 合并
            kept.Add(i);
            lastX = pts[i].X;
            lastY = pts[i].Y;
        }
        kept.Add(n - 1);
        return closed;
    }

    /// <summary>选中/高亮图元是否超出全细节预算（有效点数&gt;2000 或投影包围盒&gt;25% 视口）。</summary>
    internal static bool IsOverBudget(CanvasProjection projection, RenderItem item)
    {
        if (item.Kind is RenderKind.Hatch or RenderKind.Polyline && CountFinitePoints(item.Points) > MaxBudgetPoints)
            return true;
        if (!TryScreenBounds(projection, item, out ScreenBox box)) return false;
        double viewportArea = projection.ViewportW * projection.ViewportH;
        if (!(viewportArea > 0)) return false;
        return (box.MaxX - box.MinX) * (box.MaxY - box.MinY) > viewportArea * MaxBudgetViewportFraction;
    }

    /// <summary>有效顶点数（Hatch 的 (NaN,NaN) 是路径分隔符，不计入预算）。</summary>
    private static int CountFinitePoints(IReadOnlyList<(double X, double Y)> pts)
    {
        int count = 0;
        foreach ((double x, double y) in pts)
            if (double.IsFinite(x) && double.IsFinite(y)) count++;
        return count;
    }

    // ---- 绘制入口 ----

    /// <summary>
    /// 绘制一批图元：cull 且 indices 非空 → 仅按升序索引绘制（否则全量）；
    /// 选中/高亮在图元之后重绘（全细节受预算，超预算 → bbox + 2px 描边）。
    /// </summary>
    public void Draw(
        DrawingContext ctx, CanvasProjection projection, IReadOnlyList<RenderItem> items,
        IReadOnlyList<int>? indices, RenderQuality quality, PenResolver pens, TextCache? texts,
        RenderItem? selected, IReadOnlySet<string>? highlighted,
        bool selectionBudgetExceeded = false, bool cull = true)
    {
        bool useIndices = cull && indices is not null;
        int count = useIndices ? indices!.Count : items.Count;

        for (int n = 0; n < count; n++)
        {
            int i = useIndices ? indices![n] : n;
            if ((uint)i >= (uint)items.Count) continue;
            RenderItem item = items[i];
            DrawItemCore(ctx, projection, item, pens.ColorPen(item.ColorRgb), quality, pens, texts, bold: false);
        }

        DrawSelection(ctx, projection, items, indices, quality, pens, texts,
            selected, highlighted, selectionBudgetExceeded, cull);
    }

    /// <summary>
    /// 仅画选中/高亮叠加层（项3 T3 位图缓存：基元已烘焙进缓存位图，叠加层须实时绘制）。
    /// 语义与 <see cref="Draw"/> 的叠加段完全一致（cull + LOD + 预算：超预算 → bbox + 2px 描边）。
    /// </summary>
    public void DrawSelection(
        DrawingContext ctx, CanvasProjection projection, IReadOnlyList<RenderItem> items,
        IReadOnlyList<int>? indices, RenderQuality quality, PenResolver pens, TextCache? texts,
        RenderItem? selected, IReadOnlySet<string>? highlighted,
        bool selectionBudgetExceeded = false, bool cull = true)
    {
        if (selected is null && (highlighted is null || highlighted.Count == 0)) return;
        bool useIndices = cull && indices is not null;
        int count = useIndices ? indices!.Count : items.Count;

        for (int n = 0; n < count; n++)
        {
            int i = useIndices ? indices![n] : n;
            if ((uint)i >= (uint)items.Count) continue;
            RenderItem item = items[i];
            bool isSelected = selected is not null && IsSameItem(item, selected);
            bool isHighlighted = highlighted is not null && item.Handle.Length > 0 && highlighted.Contains(item.Handle);
            if (!isSelected && !isHighlighted) continue;

            if ((isSelected && selectionBudgetExceeded) || IsOverBudget(projection, item))
            {
                if (TryScreenBounds(projection, item, out ScreenBox box)) DrawBox(ctx, box, SelectionPen);
                LastSelectionDowngraded = true;
                continue;
            }
            DrawItemCore(ctx, projection, item, SelectionPen, quality, pens, texts, bold: true);
        }
    }

    /// <summary>
    /// 单图元绘制（diff before 层/删除线等复用）：无调用方画笔/文本缓存，
    /// 虚线笔走实例内部解析器（与旧画布逐图元等价）。帧计数由调用方 BeginFrame 管理。
    /// </summary>
    public void DrawOne(DrawingContext ctx, CanvasProjection projection, RenderItem item, Pen pen, RenderQuality quality)
    {
        DrawItemCore(ctx, projection, item, pen, quality, _fallbackPens, texts: null, bold: false);
    }

    /// <summary>帧开始：清零本帧 LOD 计数（DrawingCanvas 每帧调用一次；Draw/DrawOne 不重置）。</summary>
    public void BeginFrame() => ResetCounters();

    private void ResetCounters()
    {
        LastTextSkipped = 0;
        LastHatchDowngraded = 0;
        LastSelectionDowngraded = false;
    }

    private void DrawItemCore(
        DrawingContext ctx, CanvasProjection p, RenderItem item, Pen pen,
        RenderQuality quality, PenResolver pens, TextCache? texts, bool bold)
    {
        if (!TryScreenBounds(p, item, out ScreenBox box)) return; // 无有效坐标（NaN 角）
        pen = pens.ForItem(item, pen, p.Scale);
        double extent = Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);

        switch (item.Kind)
        {
            case RenderKind.Line:
            {
                if (item.Points.Count < 2) return;
                if (DecideTiny(extent, quality) == RenderDecision.Simplified)
                {
                    DrawDot(ctx, pen, (box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
                    return;
                }
                ctx.DrawLine(pen, ScreenPoint(p, item.Points[0]), ScreenPoint(p, item.Points[1]));
                return;
            }

            case RenderKind.Circle:
            {
                if (item.Points.Count < 2) return;
                double r = Math.Abs(item.Points[1].X) * p.Scale;
                if (r <= 0) return;
                if (DecideTiny(extent, quality) == RenderDecision.Simplified)
                {
                    DrawDot(ctx, pen, (box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
                    return;
                }
                ctx.DrawEllipse(null, pen, ScreenPoint(p, item.Points[0]), r, r);
                return;
            }

            case RenderKind.Arc:
            {
                if (item.Points.Count < 3) return;
                if (DecideTiny(extent, quality) == RenderDecision.Simplified)
                {
                    DrawDot(ctx, pen, (box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
                    return;
                }
                DrawArc(ctx, p, item, pen);
                return;
            }

            case RenderKind.Polyline:
            {
                if (item.Points.Count < 2) return;
                if (DecideTiny(extent, quality) == RenderDecision.Simplified)
                {
                    DrawDot(ctx, pen, (box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
                    return;
                }
                DrawPolyline(ctx, p, item, pen, quality);
                return;
            }

            case RenderKind.Text:
                DrawText(ctx, p, item, pen, quality, texts, bold);
                return;

            case RenderKind.Hatch:
                DrawHatch(ctx, p, item, pen, quality, box);
                return;
        }
    }

    private void DrawPolyline(DrawingContext ctx, CanvasProjection p, RenderItem item, Pen pen, RenderQuality quality)
    {
        double stepPx = quality == RenderQuality.Refined ? 0.5 : 1.0;
        IReadOnlyList<(double X, double Y)> pts = item.Points;
        int n = pts.Count;
        List<int>? kept = n > 2 ? _simplifyScratch : null;
        if (kept is not null) SimplifyInto(pts, stepPx, p.Scale, kept);

        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(ScreenPoint(p, pts[0]), false);
            if (kept is not null)
                for (int k = 1; k < kept.Count; k++) g.LineTo(ScreenPoint(p, pts[kept[k]]));
            else
                for (int k = 1; k < n; k++) g.LineTo(ScreenPoint(p, pts[k]));
            // Closed=true（整圆 tessellation）连回首点；重复首点自闭合的既有闭合多段线行为不变。
            g.EndFigure(item.Closed);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private void DrawText(
        DrawingContext ctx, CanvasProjection p, RenderItem item, Pen pen,
        RenderQuality quality, TextCache? texts, bool bold)
    {
        if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) return;
        double fontPx = item.TextHeight * p.Scale;
        RenderDecision decision = DecideText(fontPx, quality);
        if (decision == RenderDecision.Skip)
        {
            LastTextSkipped++;
            return;
        }
        (double sx, double sy) = p.WorldToScreen(item.Points[0].X, item.Points[0].Y);
        if (!double.IsFinite(sx) || !double.IsFinite(sy)) return;

        if (decision == RenderDecision.Simplified)
        {
            // 简化条：基线处 1px 横线（宽=字符数×世界字高×0.6×缩放），不画字形。
            if (item.TextHeight <= 0 || fontPx <= 0) return;
            double width = item.Text.Length * item.TextHeight * TextWidthPerHeight * p.Scale;
            if (width <= 0) return;
            ctx.DrawLine(pen, new Point(sx, sy), new Point(sx + width, sy));
            return;
        }

        // 交互档不走文字缓存（spec §3.1：交互期只变换缓存位图/粗绘，缓存仅静止精绘复用）。
        TextCache? cache = quality == RenderQuality.Refined ? texts : null;
        FormattedText text = cache is null
            ? CreateFormattedText(item.Text, fontPx, bold, pen.Brush!)
            : cache.GetOrAdd(item.Text, fontPx, bold, PenResolver.RgbOf(pen.Brush),
                () => CreateFormattedText(item.Text, fontPx, bold, pen.Brush!));
        // CAD 插入点=基线左端；FormattedText 以左上角定位 → 上移文本高度。
        ctx.DrawText(text, new Point(sx, sy - text.Height));
    }

    private void DrawHatch(
        DrawingContext ctx, CanvasProjection p, RenderItem item, Pen pen, RenderQuality quality, ScreenBox box)
    {
        if (item.Points.Count < 3) return;
        double area = (box.MaxX - box.MinX) * (box.MaxY - box.MinY);
        RenderDecision decision = DecideHatch(item.Points.Count, area, quality);
        if (decision != RenderDecision.Full) LastHatchDowngraded++;

        if (decision == RenderDecision.Bbox)
        {
            DrawBox(ctx, box, pen);
            return;
        }
        if (decision == RenderDecision.Simplified)
        {
            DrawFirstHatchPath(ctx, p, item, pen, box);
            return;
        }

        // Full：各边界路径以 (NaN,NaN) 分隔；逐路径闭合 + EvenOdd 跨路径挖岛。
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            bool inFigure = false;
            foreach ((double x, double y) in item.Points)
            {
                if (double.IsNaN(x))
                {
                    if (inFigure) g.EndFigure(true);
                    inFigure = false;
                    continue;
                }
                if (!inFigure) { g.BeginFigure(ScreenPoint(p, x, y), item.Fill); inFigure = true; }
                else g.LineTo(ScreenPoint(p, x, y));
            }
            if (inFigure) g.EndFigure(true);
        }
        ctx.DrawGeometry(item.Fill ? pen.Brush : null, pen, geo);
    }

    /// <summary>简化 Hatch：仅第一条边界路径轮廓（不填充）；无路径 → 退化为 bbox。</summary>
    private static void DrawFirstHatchPath(
        DrawingContext ctx, CanvasProjection p, RenderItem item, Pen pen, ScreenBox box)
    {
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            bool inFigure = false;
            int vertices = 0;
            foreach ((double x, double y) in item.Points)
            {
                if (double.IsNaN(x)) break; // 第一条路径结束
                if (!inFigure) { g.BeginFigure(ScreenPoint(p, x, y), false); inFigure = true; }
                else g.LineTo(ScreenPoint(p, x, y));
                vertices++;
            }
            if (vertices < 2)
            {
                DrawBox(ctx, box, pen);
                return;
            }
            if (inFigure) g.EndFigure(true);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>
    /// Arc 点约定：Points[0]=圆心，Points[1]=(半径,起始角°)，Points[2]=(终止角°,0)，角度 CCW。
    /// 世界 CCW 经 Y 翻转后在屏幕上为视觉逆时针 → SweepDirection.CounterClockwise；
    /// 扫掠角 &gt;180° → isLargeArc；start==end（扫掠 0）按整圆渲染（与 VM 命中一致）。
    /// </summary>
    private static void DrawArc(DrawingContext ctx, CanvasProjection p, RenderItem item, Pen pen)
    {
        (double cx, double cy) = item.Points[0];
        double r = Math.Abs(item.Points[1].X);
        double screenRadius = r * p.Scale;
        if (screenRadius <= 0) return;

        double startDeg = item.Points[1].Y;
        double endDeg = item.Points[2].X;
        double span = NormDeg(endDeg - startDeg);
        Point center = ScreenPoint(p, cx, cy);
        if (span < 1e-9)
        {
            ctx.DrawEllipse(null, pen, center, screenRadius, screenRadius);
            return;
        }

        double a0 = startDeg * Math.PI / 180.0;
        double a1 = endDeg * Math.PI / 180.0;
        Point start = ScreenPoint(p, cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        Point end = ScreenPoint(p, cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(screenRadius, screenRadius), 0, span > 180, SweepDirection.CounterClockwise);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    // ---- 屏幕包围盒 / 小工具 ----

    private readonly record struct ScreenBox(double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>
    /// 屏幕包围盒（含 Text 字形上升区）；无有效坐标 → false（drawer 跳过该图元，
    /// 对应 Index.BoundsOf 的 NaN 角约定）。
    /// </summary>
    private static bool TryScreenBounds(CanvasProjection p, RenderItem item, out ScreenBox box)
    {
        box = default;
        if (item.Kind is RenderKind.Circle or RenderKind.Arc)
        {
            if (item.Points.Count < 2) return false;
            (double cx, double cy) = item.Points[0];
            double r = Math.Abs(item.Points[1].X);
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(r)) return false;
            (double sx, double sy) = p.WorldToScreen(cx, cy);
            double sr = r * p.Scale;
            if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sr)) return false;
            // Circle 与 Arc 同用整圆包围盒（Arc 保守取整圆，与索引 TryBox 口径一致）。
            box = new ScreenBox(sx - sr, sy - sr, sx + sr, sy + sr);
            return true;
        }

        if (item.Kind == RenderKind.Text)
        {
            if (item.Points.Count < 1) return false;
            double h = item.TextHeight;
            if (!double.IsFinite(h) || h <= 0) return false;
            (double sx, double sy) = p.WorldToScreen(item.Points[0].X, item.Points[0].Y);
            double w = (item.Text?.Length ?? 0) * h * TextWidthPerHeight * p.Scale;
            double sh = h * p.Scale;
            if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(w) || !double.IsFinite(sh)) return false;
            double left = Math.Min(sx, sx + w), right = Math.Max(sx, sx + w);
            box = new ScreenBox(left, sy - sh, right, sy);
            return true;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach ((double x, double y) in item.Points)
        {
            (double sx, double sy) = p.WorldToScreen(x, y);
            if (!double.IsFinite(sx) || !double.IsFinite(sy)) continue;
            any = true;
            if (sx < minX) minX = sx;
            if (sy < minY) minY = sy;
            if (sx > maxX) maxX = sx;
            if (sy > maxY) maxY = sy;
        }
        if (!any) return false;
        box = new ScreenBox(minX, minY, maxX, maxY);
        return true;
    }

    private static void DrawBox(DrawingContext ctx, ScreenBox box, Pen pen) =>
        ctx.DrawRectangle(null, pen,
            new Rect(box.MinX, box.MinY, Math.Max(0, box.MaxX - box.MinX), Math.Max(0, box.MaxY - box.MinY)));

    private static void DrawDot(DrawingContext ctx, Pen pen, double x, double y)
    {
        if (pen.Brush is null || !double.IsFinite(x) || !double.IsFinite(y)) return;
        ctx.DrawEllipse(pen.Brush, null, new Point(x, y), 0.5, 0.5);
    }

    private static FormattedText CreateFormattedText(string text, double fontPx, bool bold, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
            fontPx, brush);

    private static bool IsSameItem(RenderItem item, RenderItem selected) =>
        (selected.Handle.Length > 0 && item.Handle == selected.Handle) || ReferenceEquals(item, selected);

    private static Point ScreenPoint(CanvasProjection p, (double X, double Y) world) =>
        ScreenPoint(p, world.X, world.Y);

    private static Point ScreenPoint(CanvasProjection p, double x, double y)
    {
        (double sx, double sy) = p.WorldToScreen(x, y);
        return new Point(sx, sy);
    }

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;
}
