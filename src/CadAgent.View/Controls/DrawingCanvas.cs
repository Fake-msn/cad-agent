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
/// →HitTest（5px 容差换算世界单位）→Select。选中图元黄色 2px 高亮（文本黄色加粗）。
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

    private readonly Dictionary<uint, Pen> _penCache = new();
    private readonly Dictionary<uint, IBrush> _brushCache = new();
    private bool _panning;
    private Point _lastPanPosition;
    private Size _lastFitSize;

    public DrawingCanvas(CanvasViewModel viewModel)
    {
        ViewModel = viewModel;
        SizeChanged += OnSizeChanged;
        Focusable = true;
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

        RenderItem? selected = ViewModel.SelectedItem;
        foreach (RenderItem item in ViewModel.VisibleItems)
        {
            Pen pen = IsSelected(item, selected) ? SelectionPen : GetPen(item.ColorRgb);
            DrawItem(context, item, pen);
        }
    }

    /// <summary>T4：按真实 DXF 句柄匹配选中图元（句柄唯一）；空句柄退化为引用比较。</summary>
    private static bool IsSelected(RenderItem item, RenderItem? selected) =>
        selected is not null
        && ((selected.Handle.Length > 0 && item.Handle == selected.Handle) || ReferenceEquals(item, selected));

    private void DrawItem(DrawingContext context, RenderItem item, Pen pen)
    {
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
                }
                context.DrawGeometry(null, pen, geo);
                break;
            }

            case RenderKind.Text:
            {
                if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) break;
                double fontSize = item.TextHeight * ViewModel.Scale;
                if (fontSize < MinTextFontSize) break; // 亚像素文本不可读，跳过
                bool isSelected = ReferenceEquals(pen, SelectionPen);
                var text = new FormattedText(
                    item.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, isSelected ? FontWeight.Bold : FontWeight.Normal),
                    fontSize, isSelected ? SelectionPen.Brush! : GetBrush(item.ColorRgb));
                Point p = ScreenPoint(item.Points[0]);
                // CAD 插入点=基线左端；FormattedText 以左上角定位 → 上移文本高度。
                context.DrawText(text, new Point(p.X, p.Y - text.Height));
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
            double tolerance = PickTolerancePixels / ViewModel.Scale;
            ViewModel.Select(ViewModel.HitTest(wx, wy, tolerance));
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_panning) return;
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
