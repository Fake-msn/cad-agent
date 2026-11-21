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
