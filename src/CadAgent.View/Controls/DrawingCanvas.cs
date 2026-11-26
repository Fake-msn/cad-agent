using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Controls;

/// <summary>
/// 画布控件（计划4 T3；项3 T2 改走统一绘制器）：渲染 CanvasViewModel 当前视口内图元
/// （深色背景 #1E1E1E；视口裁剪 + LOD 由 RenderItemDrawer 执行），
/// 变换/命中全部委托 VM（WorldToScreen/ScreenToWorld/HitTest）。
/// 指针：滚轮→ZoomAt（光标锚点）；中键拖拽→PanBy（屏幕增量）；左键→ScreenToWorld
/// →T9 工具拦截（ToolClickHandler，MainWindow 按 ActiveTool 路由）→未消费则
/// HitTest（5px 容差换算世界单位）→Select。选中图元黄色 2px 高亮（文本黄色加粗）。
/// T9 绘制预览：PendingPoint 十字标记 + 到光标的橡皮筋线（Line 工具第一击）。
/// 天读证据：CanvasViewModel.ShowCenterlines 开时叠加青色虚线中心线（只读，不参与命中）。
/// 尺寸变化→Fit（重置 zoom/pan，T10 可升级为保持视图）。线宽恒定设备像素（CAD 习惯）。
/// 文本按水平渲染（RotationDeg 暂不参与，与 VM 文本命中盒一致）；插入点视为基线左端。
/// </summary>
public sealed class DrawingCanvas : Control
{
    private const double WheelZoomBase = 1.2;
    private const double PickTolerancePixels = 5;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    // T9 绘制预览：待定点十字 + 到光标的橡皮筋线（Line 工具第一击后）。
    private static readonly Pen PreviewPen = new(Brushes.Yellow, 1);

    // T6 对比叠加：before 灰幽灵 / added 绿 / removed 红 + 删除线。
    private static readonly Pen DiffBeforePen = new(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1);
    private static readonly Pen DiffAddedPen = new(new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53)), 2);
    private static readonly Pen DiffRemovedPen = new(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 1);

    // 天读证据：中心线只读叠加（青色虚线）。DashStyle 图案按笔宽倍数（Avalonia 口径），
    // 恒定设备像素——中心线数据无宽度语义，不随缩放换算。
    private static readonly Pen CenterlinePen = new(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), 2)
    {
        DashStyle = new DashStyle(new double[] { 4, 2 }, 0)
    };

    // 项3 T2：统一绘制器 + 画笔/文字缓存（帧内复用；Task 3 位图缓存层复用同一 Resolver）。
    private readonly RenderItemDrawer _drawer = new();
    private readonly PenResolver _pens = new();
    private readonly TextCache _texts = new();
    private IReadOnlyList<RenderItem>? _textMarginItems;
    private double _textCullingMarginWorld;
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

    /// <summary>质量档（项3 §3.1）：默认 Refined；Task 3 交互期切 Interactive、静止 150ms 后回 Refined。</summary>
    internal RenderQuality Quality { get; set; } = RenderQuality.Refined;

    /// <summary>测试 seam（Task 3 计时器接线前）：headless 直接切档，不依赖真实 150ms。</summary>
    internal void SetQualityForTests(RenderQuality quality) => Quality = quality;

    /// <summary>状态栏注记占位（Task 3/5 写入「概览」「高亮为包围盒…」）。</summary>
    internal string? LastRenderNote { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        // 项3 T2：帧开始冻结变换快照，绘制器不再回读 VM。
        CanvasProjection projection = CanvasProjection.From(ViewModel);

        // T6 对比叠加：before 幽灵层 + after 层（diff 模式保持全量语义，暂不走裁剪/位图缓存）。
        DiffOverlay? diff = ViewModel.DiffOverlay;
        if (diff is not null) RenderDiff(context, projection, diff);
        else RenderBase(context, projection);

        // 天读证据：中心线叠加（ShowCenterlines 开时绘制；只读，不参与命中/选择）。
        if (ViewModel.ShowCenterlines)
            foreach (CenterlineSeg seg in ViewModel.Centerlines)
                context.DrawLine(CenterlinePen,
                    ScreenPoint(projection, (seg.X1, seg.Y1)), ScreenPoint(projection, (seg.X2, seg.Y2)));

        // T9 绘制预览：待定锚点十字标记 + 到当前光标的橡皮筋线（只读叠加，不参与命中）。
        if (ViewModel.PendingPoint is (double px, double py))
        {
            Point anchor = ScreenPoint(projection, (px, py));
            if (_hoverScreen is Point hover) context.DrawLine(PreviewPen, anchor, hover);
            context.DrawLine(PreviewPen, new Point(anchor.X - 6, anchor.Y), new Point(anchor.X + 6, anchor.Y));
            context.DrawLine(PreviewPen, new Point(anchor.X, anchor.Y - 6), new Point(anchor.X, anchor.Y + 6));
        }
    }

    /// <summary>
    /// 主路径（项3 T2）：视口裁剪（索引查询）+ LOD；选中/高亮由绘制器最后重绘（受预算）。
    /// </summary>
    private void RenderBase(DrawingContext context, CanvasProjection projection)
    {
        RenderItem? selected = ViewModel.SelectedItem;
        IReadOnlyList<int>? indices = QueryIndices(projection);
        bool selectionBudgetExceeded = selected is not null && RenderItemDrawer.IsOverBudget(projection, selected);
        _drawer.Draw(context, projection, ViewModel.Items, indices, Quality, _pens, _texts,
            selected, ViewModel.HighlightedHandles, selectionBudgetExceeded);
    }

    /// <summary>
    /// T6 对比路径（保持旧语义/顺序）：before 幽灵层（removed 红 + 删除线），再按 Items 序
    /// 画 after 层（added 绿优先于选中黄）；逐图元复用 <see cref="RenderItemDrawer.DrawOne"/>。
    /// </summary>
    private void RenderDiff(DrawingContext context, CanvasProjection projection, DiffOverlay diff)
    {
        // 对比模式刻意忽略 HiddenLayers：before 幽灵层直接遍历 diff.BeforeItems
        // （快照全量），不走 VisibleItems——隐藏图层的历史状态也须可见才能完整对比。
        foreach (RenderItem before in diff.BeforeItems)
        {
            bool removed = before.Handle.Length > 0 && diff.RemovedHandles.Contains(before.Handle);
            Pen pen = removed ? DiffRemovedPen : DiffBeforePen;
            _drawer.DrawOne(context, projection, before, pen, Quality);
            if (removed) DrawStrike(context, projection, before);
        }

        RenderItem? selected = ViewModel.SelectedItem;
        foreach (RenderItem item in ViewModel.VisibleItems)
        {
            // T5：问题定位高亮集命中的图元与单选选中同样施加高亮（按真实句柄匹配）。
            bool highlight = IsSelected(item, selected)
                || (item.Handle.Length > 0 && ViewModel.HighlightedHandles.Contains(item.Handle));
            bool added = item.Handle.Length > 0 && diff.AddedHandles.Contains(item.Handle);
            Pen pen = added ? DiffAddedPen : highlight ? RenderItemDrawer.SelectionPen : _pens.ColorPen(item.ColorRgb);
            _drawer.DrawOne(context, projection, item, pen, Quality);
        }
    }

    /// <summary>
    /// 视口索引查询：视口全覆盖内容且无隐藏层 → null（绘制器全量，省一次查询）；
    /// 否则按世界视口查询（隐藏层在索引内过滤）。文本剔除按 T1 评审留字形上升边距
    /// （绘制 ≈1.2×字高，索引盒只含逻辑字盒），边距单位=世界单位。
    /// </summary>
    private IReadOnlyList<int>? QueryIndices(CanvasProjection projection)
    {
        (double minX, double minY, double maxX, double maxY) = projection.WorldViewport();
        if (!(minX <= maxX && minY <= maxY)) return null; // 未 Fit：无有效视口（画布零尺寸）
        if (ViewModel.HiddenLayers.Count == 0)
        {
            (double bMinX, double bMinY, double bMaxX, double bMaxY) = RenderModelBuilder.Bounds(ViewModel.Items);
            if (minX <= bMinX && minY <= bMinY && maxX >= bMaxX && maxY >= bMaxY) return null;
        }
        double margin = TextCullingMarginWorld();
        return ViewModel.QueryViewport((minX - margin, minY - margin, maxX + margin, maxY + margin));
    }

    /// <summary>文本剔除边距 = 1.2×最大世界字高（Items 引用变化时重算一次）。</summary>
    private double TextCullingMarginWorld()
    {
        if (!ReferenceEquals(_textMarginItems, ViewModel.Items))
        {
            double maxHeight = 0;
            foreach (RenderItem item in ViewModel.Items)
                if (item.Kind == RenderKind.Text && item.TextHeight > maxHeight) maxHeight = item.TextHeight;
            _textCullingMarginWorld = 1.2 * maxHeight;
            _textMarginItems = ViewModel.Items;
        }
        return _textCullingMarginWorld;
    }

    /// <summary>
    /// T6 removed 删除线：文本按字盒中线横划（宽=字符数×字高×0.6，与命中盒一致）；
    /// 其余图元沿包围盒对角线划叉（单线图元退化为端点连线时仍可见斜划）。
    /// </summary>
    private void DrawStrike(DrawingContext context, CanvasProjection projection, RenderItem item)
    {
        if (item.Kind == RenderKind.Text)
        {
            if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) return;
            double fontSize = item.TextHeight * projection.Scale;
            double width = item.Text.Length * item.TextHeight * 0.6 * projection.Scale;
            if (fontSize <= 0 || width <= 0) return;
            Point p = ScreenPoint(projection, item.Points[0]);
            double y = p.Y - fontSize / 2; // 基线上方半字高 ≈ 字形中线
            context.DrawLine(DiffRemovedPen, new Point(p.X, y), new Point(p.X + width, y));
            return;
        }
        (double minX, double minY, double maxX, double maxY) = RenderModelBuilder.Bounds(new[] { item });
        if (maxX - minX <= 1e-12 && maxY - minY <= 1e-12) return; // 退化点无删除线
        Point a = ScreenPoint(projection, (minX, minY));
        Point b = ScreenPoint(projection, (maxX, maxY));
        context.DrawLine(DiffRemovedPen, a, b);
    }

    /// <summary>T4：按真实 DXF 句柄匹配选中图元（句柄唯一）；空句柄退化为引用比较。</summary>
    private static bool IsSelected(RenderItem item, RenderItem? selected) =>
        selected is not null
        && ((selected.Handle.Length > 0 && item.Handle == selected.Handle) || ReferenceEquals(item, selected));

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

    private static Point ScreenPoint(CanvasProjection projection, (double X, double Y) world) =>
        ScreenPoint(projection, world.X, world.Y);

    private static Point ScreenPoint(CanvasProjection projection, double worldX, double worldY)
    {
        (double x, double y) = projection.WorldToScreen(worldX, worldY);
        return new Point(x, y);
    }
}
