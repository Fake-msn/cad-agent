using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 2: RenderItemDrawer + PenResolver + TextCache。
/// 纯逻辑（决策阈值/抽稀/缓存）用 [Fact]；像素断言用 [AvaloniaFact] + 直绘宿主
/// （RenderItemDrawer 只依赖 DrawingContext，无控件依赖）。
/// LOD 语义：显示降级不改数据；像素断言只钉「画了什么/没画什么」。
/// </summary>
public class RenderItemDrawerTests
{
    private static readonly Color Background = Color.FromRgb(0x1E, 0x1E, 0x1E);

    // ---- fixtures ----

    private static RenderItem Line(double x0, double y0, double x1, double y1, uint rgb = 0xFFFFFF, string handle = "1") =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, "0", rgb, null, 0, 0, handle);

    private static RenderItem Text(string value, double x, double y, double height, uint rgb = 0xFFFFFF) =>
        new(RenderKind.Text, new[] { (x, y) }, "0", rgb, value, height, 0, "T");

    private static RenderItem Hatch(uint rgb, bool fill, params (double X, double Y)[] pts) =>
        new(RenderKind.Hatch, pts, "0", rgb, null, 0, 0, "H", Fill: fill);

    private static RenderItem HatchMany(uint rgb, bool fill, int count, (double X, double Y)[] corners) =>
        new(RenderKind.Hatch, Enumerable.Range(0, count).Select(i => corners[i % corners.Length]).ToArray(),
            "0", rgb, null, 0, 0, "H", Fill: fill);

    /// <summary>固定投影：20px/世界单位，200×200 视口（世界原点 → 屏幕左下）。</summary>
    private static CanvasProjection Projection(double scale = 20) =>
        new(0, 0, scale, 0, 0, 0, 0, 200, 200);

    // ---- decision thresholds ----

    [Theory]
    [InlineData(1.99, RenderQuality.Refined, RenderDecision.Skip)]
    [InlineData(2.0, RenderQuality.Refined, RenderDecision.Simplified)]
    [InlineData(5.99, RenderQuality.Refined, RenderDecision.Simplified)]
    [InlineData(6.0, RenderQuality.Refined, RenderDecision.Full)]
    [InlineData(6.01, RenderQuality.Refined, RenderDecision.Full)]
    [InlineData(3.99, RenderQuality.Interactive, RenderDecision.Skip)]
    [InlineData(4.0, RenderQuality.Interactive, RenderDecision.Simplified)]
    [InlineData(9.99, RenderQuality.Interactive, RenderDecision.Simplified)]
    [InlineData(10.0, RenderQuality.Interactive, RenderDecision.Full)]
    public void DecideText_Boundaries(double px, RenderQuality q, RenderDecision expected) =>
        Assert.Equal(expected, RenderItemDrawer.DecideText(px, q));

    [Theory]
    [InlineData(10, 3.99, RenderQuality.Refined, RenderDecision.Simplified)]
    [InlineData(10, 4.0, RenderQuality.Refined, RenderDecision.Full)]
    [InlineData(2000, 100.0, RenderQuality.Refined, RenderDecision.Full)]
    [InlineData(2001, 100.0, RenderQuality.Refined, RenderDecision.Simplified)]
    [InlineData(500, 100.0, RenderQuality.Interactive, RenderDecision.Full)]
    [InlineData(501, 100.0, RenderQuality.Interactive, RenderDecision.Bbox)]
    [InlineData(10, 15.99, RenderQuality.Interactive, RenderDecision.Bbox)]
    [InlineData(10, 16.0, RenderQuality.Interactive, RenderDecision.Full)]
    public void DecideHatch_Boundaries(int points, double area, RenderQuality q, RenderDecision expected) =>
        Assert.Equal(expected, RenderItemDrawer.DecideHatch(points, area, q));

    [Theory]
    [InlineData(0.49, RenderQuality.Refined, RenderDecision.Simplified)]
    [InlineData(0.5, RenderQuality.Refined, RenderDecision.Full)]
    [InlineData(0.99, RenderQuality.Interactive, RenderDecision.Simplified)]
    [InlineData(1.0, RenderQuality.Interactive, RenderDecision.Full)]
    public void DecideTiny_Boundaries(double extent, RenderQuality q, RenderDecision expected) =>
        Assert.Equal(expected, RenderItemDrawer.DecideTiny(extent, q));

    // ---- polyline simplification ----

    [Fact]
    public void Simplify_MergesCloserThanStep_KeepsEndpoints()
    {
        // scale=10、step=0.5px → 世界阈值 0.05：0/0.01/0.02 合并，0.1 与 0.11 合并，0.2 保留端点。
        var pts = new (double X, double Y)[] { (0, 0), (0.01, 0), (0.02, 0), (0.1, 0), (0.11, 0), (0.2, 0) };
        IReadOnlyList<int> kept = RenderItemDrawer.Simplify(pts, 0.5, 10, out bool closed);
        Assert.Equal(new[] { 0, 3, 5 }, kept);
        Assert.False(closed);
    }

    [Fact]
    public void Simplify_AllPointsWithinStep_KeepsOnlyEndpoints()
    {
        var pts = new (double X, double Y)[] { (0, 0), (0.001, 0), (0.002, 0), (0.003, 0) };
        IReadOnlyList<int> kept = RenderItemDrawer.Simplify(pts, 1.0, 1, out bool closed);
        Assert.Equal(new[] { 0, 3 }, kept);
        Assert.False(closed);
    }

    [Fact]
    public void Simplify_RepeatedClosingVertex_ReportsClosedAndIsPreserved()
    {
        // 既有闭合多段线重复首点惯例 → closed=true，首末索引都保留。
        var pts = new (double X, double Y)[] { (0, 0), (1, 0), (1, 1), (0, 1), (0, 0) };
        IReadOnlyList<int> kept = RenderItemDrawer.Simplify(pts, 0.5, 1, out bool closed);
        Assert.True(closed);
        Assert.Equal(0, kept[0]);
        Assert.Equal(pts.Length - 1, kept[^1]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, kept);
    }

    [Fact]
    public void Simplify_NonPositiveStep_KeepsAll()
    {
        var pts = new (double X, double Y)[] { (0, 0), (0.001, 0), (0.002, 0) };
        IReadOnlyList<int> kept = RenderItemDrawer.Simplify(pts, 0, 1, out bool closed);
        Assert.Equal(new[] { 0, 1, 2 }, kept);
        Assert.False(closed);
    }

    // ---- pen cache ----

    [AvaloniaFact]
    public void PenResolver_ColorPen_ReusesInstancePerColor()
    {
        var pens = new PenResolver();
        Pen a = pens.ColorPen(0x123456);
        Pen b = pens.ColorPen(0x123456);
        Pen c = pens.ColorPen(0x654321);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(2, pens.ColorPenCount); // 两种颜色 → 2 项
    }

    [AvaloniaFact]
    public void PenResolver_DashPen_KeyedByColorThicknessAndDashSignature()
    {
        var pens = new PenResolver();
        Pen solid = pens.ColorPen(0xFFFFFF);
        var dashed = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0", 0xFFFFFF,
            null, 0, 0, "1", DashPattern: new double[] { 10, 10 });
        Pen a = pens.ForItem(dashed, solid, 5);
        Pen b = pens.ForItem(dashed, solid, 5);
        Assert.Same(a, b);
        Pen c = pens.ForItem(dashed, solid, 10); // 缩放变化 → 设备段长不同 → 不同键
        Assert.NotSame(a, c);
        Assert.Equal(2, pens.DashPenCount);
    }

    [AvaloniaFact]
    public void PenResolver_DashPenCache_CapsAt512ThenClears()
    {
        var pens = new PenResolver();
        Pen solid = pens.ColorPen(0xFFFFFF);
        // 512 个不同 dash 签名（段长 1..512，设备取 1px 起、均 ≥0.25px）
        RenderItem Dash(int len) => new(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0",
            0xFFFFFF, null, 0, 0, "1", DashPattern: new double[] { len, 20 });
        for (int i = 1; i <= PenResolver.DashCacheCapacity; i++) pens.ForItem(Dash(i), solid, 1);
        Assert.Equal(PenResolver.DashCacheCapacity, pens.DashPenCount);

        Pen overflowPen = pens.ForItem(Dash(513), solid, 1); // 超限 → 清空后重插
        Assert.Equal(1, pens.DashPenCount);
        Assert.NotSame(overflowPen, pens.ForItem(Dash(1), solid, 1)); // 旧条目已全部失效
    }

    [AvaloniaFact]
    public void PenResolver_SubpixelDash_DegradesToSolid()
    {
        var pens = new PenResolver();
        Pen solid = pens.ColorPen(0xFFFFFF);
        var tiny = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0", 0xFFFFFF,
            null, 0, 0, "1", DashPattern: new double[] { 0.01, 10 });
        Assert.Same(solid, pens.ForItem(tiny, solid, 1)); // 任一段 <0.25px → 实线
        Assert.Equal(0, pens.DashPenCount);
    }

    // ---- text cache (LRU) ----

    [AvaloniaFact]
    public void TextCache_LruEvictsLeastRecentlyUsed()
    {
        var cache = new TextCache();
        int created = 0;
        FormattedText Make(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, Brushes.White);

        for (int i = 0; i < TextCache.Capacity; i++)
        {
            int index = i;
            cache.GetOrAdd($"T{index}", 12, false, 0xFFFFFF, () => { created++; return Make($"T{index}"); });
        }
        Assert.Equal(TextCache.Capacity, cache.Count);
        Assert.Equal(TextCache.Capacity, cache.Misses);
        Assert.Equal(0, cache.Evictions);

        // 命中 → 不重建，且访问提升为最近使用
        FormattedText hit = cache.GetOrAdd("T1", 12, false, 0xFFFFFF, () => throw new InvalidOperationException("must hit"));
        Assert.NotNull(hit);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(TextCache.Capacity, created);

        // 新键 → 淘汰最久未用的 T0（T1 因刚访问而保留）
        cache.GetOrAdd("NEW", 12, false, 0xFFFFFF, () => Make("NEW"));
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(TextCache.Capacity, cache.Count);
        cache.GetOrAdd("T1", 12, false, 0xFFFFFF, () => throw new InvalidOperationException("T1 must survive"));
        cache.GetOrAdd("T0", 12, false, 0xFFFFFF, () => Make("T0")); // 已被淘汰 → miss
        Assert.Equal(TextCache.Capacity + 2, cache.Misses);
    }

    [AvaloniaFact]
    public void TextCache_FontBucketAndColorParticipateInKey()
    {
        var cache = new TextCache();
        FormattedText Make() => new("A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, Brushes.White);
        cache.GetOrAdd("A", 12.0, false, 0xFFFFFF, Make);
        cache.GetOrAdd("A", 12.2, false, 0xFFFFFF, Make); // 12.0 与 12.2 同桶（round(px*2)/2 = 12.0）
        Assert.Equal(1, cache.Misses);
        cache.GetOrAdd("A", 12.4, false, 0xFFFFFF, Make); // 12.5 → 新桶
        cache.GetOrAdd("A", 12.0, false, 0xFF0000, Make); // 颜色不同 → 新键
        cache.GetOrAdd("A", 12.0, true, 0xFFFFFF, Make);  // bold 不同 → 新键
        Assert.Equal(4, cache.Misses);
    }

    // ---- pixel behavior ----

    [AvaloniaFact]
    public void Text_SimplifiedBar_DrawnAtBaseline_NoGlyphs()
    {
        // scale=20、TextHeight=0.15 → 3px：Refined 2–6px → 简化条（基线处 1px 横线）。
        var item = Text("AAAA", 1, 1, 0.15);
        var drawer = new RenderItemDrawer();
        PixelBuffer frame = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(), new[] { item }, null, RenderQuality.Refined,
                new PenResolver(), new TextCache(), null, null));

        (double bx, double by) = Projection().WorldToScreen(1.3, 1); // 条中点，基线 y
        Assert.True(frame.MaxBrightness((int)bx, (int)by) > 100, "简化条须在基线落像素");
        Assert.True(frame.MaxBrightness((int)bx, (int)by - 8) < 90, "简化条不画字形（基线上方为空）");
    }

    [AvaloniaFact]
    public void Text_BelowSkipThreshold_DrawsNothing_AndCountsSkip()
    {
        // Refined <2px → Skip（1.5px）。
        var item = Text("MMMM", 1, 1, 0.075);
        var drawer = new RenderItemDrawer();
        PixelBuffer frame = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(), new[] { item }, null, RenderQuality.Refined,
                new PenResolver(), new TextCache(), null, null));
        (double bx, double by) = Projection().WorldToScreen(2, 1.1);
        Assert.True(frame.MaxBrightness((int)bx, (int)by) < 90, "跳过阈值以下不落任何像素");
        Assert.Equal(1, drawer.LastTextSkipped);
        Assert.Equal(RenderDecision.Skip, RenderItemDrawer.DecideText(0.075 * 20, RenderQuality.Refined));
    }

    [AvaloniaFact]
    public void Hatch_InteractiveManyPoints_DrawsBboxOnly_InteriorEmpty()
    {
        // 600 点 + Interactive（>500）→ 仅 bbox 矩形（不填充）；内部必须为空。
        RenderItem hatch = HatchMany(0xFFFFFF, true, 600,
            new[] { (1.0, 1.0), (19.0, 1.0), (19.0, 19.0), (1.0, 19.0) });
        var drawer = new RenderItemDrawer();
        PixelBuffer frame = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(10), new[] { hatch }, null, RenderQuality.Interactive,
                new PenResolver(), null, null, null));

        (double ex, double ey) = Projection(10).WorldToScreen(1, 10); // bbox 左边中点
        (double ix, double iy) = Projection(10).WorldToScreen(10, 10); // 内部
        Assert.True(frame.MaxBrightness((int)ex, (int)ey) > 100, "bbox 边须有描边");
        Assert.True(frame.MaxBrightness((int)ix, (int)iy) < 90, "Bbox 降级不填充内部");
        Assert.Equal(1, drawer.LastHatchDowngraded);
    }

    [AvaloniaFact]
    public void Selection_BudgetExceeded_DrawsBboxWithSelectionPen_InteriorEmpty()
    {
        // 空心方块 + selectionBudgetExceeded → 基元只描边（黑，与背景不可区分），
        // 叠加层降级为黄色 2px bbox：边亮且偏黄（两通道高、一通道低），内部仍背景。
        RenderItem hatch = Hatch(0x000000, false, (1.0, 1.0), (19.0, 1.0), (19.0, 19.0), (1.0, 19.0));
        var drawer = new RenderItemDrawer();
        PixelBuffer frame = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(10), new[] { hatch }, null, RenderQuality.Refined,
                new PenResolver(), null, hatch, null, selectionBudgetExceeded: true));

        (double ex, double ey) = Projection(10).WorldToScreen(10, 1); // bbox 下边中点
        (double ix, double iy) = Projection(10).WorldToScreen(10, 10);
        Assert.True(frame.MaxBrightness((int)ex, (int)ey) > 150, "降级选中须画 bbox 描边");
        Assert.True(frame.MinBrightness((int)ex, (int)ey) < 90, "描边须为选中黄（非白）");
        Assert.True(frame.MaxBrightness((int)ix, (int)iy) < 90, "降级选中不画内部几何");
        Assert.True(drawer.LastSelectionDowngraded);
    }

    [AvaloniaFact]
    public void Highlight_DenseHatch_OverPerItemBudget_DrawsBbox()
    {
        // 高亮多选：单图元 >2000 点 → 该图元降级 bbox（无需调用方给 budget 标志）。
        RenderItem hatch = HatchMany(0x000000, true, 2001,
            new[] { (1.0, 1.0), (19.0, 1.0), (19.0, 19.0), (1.0, 19.0) });
        var drawer = new RenderItemDrawer();
        var highlighted = new HashSet<string>(StringComparer.Ordinal) { "H" };
        PixelBuffer frame = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(10), new[] { hatch }, null, RenderQuality.Refined,
                new PenResolver(), null, null, highlighted));

        (double ex, double ey) = Projection(10).WorldToScreen(1, 10);
        (double ix, double iy) = Projection(10).WorldToScreen(10, 10);
        Assert.True(frame.MaxBrightness((int)ex, (int)ey) > 150, "高亮降级 bbox 边须落像素");
        Assert.True(frame.MaxBrightness((int)ix, (int)iy) < 90, "高亮降级不画填充");
        Assert.True(drawer.LastSelectionDowngraded);
    }

    [AvaloniaFact]
    public void Cull_WithIndices_DrawsOnlyListedItems()
    {
        RenderItem a = Line(1, 10, 3, 10, 0xFFFFFF, "A");   // 屏幕左区
        RenderItem b = Line(15, 10, 17, 10, 0xFFFFFF, "B"); // 屏幕右区
        var items = new[] { a, b };
        var drawer = new RenderItemDrawer();
        var pens = new PenResolver();

        PixelBuffer onlyA = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(10), items, new[] { 0 }, RenderQuality.Refined, pens, null, null, null));
        PixelBuffer onlyB = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(10), items, new[] { 1 }, RenderQuality.Refined, pens, null, null, null));

        (double ax, double ay) = Projection(10).WorldToScreen(2, 10);
        (double bx, double by) = Projection(10).WorldToScreen(16, 10);
        Assert.True(onlyA.MaxBrightness((int)ax, (int)ay) > 100, "索引 0 → A 可见");
        Assert.True(onlyA.MaxBrightness((int)bx, (int)by) < 90, "索引 0 → B 不画");
        Assert.True(onlyB.MaxBrightness((int)bx, (int)by) > 100, "索引 1 → B 可见");
        Assert.True(onlyB.MaxBrightness((int)ax, (int)ay) < 90, "索引 1 → A 不画");
    }

    [AvaloniaFact]
    public void NonFiniteItem_SkippedWithoutThrowing()
    {
        var bad = new RenderItem(RenderKind.Polyline,
            new[] { (double.NaN, double.NaN), (double.PositiveInfinity, 1) }, "0", 0xFFFFFF, null, 0, 0, "X");
        var drawer = new RenderItemDrawer();
        PixelBuffer frame = RenderPixels(ctx =>
            drawer.Draw(ctx, Projection(), new[] { bad }, null, RenderQuality.Refined,
                new PenResolver(), null, null, null));
        Assert.True(frame.MaxBrightness(100, 100) < 90);
    }

    [AvaloniaFact]
    public void DrawerDirect_MatchesDrawingCanvas_OnMixedFixture()
    {
        // 接线等价：同一 VM 投影下，Drawer 直绘与 DrawingCanvas.Render（内部即 Drawer）
        // 逐像素通道差 ≤2/255（背景/线/圆/弧/折线/文字/hatch 混合夹具）。
        var items = new[]
        {
            Line(0, 0, 10, 20),
            new RenderItem(RenderKind.Circle, new[] { (5.0, 5.0), (3.0, 0.0) }, "0", 0xFF0000, null, 0, 0, "2"),
            new RenderItem(RenderKind.Arc, new[] { (1.0, 2.0), (4.0, 30.0), (120.0, 0.0) }, "0", 0x00FF00, null, 0, 0, "3"),
            new RenderItem(RenderKind.Polyline, new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 3.0) }, "0", 0x00FFFF, null, 0, 0, "4"),
            Text("HELLO", 7, 8, 2.5),
            Hatch(0x808080, true, (2.0, 12.0), (5.0, 12.0), (5.0, 15.0), (2.0, 15.0)),
        };
        var vm = new CanvasViewModel(items);
        var canvas = new DrawingCanvas(vm);
        var window = new Window { Width = 200, Height = 200, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        WriteableBitmap? canvasFrame = window.CaptureRenderedFrame();
        Assert.NotNull(canvasFrame);
        PixelBuffer canvasPixels = PixelBuffer.From(canvasFrame!);
        CanvasProjection projection = CanvasProjection.From(vm);
        var drawer = new RenderItemDrawer();

        PixelBuffer direct = RenderPixels(ctx =>
            drawer.Draw(ctx, projection, items, null, RenderQuality.Refined,
                new PenResolver(), new TextCache(), null, null));
        window.Close();

        Assert.Equal(canvasPixels.Width, direct.Width);
        Assert.Equal(canvasPixels.Height, direct.Height);
        int maxDiff = 0;
        for (int i = 0; i < canvasPixels.Data.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(canvasPixels.Data[i] - direct.Data[i]));
        Assert.True(maxDiff <= 2, $"Drawer 直绘与 DrawingCanvas 帧差 {maxDiff}/255 超出容差 2");
    }

    // ---- helpers ----

    private sealed class DrawerHost : Control
    {
        public Action<DrawingContext>? Paint;
        public override void Render(DrawingContext context) => Paint?.Invoke(context);
    }

    private static PixelBuffer RenderPixels(Action<DrawingContext> paint, int w = 200, int h = 200)
    {
        var host = new DrawerHost
        {
            Paint = ctx =>
            {
                ctx.FillRectangle(new SolidColorBrush(Background), new Rect(0, 0, w, h));
                paint(ctx);
            },
        };
        var window = new Window { Width = w, Height = h, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame(); // 预热：确保至少一帧合成
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        PixelBuffer buffer = PixelBuffer.From(frame!);
        window.Close();
        return buffer;
    }

    /// <summary>帧像素快照（Bgra/Rgba 通道序无关：断言用 min/max 三通道）。</summary>
    private sealed class PixelBuffer
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Data { get; init; }

        public static PixelBuffer From(WriteableBitmap frame)
        {
            using ILockedFramebuffer fb = frame.Lock();
            int width = fb.Size.Width, height = fb.Size.Height;
            var data = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int src = y * fb.RowBytes + x * 4;
                    int dst = (y * width + x) * 4;
                    for (int c = 0; c < 4; c++) data[dst + c] = Marshal.ReadByte(fb.Address, src + c);
                }
            return new PixelBuffer { Width = width, Height = height, Data = data };
        }

        private int Channel(int x, int y, int c) =>
            x < 0 || y < 0 || x >= Width || y >= Height ? 0 : Data[(y * Width + x) * 4 + c];

        /// <summary>5×5 邻域三通道最大亮度（抗锯齿描边可能不覆盖数学点）。</summary>
        public int MaxBrightness(int x, int y)
        {
            int max = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    for (int c = 0; c < 3; c++)
                        max = Math.Max(max, Channel(x + dx, y + dy, c));
            return max;
        }

        /// <summary>5×5 邻域三通道最小值（区分黄/白：黄有一通道≈0）。</summary>
        public int MinBrightness(int x, int y)
        {
            int min = 255;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px < 0 || py < 0 || px >= Width || py >= Height) continue;
                    for (int c = 0; c < 3; c++) min = Math.Min(min, Channel(px, py, c));
                }
            return min;
        }
    }
}
