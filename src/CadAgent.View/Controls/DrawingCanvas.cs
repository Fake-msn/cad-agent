using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Controls;

/// <summary>
/// 画布控件（计划4 T3；项3 T2 改走统一绘制器；T3 位图平移缓存 + 两阶段精绘）：渲染
/// CanvasViewModel 当前视口内图元（深色背景 #1E1E1E；视口裁剪 + LOD 由 RenderItemDrawer 执行），
/// 变换/命中全部委托 VM（WorldToScreen/ScreenToWorld/HitTest）。
/// 项3 T3 渲染顺序：基元（命中缓存 → blit 缓存位图；否则重建）→ 中心线/预览等只读叠加；
/// 选中/高亮叠加实时绘制（不烘焙，受 §3.2 预算）；diff 模式直绘并绕过缓存。
/// 交互期（滚轮/中键拖动/编辑工具点击）Quality=Interactive，静止 150ms 后 Refined 重建缓存（两阶段）；
/// 交互期缩放复用旧位图（按比例变换），平移在 128px 外扩内只做 blit，越界/失效重建。
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

    /// <summary>位图缓存四周外扩（DIP；平移超出即重建）。</summary>
    private const double CacheMargin = 128;

    /// <summary>交互静止多久后回到 Refined 精绘（毫秒；可经 <see cref="IdleRefineMs"/> 覆盖）。</summary>
    private const int DefaultIdleRefineMs = 150;

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
    private double _textMarginAscentWorld;
    private double _textMarginRightWorld;
    private bool _panning;
    private Point _lastPanPosition;
    private Size _lastFitSize;

    // 项3 T3 位图缓存：缓存层单独一个绘制器（避免覆盖本帧 LOD/降级计数），画笔/文字缓存共享。
    private readonly RenderItemDrawer _cacheDrawer = new();
    private RenderTargetBitmap? _cacheBitmap;
    private IReadOnlyList<RenderItem>? _cacheItems;
    private double _cacheScale = 1;
    private (double X, double Y) _cachePan;
    private Size _cacheBounds;
    private Size _cacheViewport;
    private Size _cacheLogicalSize;
    private double _cacheDpi = 1;
    private int _cacheHiddenHash;
    private RenderQuality _cacheQuality = RenderQuality.Refined;
    private (double MinX, double MinY, double OffsetX, double OffsetY) _cacheOrigin;

    // 两阶段交互：交互标记 + 静止计时器（150ms 后 Refined 重建缓存）。
    // 截止时刻用 Environment.TickCount64（单调钟；墙钟回拨不会提前/推迟精绘）。
    private bool _interacting;
    private long _interactionUntil;
    private DispatcherTimer? _idleTimer;

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
        StopIdleTimer();
        // 单缓存：脱离即释放位图与锚点（不跨树/跨快照复用陈旧位图），重挂后按需重建。
        DisposeCache();
        _interacting = false;
        Quality = RenderQuality.Refined;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0 || e.NewSize == _lastFitSize) return;
        _lastFitSize = e.NewSize;
        ViewModel.Fit(RenderModelBuilder.Bounds(ViewModel.Items), e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>质量档（项3 §3.1）：默认 Refined；交互期切 Interactive、静止 150ms 后回 Refined。</summary>
    internal RenderQuality Quality { get; set; } = RenderQuality.Refined;

    /// <summary>测试 seam：headless 直接切档，不依赖真实 150ms 计时器。</summary>
    internal void SetQualityForTests(RenderQuality quality) => Quality = quality;

    /// <summary>状态栏注记（「概览」/「高亮为包围盒（图元过密）」）；MainWindow 读取（Task 5 接线）。</summary>
    internal string? LastRenderNote { get; private set; }

    /// <summary>测试 seam：直绘对照（关缓存），用于缓存 blit 与直绘的像素等价断言。</summary>
    internal bool DisableCacheForTests { get; set; }

    /// <summary>基元层从零绘制的次数（缓存重建/直绘）；平移复用缓存时不变（测试 seam）。</summary>
    internal int BaseRenderCount { get; private set; }

    /// <summary>测试 seam：同步走「静止 → Refined 重建缓存」路径（与计时器到点同一实现）。</summary>
    internal void ForceRefinedForTests() => RefineNow();

    /// <summary>静止精绘延迟（毫秒）；测试置 0 → 不装真实计时器（交互行为仍可验）。</summary>
    internal int IdleRefineMs { get; set; } = DefaultIdleRefineMs;

    /// <summary>测试 seam：静止计时器是否在跑（RefineNow/detach/Close 后须为 false）。</summary>
    internal bool HasIdleTimer => _idleTimer is not null;

    /// <summary>测试 seam：是否驻留缓存位图（未挂树/直绘/diff 路径须为 false）。</summary>
    internal bool HasCacheBitmap => _cacheBitmap is not null;

    /// <summary>测试 seam：覆盖渲染缩放（headless 无 DPI 控制）；null → 平台值。</summary>
    internal double? RenderScalingForTests { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        // 项3 T2：帧开始冻结变换快照，绘制器不再回读 VM；帧计数只在此清零一次（diff 各 DrawOne 累加）。
        CanvasProjection projection = CanvasProjection.From(ViewModel);
        _drawer.BeginFrame();

        // T6 对比叠加：diff 模式直接绘（全量语义），不进缓存；关 diff 后按需重建。
        DiffOverlay? diff = ViewModel.DiffOverlay;
        if (diff is not null)
        {
            DisposeCache();
            RenderDiff(context, projection, diff);
        }
        else if (TryDrawFromCache(context, projection))
        {
            // 基元来自缓存位图；选中/高亮叠加实时绘制（不烘焙，受 §3.2 预算）。
            DrawSelectionOverlay(context, projection);
        }
        else
        {
            RenderBase(context, projection);
        }

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

        UpdateRenderNote();
    }

    /// <summary>状态注记：叠加降级优先（数据在途），其次交互概览。</summary>
    private void UpdateRenderNote() =>
        LastRenderNote = _drawer.LastSelectionDowngraded ? "高亮为包围盒（图元过密）"
            : Quality == RenderQuality.Interactive ? "概览"
            : null;

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
        BaseRenderCount++;
    }

    /// <summary>缓存 blit 之上的选中/高亮叠加（实时绘制，不进位图）。</summary>
    private void DrawSelectionOverlay(DrawingContext context, CanvasProjection projection)
    {
        RenderItem? selected = ViewModel.SelectedItem;
        if (selected is null && ViewModel.HighlightedHandles.Count == 0) return;
        IReadOnlyList<int>? indices = QueryIndices(projection);
        bool selectionBudgetExceeded = selected is not null && RenderItemDrawer.IsOverBudget(projection, selected);
        _drawer.DrawSelection(context, projection, ViewModel.Items, indices, Quality, _pens, _texts,
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
        BaseRenderCount++;
    }

    /// <summary>
    /// 视口索引查询：视口全覆盖内容且无隐藏层 → null（绘制器全量，省一次查询）；
    /// 否则按世界视口查询（隐藏层在索引内过滤）。内容包围盒取索引构造时预计算值
    /// （O(1)，避免每帧全顶点扫描）。文本剔除留字形边距（绘制 ≈1.2×字高；字盒只含
    /// 逻辑字盒），右侧另按最大文本世界宽度加宽（文本向右延伸），均世界单位。
    /// </summary>
    private IReadOnlyList<int>? QueryIndices(CanvasProjection projection) =>
        QueryWorld(projection.WorldViewport());

    /// <summary>世界矩形 → 可见图元索引（null=全量捷径）；缓存外扩视口复用同一逻辑。</summary>
    private IReadOnlyList<int>? QueryWorld((double MinX, double MinY, double MaxX, double MaxY) rect)
    {
        (double minX, double minY, double maxX, double maxY) = rect;
        if (!(minX <= maxX && minY <= maxY)) return null; // 未 Fit：无有效视口（画布零尺寸）
        if (ViewModel.HiddenLayers.Count == 0)
        {
            (double bMinX, double bMinY, double bMaxX, double bMaxY) = ViewModel.Index.ContentBounds;
            if (!double.IsNaN(bMinX)
                && minX <= bMinX && minY <= bMinY && maxX >= bMaxX && maxY >= bMaxY)
                return null;
        }
        (double ascent, double right) = TextCullingMarginsWorld();
        return ViewModel.QueryViewport((minX - ascent, minY - ascent,
            maxX + Math.Max(ascent, right), maxY + ascent));
    }

    // ---- 项3 T3：位图平移缓存 ----

    /// <summary>缓存可用前提：已挂树（DPI 可查；离屏快照直绘不驻留位图）+ 有效尺寸/视口。</summary>
    private bool CacheAllowed(CanvasProjection projection) =>
        !DisableCacheForTests
        && TopLevel.GetTopLevel(this) is not null
        && Bounds.Width > 0 && Bounds.Height > 0
        && projection.ViewportW > 0 && projection.ViewportH > 0;

    private double CurrentRenderScaling =>
        RenderScalingForTests ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>
    /// 缓存路径：结构键（items 引用/尺寸/DPI/隐藏层）→ 平移复用；缩放变化在交互期缩放旧图；
    /// 其余（尺寸/DPI/隐藏层/items/质量/平移越界）→ 同步重建后 blit。
    /// </summary>
    private bool TryDrawFromCache(DrawingContext context, CanvasProjection projection)
    {
        if (!CacheAllowed(projection)) return false;
        if (!CacheStructureMatches(projection))
        {
            RebuildCache(projection);
            BlitCache(context, projection, scaled: false);
            return true;
        }
        bool scaleSame = Math.Abs(projection.Scale - _cacheScale) <= 1e-12;
        double dx = projection.PanX - _cachePan.X;
        double dy = -(projection.PanY - _cachePan.Y);
        if (scaleSame)
        {
            // 交互期质量档变化不重建（沿用已烘焙的 Refined 位图）；静止后按质量档重建。
            bool qualitySame = _cacheQuality == Quality;
            if ((qualitySame || _interacting)
                && Math.Abs(dx) <= CacheMargin + 1e-6 && Math.Abs(dy) <= CacheMargin + 1e-6)
            {
                BlitCache(context, projection, scaled: false);
                return true;
            }
        }
        else if (_interacting)
        {
            // 交互期缩放：旧位图按 Scale1/Scale0 缩放，静止计时器到点后重建精绘。
            BlitCache(context, projection, scaled: true);
            return true;
        }
        RebuildCache(projection);
        BlitCache(context, projection, scaled: false);
        return true;
    }

    private bool CacheStructureMatches(CanvasProjection projection) =>
        _cacheBitmap is not null
        && ReferenceEquals(_cacheItems, ViewModel.Items)
        && _cacheBounds == new Size(Bounds.Width, Bounds.Height)
        && _cacheViewport == new Size(projection.ViewportW, projection.ViewportH)
        && Math.Abs(_cacheDpi - CurrentRenderScaling) <= 1e-9
        && _cacheHiddenHash == HiddenHash()
        // 原点（世界 min + 视口固定偏移）同属缓存 key：Fit/偏移变化即失效；Pan 另以锚点复用。
        && _cacheOrigin == (projection.MinX, projection.MinY, projection.OffsetX, projection.OffsetY);

    /// <summary>隐藏层集合的内容哈希（排序后聚合，集合增删即失效）。</summary>
    private int HiddenHash()
    {
        if (ViewModel.HiddenLayers.Count == 0) return 0;
        string[] names = ViewModel.HiddenLayers.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        var hash = new HashCode();
        foreach (string name in names) hash.Add(name, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    /// <summary>
    /// 重建缓存位图（UI 线程）：私有缓存层以「背景 + 基元」绘制到 RenderTargetBitmap，
    /// 内容四周外扩 <see cref="CacheMargin"/>（extra offset 令内容右/下各留边距），
    /// 锚点 Pan 冻结在构建时刻，之后平移只做 blit 偏移。
    /// </summary>
    private void RebuildCache(CanvasProjection projection)
    {
        double dpi = CurrentRenderScaling;
        double logicalW = Math.Ceiling(Bounds.Width) + 2 * CacheMargin;
        double logicalH = Math.Ceiling(Bounds.Height) + 2 * CacheMargin;
        var logical = new Size(logicalW, logicalH);
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(logicalW * dpi)),
            Math.Max(1, (int)Math.Ceiling(logicalH * dpi)));

        CanvasProjection cacheProjection = projection with { ExtraOffsetX = CacheMargin, ExtraOffsetY = -CacheMargin };
        IReadOnlyList<int>? indices = QueryWorld(cacheProjection.WorldRectOf(0, 0, logicalW, logicalH));
        var layer = new CacheLayer(cacheProjection, ViewModel.Items, indices, Quality, _cacheDrawer, _pens, _texts);
        layer.Measure(logical);
        layer.Arrange(new Rect(logical));

        // 先释放旧位图再渲染新层：任一时刻只驻留一份缓存（峰值仍为 2×：Skia 渲染目标内部
        // 可能再分配同尺寸表面；DPI=2 时缓存像素约为单屏设备像素的 200%）。
        DisposeCache();
        var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * dpi, 96 * dpi));
        bitmap.Render(layer);

        _cacheBitmap = bitmap;
        _cacheItems = ViewModel.Items;
        _cacheScale = projection.Scale;
        _cachePan = (projection.PanX, projection.PanY);
        _cacheBounds = new Size(Bounds.Width, Bounds.Height);
        _cacheViewport = new Size(projection.ViewportW, projection.ViewportH);
        _cacheLogicalSize = logical;
        _cacheDpi = dpi;
        _cacheHiddenHash = HiddenHash();
        _cacheQuality = Quality;
        _cacheOrigin = (projection.MinX, projection.MinY, projection.OffsetX, projection.OffsetY);
        BaseRenderCount++;
    }

    /// <summary>
    /// blit 缓存位图：平移 → 仅偏移（最近邻，像素保真）；缩放 → 以 Scale1/Scale0 仿射
    /// （与平移同一矩阵公式，f=1 时退化为偏移；Pan 差吸收锚点位移）。
    /// </summary>
    private void BlitCache(DrawingContext context, CanvasProjection projection, bool scaled)
    {
        if (_cacheBitmap is null) return;
        double f = scaled ? projection.Scale / _cacheScale : 1;
        // 源项取重建时刻的原点/锚点（位图 0,0 的世界→屏幕基准），目标用当前平移/缩放。
        double sourceX = CacheMargin + _cachePan.X + _cacheOrigin.OffsetX;
        double sourceY = _cacheViewport.Height - _cacheOrigin.OffsetY + CacheMargin - _cachePan.Y;
        double tx = projection.PanX + projection.OffsetX - f * sourceX;
        double ty = projection.ViewportH - projection.OffsetY - projection.PanY - f * sourceY;
        var dest = new Rect(0, 0, _cacheLogicalSize.Width, _cacheLogicalSize.Height);
        var options = new RenderOptions
        {
            BitmapInterpolationMode = scaled ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None
        };
        using (context.PushTransform(new Matrix(f, 0, 0, f, tx, ty)))
        using (context.PushRenderOptions(options))
            context.DrawImage(_cacheBitmap, dest);
    }

    private void DisposeCache()
    {
        _cacheBitmap?.Dispose();
        _cacheBitmap = null;
        _cacheItems = null;
    }

    /// <summary>
    /// 交互标记（滚轮/中键拖动）：切 Interactive 并重置 150ms 静止计时器；
    /// 静止到点 → Refined 重建缓存（两阶段精绘）。
    /// </summary>
    private void BeginInteraction()
    {
        _interacting = true;
        Quality = RenderQuality.Interactive;
        _interactionUntil = Environment.TickCount64 + IdleRefineMs;
        if (IdleRefineMs <= 0) return; // 测试：不装真实计时器（ForceRefinedForTests 驱动精绘）
        if (_idleTimer is null)
        {
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleRefineMs) };
            _idleTimer.Tick += OnIdleTick;
        }
        _idleTimer.Stop();
        _idleTimer.Interval = TimeSpan.FromMilliseconds(IdleRefineMs);
        _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        long remaining = _interactionUntil - Environment.TickCount64;
        if (remaining > 0)
        {
            _idleTimer!.Stop();
            _idleTimer.Interval = TimeSpan.FromMilliseconds(remaining); // 提前触发 → 补足静止时间
            _idleTimer.Start();
            return;
        }
        StopIdleTimer();
        RefineNow();
    }

    /// <summary>静止精绘：先停表（不得再触发一次精绘）、回 Refined、立即重建缓存并请求重绘。</summary>
    private void RefineNow()
    {
        StopIdleTimer();
        _interacting = false;
        Quality = RenderQuality.Refined;
        if (_cacheBitmap is not null && Bounds.Width > 0 && Bounds.Height > 0)
            RebuildCache(CanvasProjection.From(ViewModel));
        InvalidateVisual();
    }

    private void StopIdleTimer()
    {
        if (_idleTimer is null) return;
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTick;
        _idleTimer = null;
    }

    /// <summary>缓存层：私有 Control，只画背景 + 基元（无选中/高亮叠加/中心线/预览/diff）。</summary>
    private sealed class CacheLayer : Control
    {
        private readonly RenderItemDrawer _drawer;
        private readonly PenResolver _pens;
        private readonly TextCache _texts;

        public CacheLayer(CanvasProjection projection, IReadOnlyList<RenderItem> items,
            IReadOnlyList<int>? indices, RenderQuality quality,
            RenderItemDrawer drawer, PenResolver pens, TextCache texts)
        {
            Projection = projection;
            Items = items;
            Indices = indices;
            Quality = quality;
            _drawer = drawer;
            _pens = pens;
            _texts = texts;
            IsHitTestVisible = false;
        }

        public CanvasProjection Projection { get; }
        public IReadOnlyList<RenderItem> Items { get; }
        public IReadOnlyList<int>? Indices { get; }
        public RenderQuality Quality { get; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
            _drawer.BeginFrame();
            _drawer.Draw(context, Projection, Items, Indices, Quality, _pens, _texts, null, null);
        }
    }

    /// <summary>
    /// 文本剔除边距（Items 引用变化时重算一次）：上升边距=1.2×最大世界字高（字形超出
    /// 逻辑字盒）；右向边距=最大文本世界宽度（len×字高×0.6，文本自插入点向右延伸）。
    /// </summary>
    private (double Ascent, double Right) TextCullingMarginsWorld()
    {
        if (!ReferenceEquals(_textMarginItems, ViewModel.Items))
        {
            double maxHeight = 0, maxWidth = 0;
            foreach (RenderItem item in ViewModel.Items)
            {
                if (item.Kind != RenderKind.Text) continue;
                if (item.TextHeight > maxHeight) maxHeight = item.TextHeight;
                double width = (item.Text?.Length ?? 0) * item.TextHeight * 0.6;
                if (width > maxWidth) maxWidth = width;
            }
            _textMarginAscentWorld = 1.2 * maxHeight;
            _textMarginRightWorld = maxWidth;
            _textMarginItems = ViewModel.Items;
        }
        return (_textMarginAscentWorld, _textMarginRightWorld);
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
        BeginInteraction(); // 两阶段：缩放期 Interactive（旧位图缩放），静止 150ms 后精绘
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
            BeginInteraction();
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            // 编辑工具点击同属交互（brief：滚轮/中键拖动/工具点击触发）——先切交互档，
            // 避免点击瞬间同步重建整幅基元缓存；静止 150ms 后精绘。
            if (ViewModel.ToolClickHandler is not null) BeginInteraction();
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
        BeginInteraction();
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
