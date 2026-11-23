using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Controls;

/// <summary>
/// 画布控件（计划4 T3）：渲染 CanvasViewModel.VisibleItems（深色背景 #1E1E1E），
/// 变换/命中全部委托 VM（WorldToScreen/ScreenToWorld/HitTest）。
/// 指针：滚轮→ZoomAt（光标锚点）；中键拖拽→PanBy（屏幕增量）；左键→ScreenToWorld
/// →T9 工具拦截（ToolClickHandler，MainWindow 按 ActiveTool 路由）→未消费则
/// HitTest（5px 容差换算世界单位）→Select。选中图元黄色 2px 高亮（文本黄色加粗）。
/// T9 绘制预览：PendingPoint 十字标记 + 到光标的橡皮筋线（Line 工具第一击）。
/// 尺寸变化→Fit（重置 zoom/pan，T10 可升级为保持视图）。线宽恒定设备像素（CAD 习惯）。
/// 文本按水平渲染（RotationDeg 暂不参与，与 VM 文本命中盒一致）；插入点视为基线左端。
/// </summary>
public sealed class DrawingCanvas : Control
{
    private const double WheelZoomBase = 1.2;
    private const double PickTolerancePixels = 5;
    private const double MinTextFontSize = 0.5;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Pen SelectionPen = new(Brushes.Yellow, 2);

    // T9 绘制预览：待定点十字 + 到光标的橡皮筋线（Line 工具第一击后）。
    private static readonly Pen PreviewPen = new(Brushes.Yellow, 1);

    // T6 对比叠加：before 灰幽灵 / added 绿 / removed 红 + 删除线。
    private static readonly Pen DiffBeforePen = new(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1);
    private static readonly Pen DiffAddedPen = new(new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53)), 2);
    private static readonly Pen DiffRemovedPen = new(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 1);

    private readonly Dictionary<uint, Pen> _penCache = new();
    private readonly Dictionary<uint, IBrush> _brushCache = new();
    private bool _panning;
    private Point _lastPanPosition;
    private Size _lastFitSize;

    /// <summary>T9：光标屏幕位置（仅 PendingPoint 存在时跟踪，预览橡皮筋用）。</summary>
    private Point? _hoverScreen;

    public DrawingCanvas(CanvasViewModel viewModel)
    {
        ViewModel = viewModel;
        SizeChanged += OnSizeChanged;
        Focusable = true;
        // T10（真机验证发现）：缩放/平移后图元可超出控件边界——裁剪到自身布局槽，
        // 否则内容溢出绘制到图层/签认等相邻面板之上。
        ClipToBounds = true;
    }

    /// <summary>画布视图模型（纯 C#，可单测）。</summary>
    public CanvasViewModel ViewModel { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ViewModel.RenderInvalidated += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ViewModel.RenderInvalidated -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0 || e.NewSize == _lastFitSize) return;
        _lastFitSize = e.NewSize;
        ViewModel.Fit(RenderModelBuilder.Bounds(ViewModel.Items), e.NewSize.Width, e.NewSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        // T6 对比叠加：先画快照 before 幽灵层（灰；removed 红 + 删除线），
        // 再画当前 after 层（added 绿优先于选中黄，其余正常/高亮）。
        DiffOverlay? diff = ViewModel.DiffOverlay;
        if (diff is not null)
        {
            // 对比模式刻意忽略 HiddenLayers：before 幽灵层直接遍历 diff.BeforeItems
            // （快照全量），不走 VisibleItems——隐藏图层的历史状态也须可见才能完整对比。
            foreach (RenderItem before in diff.BeforeItems)
            {
                bool removed = before.Handle.Length > 0 && diff.RemovedHandles.Contains(before.Handle);
                Pen pen = removed ? DiffRemovedPen : DiffBeforePen;
                DrawItem(context, before, pen);
                if (removed) DrawStrike(context, before);
            }
        }

        RenderItem? selected = ViewModel.SelectedItem;
        foreach (RenderItem item in ViewModel.VisibleItems)
        {
            // T5：问题定位高亮集命中的图元与单选选中同样施加高亮（按真实句柄匹配）。
            bool highlight = IsSelected(item, selected)
                || (item.Handle.Length > 0 && ViewModel.HighlightedHandles.Contains(item.Handle));
            bool added = diff is not null && item.Handle.Length > 0 && diff.AddedHandles.Contains(item.Handle);
            Pen pen = added ? DiffAddedPen : highlight ? SelectionPen : GetPen(item.ColorRgb);
            DrawItem(context, item, pen);
        }

        // T9 绘制预览：待定锚点十字标记 + 到当前光标的橡皮筋线（只读叠加，不参与命中）。
        if (ViewModel.PendingPoint is (double px, double py))
        {
            Point anchor = ScreenPoint((px, py));
            if (_hoverScreen is Point hover) context.DrawLine(PreviewPen, anchor, hover);
            context.DrawLine(PreviewPen, new Point(anchor.X - 6, anchor.Y), new Point(anchor.X + 6, anchor.Y));
            context.DrawLine(PreviewPen, new Point(anchor.X, anchor.Y - 6), new Point(anchor.X, anchor.Y + 6));
        }
    }

    /// <summary>
    /// T6 removed 删除线：文本按字盒中线横划（宽=字符数×字高×0.6，与命中盒一致）；
    /// 其余图元沿包围盒对角线划叉（单线图元退化为端点连线时仍可见斜划）。
    /// </summary>
    private void DrawStrike(DrawingContext context, RenderItem item)
    {
        if (item.Kind == RenderKind.Text)
        {
            if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) return;
            double fontSize = item.TextHeight * ViewModel.Scale;
            double width = item.Text.Length * item.TextHeight * 0.6 * ViewModel.Scale;
            if (fontSize <= 0 || width <= 0) return;
            Point p = ScreenPoint(item.Points[0]);
            double y = p.Y - fontSize / 2; // 基线上方半字高 ≈ 字形中线
            context.DrawLine(DiffRemovedPen, new Point(p.X, y), new Point(p.X + width, y));
            return;
        }
        (double minX, double minY, double maxX, double maxY) = RenderModelBuilder.Bounds(new[] { item });
        if (maxX - minX <= 1e-12 && maxY - minY <= 1e-12) return; // 退化点无删除线
        Point a = ScreenPoint((minX, minY));
        Point b = ScreenPoint((maxX, maxY));
        context.DrawLine(DiffRemovedPen, a, b);
    }

    /// <summary>T4：按真实 DXF 句柄匹配选中图元（句柄唯一）；空句柄退化为引用比较。</summary>
    private static bool IsSelected(RenderItem item, RenderItem? selected) =>
        selected is not null
        && ((selected.Handle.Length > 0 && item.Handle == selected.Handle) || ReferenceEquals(item, selected));

    private void DrawItem(DrawingContext context, RenderItem item, Pen pen)
    {
        if (item.DashPattern is { Count: > 0 } dp)
        {
            // drawing 单位 → 设备像素 → pen 厚度倍数（Avalonia DashStyle 口径）。
            // 亚像素图案（任一段 < 0.25px）→实线降级防摩尔纹；超长图案总量守卫防性能退化。
            double[] device = dp.Select(d => d * ViewModel.Scale).ToArray();
            if (device.All(d => d >= 0.25) && device.Sum() < 1e6)
                pen = new Pen(pen.Brush, pen.Thickness)
                {
                    DashStyle = new DashStyle(device.Select(d => d / pen.Thickness), 0)
                };
        }

        switch (item.Kind)
        {
            case RenderKind.Line:
                if (item.Points.Count >= 2)
                    context.DrawLine(pen, ScreenPoint(item.Points[0]), ScreenPoint(item.Points[1]));
                break;

            case RenderKind.Circle:
            {
                if (item.Points.Count < 2) break;
                double r = Math.Abs(item.Points[1].X) * ViewModel.Scale;
                if (r > 0) context.DrawEllipse(null, pen, ScreenPoint(item.Points[0]), r, r);
                break;
            }

            case RenderKind.Arc:
                DrawArc(context, item, pen);
                break;

            case RenderKind.Polyline:
            {
                if (item.Points.Count < 2) break;
                var geo = new StreamGeometry();
                using (StreamGeometryContext g = geo.Open())
                {
                    g.BeginFigure(ScreenPoint(item.Points[0]), false);
                    for (int i = 1; i < item.Points.Count; i++) g.LineTo(ScreenPoint(item.Points[i]));
                    // Closed=true（整圆 tessellation）连回首点闭合，消除弦缺口；
                    // 默认 false 保持开放——既有闭合多段线重复首点自闭合，行为不变。
                    g.EndFigure(item.Closed);
                }
                context.DrawGeometry(null, pen, geo);
                break;
            }

            case RenderKind.Text:
            {
                if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) break;
                double fontSize = item.TextHeight * ViewModel.Scale;
                if (fontSize < MinTextFontSize) break; // 亚像素文本不可读，跳过
                // 选中判定按 brush 引用：dash 克隆 pen 保留原 brush（SelectionPen
                // 用 Brushes.Yellow 单例，普通/对比 pen 均为独立实例），克隆后仍成立。
                bool isSelected = ReferenceEquals(pen.Brush, SelectionPen.Brush);
                // pen.Brush：普通 pen 即图元色；选中黄 / T6 对比灰/绿/红随 pen 传递。
                var text = new FormattedText(
                    item.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, isSelected ? FontWeight.Bold : FontWeight.Normal),
                    fontSize, pen.Brush!);
                Point p = ScreenPoint(item.Points[0]);
                // CAD 插入点=基线左端；FormattedText 以左上角定位 → 上移文本高度。
                context.DrawText(text, new Point(p.X, p.Y - text.Height));
                break;
            }

            case RenderKind.Hatch:
            {
                // Hatch Points 约定：各边界路径以 (NaN,NaN) 分隔（RenderItem XML doc）。
                // 逐路径 BeginFigure/EndFigure(true) 闭合 + FillRule.EvenOdd → 跨路径
                // 挖岛（内环为洞）。实心（Fill=true）填充+描边；图案填充（非 solid）
                // 仅描边界轮廓——图案线生成 deferred（spec 注记，不假装已渲染图案）。
                if (item.Points.Count < 3) break;
                var geo = new StreamGeometry();
                using (StreamGeometryContext g = geo.Open())
                {
                    // Avalonia 11 无 StreamGeometry.FillRule 属性——填充规则经上下文
                    // SetFillRule 设置（须在 BeginFigure 之前；默认即 EvenOdd，显式钉住）。
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
                        if (!inFigure) { g.BeginFigure(ScreenPoint((x, y)), item.Fill); inFigure = true; }
                        else g.LineTo(ScreenPoint((x, y)));
                    }
                    if (inFigure) g.EndFigure(true);
                }
                context.DrawGeometry(item.Fill ? pen.Brush : null, pen, geo);
                break;
            }
        }
    }

    /// <summary>
    /// Arc 点约定（T2）：Points[0]=圆心，Points[1]=(半径,起始角°)，Points[2]=(终止角°,0)，
    /// 角度 CCW。世界 CCW 经 Y 翻转后在屏幕上为视觉逆时针 → SweepDirection.CounterClockwise；
    /// 扫掠角 &gt; 180° → isLargeArc。start==end（扫掠 0）按整圆渲染（与 VM 命中一致）。
    /// </summary>
    private void DrawArc(DrawingContext context, RenderItem item, Pen pen)
    {
        if (item.Points.Count < 3) return;
        (double cx, double cy) = item.Points[0];
        double r = Math.Abs(item.Points[1].X);
        double screenRadius = r * ViewModel.Scale;
        if (screenRadius <= 0) return;

        double startDeg = item.Points[1].Y;
        double endDeg = item.Points[2].X;
        double span = NormDeg(endDeg - startDeg);
        Point center = ScreenPoint(item.Points[0]);
        if (span < 1e-9)
        {
            context.DrawEllipse(null, pen, center, screenRadius, screenRadius);
            return;
        }

        double a0 = startDeg * Math.PI / 180.0;
        double a1 = endDeg * Math.PI / 180.0;
        Point start = ScreenPoint((cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)));
        Point end = ScreenPoint((cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)));
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(screenRadius, screenRadius), 0, span > 180, SweepDirection.CounterClockwise);
        }
        context.DrawGeometry(null, pen, geo);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point p = e.GetPosition(this);
        ViewModel.ZoomAt(Math.Pow(WheelZoomBase, e.Delta.Y), p.X, p.Y);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _lastPanPosition = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            (double wx, double wy) = ViewModel.ScreenToWorld(point.Position.X, point.Position.Y);
            // T9：工具点击拦截（MainWindow 按 ActiveTool 路由）；未消费 → 默认选择。
            if (ViewModel.ToolClickHandler is not { } handler || !handler(wx, wy))
            {
                double tolerance = PickTolerancePixels / ViewModel.Scale;
                ViewModel.Select(ViewModel.HitTest(wx, wy, tolerance));
            }
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_panning)
        {
            // T9：有待定预览锚点时跟踪光标（橡皮筋线随动）。
            if (ViewModel.PendingPoint is not null)
            {
                _hoverScreen = e.GetPosition(this);
                InvalidateVisual();
            }
            return;
        }
        Point p = e.GetPosition(this);
        ViewModel.PanBy(p.X - _lastPanPosition.X, p.Y - _lastPanPosition.Y);
        _lastPanPosition = p;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panning = false; // 捕获意外丢失（窗口失焦等）→ 退出拖拽，避免粘滞平移
    }

    private Point ScreenPoint((double X, double Y) world)
    {
        (double x, double y) = ViewModel.WorldToScreen(world.X, world.Y);
        return new Point(x, y);
    }

    private Pen GetPen(uint rgb)
    {
        if (!_penCache.TryGetValue(rgb, out Pen? pen))
        {
            pen = new Pen(GetBrush(rgb), 1);
            _penCache[rgb] = pen;
        }
        return pen;
    }

    private IBrush GetBrush(uint rgb)
    {
        if (!_brushCache.TryGetValue(rgb, out IBrush? brush))
        {
            brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _brushCache[rgb] = brush;
        }
        return brush;
    }

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;
}
