using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 3 headless coverage ([AvaloniaFact], real Skia frames):
/// control instantiation + Render of every RenderKind without exception,
/// pixel-probe pinning the Y flip and arc sweep direction, and pointer wiring
/// (wheel zoom anchor / middle-drag pan / left-click select) through the
/// headless input extensions. VM math itself is pinned by CanvasViewModelTests.
/// </summary>
public class CanvasHeadlessTests
{
    private static RenderItem Line(double x0, double y0, double x1, double y1, string handle = "1") =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, "0", 0xFFFFFF, null, 0, 0, handle);

    private static IReadOnlyList<RenderItem> MixedItems() => new[]
    {
        Line(0, 0, 10, 20),
        new RenderItem(RenderKind.Circle, new[] { (5.0, 5.0), (3.0, 0.0) }, "0", 0xFF0000, null, 0, 0, "2"),
        new RenderItem(RenderKind.Arc, new[] { (1.0, 2.0), (4.0, 30.0), (120.0, 0.0) }, "0", 0x00FF00, null, 0, 0, "3"),
        new RenderItem(RenderKind.Polyline, new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 3.0) }, "0", 0x00FFFF, null, 0, 0, "4"),
        new RenderItem(RenderKind.Text, new[] { (7.0, 8.0) }, "0", 0xFFFFFF, "HELLO", 2.5, 0, "5"),
    };

    private static (Window Window, DrawingCanvas Canvas, CanvasViewModel Vm) Show(
        IReadOnlyList<RenderItem> items, double width = 800, double height = 600)
    {
        var vm = new CanvasViewModel(items);
        var canvas = new DrawingCanvas(vm);
        var window = new Window { Width = width, Height = height, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, canvas, vm);
    }

    [AvaloniaFact]
    public void Canvas_MixedItems_LayoutFitAndRenderFrame()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(MixedItems());
        try
        {
            Assert.True(canvas.Bounds.Width > 0 && canvas.Bounds.Height > 0);
            Assert.Equal(1, vm.Zoom); // SizeChanged -> Fit ran
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame); // Render of all 5 kinds produced a real frame
            Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_Arc0To90_PixelProbePinsYFlipAndSweep()
    {
        // Arc center (0,0) r=1, 0..90 deg CCW; bounds (-1,-1,1,1) in a 200x200
        // viewport -> scale 90, center screen (100,100). The 45 deg arc point is
        // world (+0.707,+0.707) -> screen UPPER right; the mirrored lower-left
        // point must stay background (Y flip + CounterClockwise sweep pinned).
        var arc = new RenderItem(RenderKind.Arc, new[] { (0.0, 0.0), (1.0, 0.0), (90.0, 0.0) }, "0", 0xFFFFFF, null, 0, 0, "A1");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { arc }, 200, 200);
        try
        {
            window.CaptureRenderedFrame(); // ensure at least one composed frame
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            double d = Math.Sqrt(0.5);
            (double ax, double ay) = vm.WorldToScreen(d, d);   // on the arc
            (double bx, double by) = vm.WorldToScreen(-d, -d); // opposite quadrant
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ax), (int)Math.Round(ay)) > 200,
                "expected bright arc pixel in upper-right (world +X+Y)");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(bx), (int)Math.Round(by)) < 90,
                "expected background in lower-left (arc must not sweep there)");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_Wheel_ZoomsWithCursorAnchor()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { Line(0, 0, 100, 50) });
        try
        {
            var cursor = new Point(400, 300);
            (double wx, double wy) = vm.ScreenToWorld(cursor.X, cursor.Y);
            window.MouseWheel(cursor, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.Zoom > 1);
            (double sx, double sy) = vm.WorldToScreen(wx, wy);
            Assert.Equal(cursor.X, sx, 3);
            Assert.Equal(cursor.Y, sy, 3);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_MiddleDrag_PansByScreenDelta()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { Line(0, 0, 100, 50) });
        try
        {
            (double x0, double y0) = vm.WorldToScreen(10, 10);
            window.MouseDown(new Point(100, 100), MouseButton.Middle);
            window.MouseMove(new Point(150, 130));
            window.MouseUp(new Point(150, 130), MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            (double x1, double y1) = vm.WorldToScreen(10, 10);
            Assert.Equal(x0 + 50, x1, 3);
            Assert.Equal(y0 + 30, y1, 3);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_LeftClick_SelectsThenClears()
    {
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { Line(0, 0, 10, 0) });
        try
        {
            (double sx, double sy) = vm.WorldToScreen(5, 0); // on the line
            var onLine = new Point(sx, sy);
            window.MouseDown(onLine, MouseButton.Left);
            window.MouseUp(onLine, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.SelectedIndex);
            Assert.Equal("1", vm.SelectedHandle); // real handle string carried by the item (T4)

            var far = new Point(10, 10); // empty area
            window.MouseDown(far, MouseButton.Left);
            window.MouseUp(far, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.SelectedIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_TextSelection_PixelProbeShowsYellowOnlyWhenSelected()
    {
        // Single RED text at the origin: degenerate bounds -> baseScale=1, so the
        // font size equals TextHeight (100px). Glyphs render above the insertion
        // screen point. Yellow highlight = high GREEN channel; red text on the
        // dark background keeps GREEN low. Probe 5x5 AA neighborhoods on a grid
        // across the glyph box (exact glyph metrics vary by font).
        var text = new RenderItem(RenderKind.Text, new[] { (0.0, 0.0) }, "0", 0xFF0000, "MMMM", 100, 0, "T1");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { text });
        try
        {
            (double px, double py) = vm.WorldToScreen(0, 0);
            window.CaptureRenderedFrame(); // ensure at least one composed frame
            WriteableBitmap? normal = window.CaptureRenderedFrame();
            Assert.NotNull(normal);
            Assert.True(MaxChannelInBox(normal!, px + 2, py - 100, px + 350, py - 2, channel: 0) > 150,
                "red text must actually render (RED channel present)");
            Assert.True(MaxChannelInBox(normal!, px + 2, py - 100, px + 350, py - 2, channel: 1) < 90,
                "unselected text must stay its normal red color (no yellow)");

            vm.Select(0);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            WriteableBitmap? selected = window.CaptureRenderedFrame();
            Assert.NotNull(selected);
            Assert.True(MaxChannelInBox(selected!, px + 2, py - 100, px + 350, py - 2, channel: 1) > 150,
                "selected text must highlight yellow (SelectionPen color)");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Hatch_Solid_RenderedFilled_DoesNotThrow()
    {
        // 实心填充方块 (0,0)-(10,10)：渲染一帧无异常 + 视觉树存活（镜像既有帧测试
        // 断言粒度）；像素探针镜像 NeighborhoodBrightness 模式：边界内实心填充亮、
        // 边界外保持深色背景。200x200 视口 Fit → scale 18，世界 (5,5)→屏幕 (100,100)。
        var hatch = new RenderItem(RenderKind.Hatch,
            new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0) },
            "0", 0xFFFFFF, null, 0, 0, "H1", Fill: true);
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { hatch }, 200, 200);
        try
        {
            Assert.True(canvas.Bounds.Width > 0 && canvas.Bounds.Height > 0);
            window.CaptureRenderedFrame(); // ensure at least one composed frame
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame); // Hatch 渲染产出真实帧（无异常）
            Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
            (double ix, double iy) = vm.WorldToScreen(5, 5);      // 方块内部 → 实心填充
            (double ox, double oy) = vm.WorldToScreen(-0.3, 5);   // 左边界外（仍在视口内）→ 背景
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ix), (int)Math.Round(iy)) > 200,
                "expected solid fill inside the hatch boundary");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ox), (int)Math.Round(oy)) < 90,
                "expected background outside the hatch boundary");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Hatch_TwoPaths_EvenOddHoleNotFilled()
    {
        // 外方 (0,0)-(10,10) + 岛 (3,3)-(7,7)，(NaN,NaN) 路径分隔 → EvenOdd 跨路径
        // 挖岛：岛中心须为背景（未填充），内外方形之间须填充。钉住 NaN 分隔约定的
        // 画布端语义（StreamGeometry FillRule.EvenOdd + 逐路径 BeginFigure/EndFigure）。
        var hatch = new RenderItem(RenderKind.Hatch,
            new[]
            {
                (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0),
                (double.NaN, double.NaN),
                (3.0, 3.0), (7.0, 3.0), (7.0, 7.0), (3.0, 7.0)
            },
            "0", 0xFFFFFF, null, 0, 0, "H2", Fill: true);
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { hatch }, 200, 200);
        try
        {
            window.CaptureRenderedFrame(); // ensure at least one composed frame
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            (double hx, double hy) = vm.WorldToScreen(5, 5);     // 岛中心 → EvenOdd 挖空
            (double fx, double fy) = vm.WorldToScreen(1.5, 5);   // 内外方形之间 → 填充
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(hx), (int)Math.Round(hy)) < 90,
                "expected EvenOdd hole (background) at the island center");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(fx), (int)Math.Round(fy)) > 200,
                "expected solid fill between outer boundary and island");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_DashedLine_PixelProbeShowsGaps()
    {
        // Task 3（线型虚实）：水平线 (0,0)-(100,0) + DashPattern [10,10]（drawing
        // 单位）。800x600 视口 Fit → scale 7.2，设备段长 72px：世界 x∈[0,10) 为划、
        // [10,20) 为空。探针钉住 pen DashStyle 克隆生效：划中点亮、空中点背景。
        var dashed = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (100.0, 0.0) },
            "0", 0xFFFFFF, null, 0, 0, "D1", DashPattern: new double[] { 10, 10 });
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { dashed });
        try
        {
            window.CaptureRenderedFrame(); // ensure at least one composed frame
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            (double dx, double dy) = vm.WorldToScreen(5, 0);   // 第一划中点 → 亮
            (double gx, double gy) = vm.WorldToScreen(15, 0);  // 第一空中点 → 背景
            // 1px 水平描边落在整数屏幕 y → AA 上下半覆盖（~142），亮阈值 >100
            // （背景 30；空中探针 <90 仍可判别）。
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(dx), (int)Math.Round(dy)) > 100,
                "expected bright dash segment at world x=5");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(gx), (int)Math.Round(gy)) < 90,
                "expected background gap at world x=15 (dash pattern must reach the pen)");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_SubpixelDashPattern_DegradesToSolid()
    {
        // 亚像素守卫：DashPattern [0.01,10] → 设备 [0.072,72]px，任一段 < 0.25px
        // →实线降级防摩尔纹。若守卫失效，0.072px 短划 + 72px 长空会使 x=5/x=15
        // 均落在空中（暗）；降级实线则两点皆亮——探针可判别。
        var tiny = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (100.0, 0.0) },
            "0", 0xFFFFFF, null, 0, 0, "D2", DashPattern: new double[] { 0.01, 10 });
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { tiny });
        try
        {
            window.CaptureRenderedFrame();
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            (double ax, double ay) = vm.WorldToScreen(5, 0);
            (double bx, double by) = vm.WorldToScreen(15, 0);
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ax), (int)Math.Round(ay)) > 100,
                "subpixel pattern must degrade to solid (bright at x=5)");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(bx), (int)Math.Round(by)) > 100,
                "subpixel pattern must degrade to solid (bright at x=15)");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_SolidLine_NoDashPattern_Continuous()
    {
        // 行为保持回归锚：无 DashPattern（null）→实线，全程亮（既有渲染不变）。
        var solid = Line(0, 0, 100, 0, "D3");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { solid });
        try
        {
            window.CaptureRenderedFrame();
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            (double ax, double ay) = vm.WorldToScreen(5, 0);
            (double bx, double by) = vm.WorldToScreen(15, 0);
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ax), (int)Math.Round(ay)) > 100,
                "solid line must stay continuous at x=5");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(bx), (int)Math.Round(by)) > 100,
                "solid line must stay continuous at x=15");
        }
        finally { window.Close(); }
    }

    // ---- 项3 T2 修复轮：画布级裁剪 E2E 与质量 seam ----

    private static RenderItem HatchSquare(double x0, double y0, double size, string layer, string handle) =>
        new(RenderKind.Hatch,
            new[] { (x0, y0), (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size) },
            layer, 0xFFFFFF, null, 0, 0, handle, Fill: true);

    [AvaloniaFact]
    public void Canvas_ZoomedViewport_DrawsPartiallyVisibleItem_AndCullsOffscreenOne()
    {
        // 缩放锚在 A 中心：视口（世界 x≈[0.78,2.72]）只覆盖 A(0..2) 一部分，
        // C(12..14) 完全出屏 → QueryIndices 走视口子集（非全量捷径）。
        // A 的屏内部分必须照常绘制（索引查询不得漏掉相交项）。
        RenderItem a = HatchSquare(0, 0, 2, "0", "A");
        RenderItem c = HatchSquare(12, 0, 2, "0", "C");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { a, c }, 200, 200);
        try
        {
            (double ax, double ay) = vm.WorldToScreen(1, 1); // A 中心（视口锚点保持不动）
            vm.ZoomAt(8, ax, ay);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            (double cx, _) = vm.WorldToScreen(12, 1);
            Assert.True(cx > canvas.Bounds.Width, "C 须完全出屏（右侧视口外）");
            (double ix, double iy) = vm.WorldToScreen(1.5, 1); // A 内部、仍在屏内
            Assert.True(ix > 0 && ix < canvas.Bounds.Width, "A 探针点须在屏内");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ix), (int)Math.Round(iy)) > 150,
                "部分可见项 A 的屏内填充必须绘制（裁剪不得漏项）");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_HiddenLayer_ItemNotDrawn_WhileVisibleItemDrawn()
    {
        // HiddenLayers 非空 → 全量捷径关闭，走 QueryViewport 的隐藏层过滤：
        // 隐藏层 B 屏内位置必须为背景，可见层 A 照常绘制。
        RenderItem a = HatchSquare(0, 0, 2, "Visible", "A");
        RenderItem b = HatchSquare(5, 0, 2, "Hidden", "B");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { a, b }, 200, 200);
        try
        {
            vm.HiddenLayers.Add("Hidden");
            vm.Invalidate();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            (double bx, double by) = vm.WorldToScreen(6, 1);
            (double ax, double ay) = vm.WorldToScreen(1, 1);
            Assert.True(bx > 0 && bx < canvas.Bounds.Width, "B 屏内位置须在视口内（证明是隐藏过滤不是裁剪）");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(bx), (int)Math.Round(by)) < 90,
                "隐藏层图元不得绘制");
            Assert.True(NeighborhoodBrightness(frame!, (int)Math.Round(ax), (int)Math.Round(ay)) > 150,
                "可见层图元须照常绘制");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_SetQualityForTests_Interactive_ReachesDraw()
    {
        // 退化包围盒（单文本插入点）→ baseScale=1、插入点屏幕 (100,100)。
        // 3px 文本：Refined 2–6px 画简化条（基线亮、字形带空）；
        // Interactive 4px 以下 → Skip（基线与字形带均无像素）。
        var text = new RenderItem(RenderKind.Text, new[] { (0.0, 0.0) }, "0", 0xFFFFFF, "MMMM", 3, 0, "T1");
        (Window window, DrawingCanvas canvas, CanvasViewModel vm) = Show(new[] { text }, 200, 200);
        try
        {
            (double bx, double by) = vm.WorldToScreen(3, 0); // 基线正上方一点（屏 103,100）
            window.CaptureRenderedFrame();
            WriteableBitmap? refined = window.CaptureRenderedFrame();
            Assert.NotNull(refined);
            Assert.True(NeighborhoodBrightness(refined!, (int)Math.Round(bx), (int)Math.Round(by)) > 100,
                "Refined 3px 文本须画简化条（基线落像素）");

            canvas.SetQualityForTests(RenderQuality.Interactive);
            canvas.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            WriteableBitmap? interactive = window.CaptureRenderedFrame();
            Assert.NotNull(interactive);
            Assert.True(NeighborhoodBrightness(interactive!, (int)Math.Round(bx), (int)Math.Round(by)) < 90,
                "Interactive 3px 文本须跳过（quality seam 到达 Render）");
        }
        finally { window.Close(); }
    }

    /// <summary>Max RGB channel in a 5x5 neighborhood (anti-aliased 1px stroke may
    /// not fully cover the exact mathematical point), bounds-checked.</summary>
    private static int NeighborhoodBrightness(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int max = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= fb.Size.Width || py >= fb.Size.Height) continue;
                int offset = py * fb.RowBytes + px * 4;
                int b = Marshal.ReadByte(fb.Address, offset);
                int g = Marshal.ReadByte(fb.Address, offset + 1);
                int r = Marshal.ReadByte(fb.Address, offset + 2);
                max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
            }
        return max;
    }

    /// <summary>Max of one RGBA channel (0=R,1=G,2=B; headless frames are Rgba8888)
    /// over 5x5 AA neighborhoods sampled on an 8px grid inside the given screen
    /// box, bounds-checked.</summary>
    private static int MaxChannelInBox(WriteableBitmap frame, double x0, double y0, double x1, double y1, int channel)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int max = 0;
        for (int y = (int)Math.Round(y0); y <= (int)Math.Round(y1); y += 8)
            for (int x = (int)Math.Round(x0); x <= (int)Math.Round(x1); x += 8)
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int px = x + dx, py = y + dy;
                        if (px < 0 || py < 0 || px >= fb.Size.Width || py >= fb.Size.Height) continue;
                        max = Math.Max(max, Marshal.ReadByte(fb.Address, py * fb.RowBytes + px * 4 + channel));
                    }
        return max;
    }
}
