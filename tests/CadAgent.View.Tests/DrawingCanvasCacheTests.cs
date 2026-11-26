using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 3 headless coverage: 位图平移缓存 + 两阶段精绘。
/// 真实 Skia 帧（UseHeadlessDrawing=false）像素对比；计数 seam 判定缓存是否复用；
/// 交互计时器不依赖真实 150ms（ForceRefinedForTests 同步走同一路径）。
/// </summary>
public class DrawingCanvasCacheTests
{
    // ---- 夹具 ----

    private static RenderItem Line(double x0, double y0, double x1, double y1, string layer = "0", string handle = "L1") =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem HatchedSquare(double x0, double y0, double size, string layer, string handle) =>
        new(RenderKind.Hatch,
            new[] { (x0, y0), (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size) },
            layer, 0x00FFFF, null, 0, 0, handle, Fill: true);

    /// <summary>线 + 圆 + 文本 + 实心 Hatch（同一图层，全部落在视口内）。</summary>
    private static IReadOnlyList<RenderItem> NormalItems() => new[]
    {
        Line(1, 1, 5, 5),
        new RenderItem(RenderKind.Circle, new[] { (4.0, 4.0), (1.0, 0.0) }, "0", 0x00FF00, null, 0, 0, "C1"),
        new RenderItem(RenderKind.Text, new[] { (1.0, 7.0) }, "0", 0xFFFFFF, "ABC", 0.8, 0, "T1"),
        HatchedSquare(6, 1, 3, "0", "H1"),
    };

    private static (Window Window, DrawingCanvas Canvas, CanvasViewModel Vm) Show(
        IReadOnlyList<RenderItem> items, double width = 240, double height = 200)
    {
        var vm = new CanvasViewModel(items);
        var canvas = new DrawingCanvas(vm);
        var window = new Window { Width = width, Height = height, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, canvas, vm);
    }

    private static WriteableBitmap Capture(Window window)
    {
        window.CaptureRenderedFrame(); // 先确保至少合成一帧
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame!;
    }

    // ---- 像素工具（headless 帧为 Rgba8888）----

    private static int Brightness(WriteableBitmap frame, int x, int y, int radius = 2)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int max = 0;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                for (int c = 0; c < 3; c++)
                    max = Math.Max(max, Read(frame, fb, x + dx, y + dy, c));
        return max;
    }

    /// <summary>黄色得分 = max(5×5 内 min(R,G) - B)：纯黄 ≈255，白/灰/深色 ≈0。</summary>
    private static int YellowScore(WriteableBitmap frame, int x, int y, int radius = 2)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int best = int.MinValue;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!InBounds(fb, x + dx, y + dy)) continue;
                int r = Read(frame, fb, x + dx, y + dy, 0);
                int g = Read(frame, fb, x + dx, y + dy, 1);
                int b = Read(frame, fb, x + dx, y + dy, 2);
                best = Math.Max(best, Math.Min(r, g) - b);
            }
        return best;
    }

    private static int Read(WriteableBitmap frame, ILockedFramebuffer fb, int x, int y, int channel)
    {
        if (!InBounds(fb, x, y)) return 0;
        return Marshal.ReadByte(fb.Address, y * fb.RowBytes + x * 4 + channel);
    }

    private static bool InBounds(ILockedFramebuffer fb, int x, int y) =>
        x >= 0 && y >= 0 && x < fb.Size.Width && y < fb.Size.Height;

    /// <summary>两帧逐像素最大通道差（0..255）。</summary>
    private static int MaxChannelDelta(WriteableBitmap a, WriteableBitmap b)
    {
        Assert.Equal(a.PixelSize, b.PixelSize);
        using ILockedFramebuffer fa = a.Lock();
        using ILockedFramebuffer fb = b.Lock();
        int max = 0;
        for (int y = 0; y < fa.Size.Height; y++)
            for (int x = 0; x < fa.Size.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int offset = y * fa.RowBytes + x * 4 + c;
                    int delta = Math.Abs(Marshal.ReadByte(fa.Address, offset) - Marshal.ReadByte(fb.Address, offset));
                    if (delta > max) max = delta;
                }
        return max;
    }

    /// <summary>通道差 &gt; threshold 的像素数。</summary>
    private static int DifferingPixels(WriteableBitmap a, WriteableBitmap b, int threshold)
    {
        using ILockedFramebuffer fa = a.Lock();
        using ILockedFramebuffer fb = b.Lock();
        int count = 0;
        for (int y = 0; y < fa.Size.Height; y++)
            for (int x = 0; x < fa.Size.Width; x++)
            {
                int offset = y * fa.RowBytes + x * 4;
                bool differs = false;
                for (int c = 0; c < 3 && !differs; c++)
                    differs = Math.Abs(Marshal.ReadByte(fa.Address, offset + c)
                        - Marshal.ReadByte(fb.Address, offset + c)) > threshold;
                if (differs) count++;
            }
        return count;
    }

    // ---- 1. 缓存 blit 与直绘等价 ----

    [AvaloniaFact]
    public void Cache_SameState_BlitMatchesDirectRender()
    {
        (Window window, DrawingCanvas canvas, _) = Show(NormalItems());
        try
        {
            canvas.DisableCacheForTests = true; // 直绘对照
            canvas.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap direct = Capture(window);

            canvas.DisableCacheForTests = false; // 缓存重建 + blit
            canvas.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap cached = Capture(window);

            Assert.True(MaxChannelDelta(direct, cached) <= 2,
                $"缓存 blit 与直绘须在 2/255 通道差内（实测 {MaxChannelDelta(direct, cached)}）");
        }
        finally { window.Close(); }
    }

    // ---- 2. 平移复用缓存 ----

    [AvaloniaFact]
    public void Cache_Pan32Px_ReusesBitmapAndShiftsContent()
    {
        var line = Line(0, 0, 10, 0);
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { line }, 200, 200);
        try
        {
            WriteableBitmap before = Capture(window);
            int baseline = canvas.BaseRenderCount;
            Assert.True(baseline > 0, "首帧须已构建缓存");

            (double bx, double by) = vm.WorldToScreen(5, 0);
            Assert.True(Brightness(before, (int)Math.Round(bx), (int)Math.Round(by)) > 100, "初始线条可见");

            vm.PanBy(0, 32); // 内容下移 32px
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap after = Capture(window);

            Assert.Equal(baseline, canvas.BaseRenderCount); // 未重建基元层
            Assert.True(Brightness(after, (int)Math.Round(bx), (int)Math.Round(by)) < 90, "原位应回到背景");
            Assert.True(Brightness(after, (int)Math.Round(bx), (int)Math.Round(by) + 32) > 100, "线条应下移 32px");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Cache_Pan200Px_RebuildsBitmap()
    {
        var line = Line(0, 0, 10, 0);
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { line }, 200, 200);
        try
        {
            Capture(window);
            int baseline = canvas.BaseRenderCount;

            (double bx, double by) = vm.WorldToScreen(5, 0);
            vm.PanBy(200, 0); // 超出 128px 外扩 → 重建（内容整幅右移出屏）
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap after = Capture(window);

            Assert.True(canvas.BaseRenderCount > baseline, "超出外扩边距须重建缓存");
            Assert.True(Brightness(after, (int)Math.Round(bx), (int)Math.Round(by)) < 90, "整幅移出后原位为背景");

            vm.PanBy(-200, 0); // 再越界一次 → 再次重建；内容回到原位
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap restored = Capture(window);
            Assert.True(canvas.BaseRenderCount >= baseline + 2, "每次越界平移都须重建");
            Assert.True(Brightness(restored, (int)Math.Round(bx), (int)Math.Round(by)) > 100,
                "重建后线条回到原位（缓存锚点随重建刷新）");
        }
        finally { window.Close(); }
    }

    // ---- 3. 两阶段：交互缩放旧图 → 静止精绘 ----

    [AvaloniaFact]
    public void Cache_ZoomDuringInteraction_ScalesOldBitmap_ThenForceRefinedRebuilds()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(NormalItems());
        try
        {
            WriteableBitmap initial = Capture(window);
            int baseline = canvas.BaseRenderCount;

            window.MouseWheel(new Point(120, 100), new Vector(0, 1)); // 交互开始 + 1.2× 缩放
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap scaled = Capture(window);

            Assert.Equal(RenderQuality.Interactive, canvas.Quality);
            Assert.Equal("概览", canvas.LastRenderNote);
            Assert.Equal(baseline, canvas.BaseRenderCount); // 交互期只缩放旧位图

            vm.ZoomAt(3, 120, 100); // 继续放大 → 旧位图更明显的插值差
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap scaledMore = Capture(window);
            Assert.Equal(baseline, canvas.BaseRenderCount);

            canvas.ForceRefinedForTests(); // 同步精绘（不等 150ms 计时器）
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap refined = Capture(window);

            Assert.Equal(RenderQuality.Refined, canvas.Quality);
            Assert.Equal(baseline + 1, canvas.BaseRenderCount);
            Assert.Null(canvas.LastRenderNote);
            Assert.True(DifferingPixels(scaledMore, refined, 8) > 0, "精绘帧须与旧图缩放帧不同");
            Assert.True(DifferingPixels(initial, refined, 8) > 0, "缩放后精绘帧须与初始帧不同");
        }
        finally { window.Close(); }
    }

    // ---- 4. 逐项失效 ----

    [AvaloniaFact]
    public void Cache_ItemsReferenceChange_Rebuilds()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(NormalItems());
        try
        {
            Capture(window);
            int baseline = canvas.BaseRenderCount;

            vm.Refresh(NormalItems()); // 新引用（编辑提交口径）
            Dispatcher.UIThread.RunJobs();
            Capture(window);
            Assert.True(canvas.BaseRenderCount > baseline, "Items 引用变化须重建");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Cache_HiddenLayersChange_Rebuilds()
    {
        var items = new[] { HatchedSquare(0, 0, 3, "L1", "A"), HatchedSquare(5, 0, 3, "L2", "B") };
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(items);
        try
        {
            Capture(window);
            int baseline = canvas.BaseRenderCount;

            vm.HiddenLayers.Add("L2");
            vm.Invalidate();
            Dispatcher.UIThread.RunJobs();
            Capture(window);
            Assert.True(canvas.BaseRenderCount > baseline, "隐藏层集合变化须重建");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Cache_DiffOverlaySet_BypassesCache()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(NormalItems());
        try
        {
            Capture(window);
            int baseline = canvas.BaseRenderCount;

            vm.SetDiffOverlay(new DiffOverlay(NormalItems(), new[] { "L1" }, Array.Empty<string>()));
            Dispatcher.UIThread.RunJobs();
            Capture(window);
            Assert.True(canvas.BaseRenderCount > baseline, "diff 模式直绘（不进缓存）须计一次基元层");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Cache_SizeChange_Rebuilds()
    {
        (Window window, DrawingCanvas canvas, _) = Show(NormalItems());
        try
        {
            Capture(window);
            int baseline = canvas.BaseRenderCount;

            window.Width = 320;
            window.Height = 260;
            Dispatcher.UIThread.RunJobs();
            Capture(window);
            Assert.True(canvas.BaseRenderCount > baseline, "尺寸变化须重建");
        }
        finally { window.Close(); }
    }

    // ---- 5. 密集 Hatch 选中：降级为包围盒 ----

    [AvaloniaFact]
    public void Selection_DenseHatch_NoteBboxOnly_BboxEdgeHighlighted()
    {
        const int points = 2500; // > 2000 → 高亮降级为 bbox（§3.2 预算）
        var diagonal = new (double X, double Y)[points];
        for (int i = 0; i < points; i++)
        {
            double t = 10.0 * i / (points - 1);
            diagonal[i] = (t, t); // 细对角线：bbox (0,0)-(10,10) 的边远离几何
        }
        var hatch = new RenderItem(RenderKind.Hatch, diagonal, "0", 0xFFFFFF, null, 0, 0, "DENSE");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { hatch }, 200, 200);
        try
        {
            Capture(window);
            vm.Select(0);
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap frame = Capture(window);

            Assert.Contains("包围盒", canvas.LastRenderNote);

            (double ex, double ey) = vm.WorldToScreen(5, 10);  // bbox 上边中点
            (double ix, double iy) = vm.WorldToScreen(2, 8);   // bbox 内部（远离对角线）
            Assert.True(YellowScore(frame, (int)Math.Round(ex), (int)Math.Round(ey)) > 150,
                "bbox 边须有黄色描边");
            Assert.True(YellowScore(frame, (int)Math.Round(ix), (int)Math.Round(iy)) < 100,
                "内部不得填充选中色（只画包围盒）");
        }
        finally { window.Close(); }
    }

    // ---- 6. 叠加层不烘焙进缓存 ----

    [AvaloniaFact]
    public void Cache_DoesNotBakeSelectionOverlay()
    {
        var line = Line(0, 0, 10, 0);
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { line }, 200, 200);
        try
        {
            Capture(window);
            int baseline = canvas.BaseRenderCount;

            vm.Select(0);
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap selected = Capture(window);
            (double sx, double sy) = vm.WorldToScreen(5, 0);
            Assert.True(YellowScore(selected, (int)Math.Round(sx), (int)Math.Round(sy)) > 150,
                "选中须实时叠加黄色高亮");

            vm.Select(null);
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap deselected = Capture(window);
            Assert.True(YellowScore(deselected, (int)Math.Round(sx), (int)Math.Round(sy)) < 100,
                "取消选中后高亮须消失（未被烘焙进缓存）");
            Assert.Equal(baseline, canvas.BaseRenderCount); // 两次叠加都复用同一基元位图
        }
        finally { window.Close(); }
    }

    // ---- 7. 计时器 seam 与 detach 安全 ----

    [AvaloniaFact]
    public void ForceRefinedForTests_SynchronousRefine_AndTimerDisposedOnClose()
    {
        (Window window, DrawingCanvas canvas, _) = Show(NormalItems());
        try
        {
            Capture(window);
            window.MouseWheel(new Point(120, 100), new Vector(0, 1)); // 启动 150ms 静止计时器
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(RenderQuality.Interactive, canvas.Quality);

            canvas.ForceRefinedForTests(); // 同步生效（不等计时器）
            Assert.Equal(RenderQuality.Refined, canvas.Quality);
            Dispatcher.UIThread.RunJobs();
            Capture(window);
            Assert.Null(canvas.LastRenderNote);
        }
        finally { window.Close(); }

        canvas.ForceRefinedForTests(); // detach 后计时器已停/位图已释放：不得抛异常
        Assert.Equal(RenderQuality.Refined, canvas.Quality);
    }
}
