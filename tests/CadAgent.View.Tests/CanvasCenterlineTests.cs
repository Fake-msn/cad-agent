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
/// 天读证据中心线叠加（画布侧）：CanvasViewModel.SetCenterlines 存储+重绘；
/// ShowCenterlines 默认关、切换重绘；SelectByHandle 句柄定位（含 Insert 展开子图元
/// "{handle}:{i}"）。[AvaloniaFact] 帧测试：虚线叠加实际落帧（青色通道探针）。
/// </summary>
public class CanvasCenterlineTests
{
    private static RenderItem Line(double x0, double y0, double x1, double y1, string handle) =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, "0", 0xFFFFFF, null, 0, 0, handle);

    private static CanvasViewModel Fitted()
    {
        var vm = new CanvasViewModel(new[]
        {
            Line(0, 0, 10, 0, "A1"),
            Line(0, 10, 10, 10, "A1:0"), // Insert 展开子图元句柄口径
        });
        vm.Fit((0, 0, 10, 10), 800, 600);
        return vm;
    }

    [Fact]
    public void SetCenterlines_StoresAndInvalidates()
    {
        CanvasViewModel vm = Fitted();
        int invalidated = 0;
        vm.RenderInvalidated += () => invalidated++;

        var seg = new CenterlineSeg("A1", 0, 5, 10, 5);
        vm.SetCenterlines(new[] { seg });

        Assert.Same(seg, Assert.Single(vm.Centerlines));
        Assert.Equal(1, invalidated);
    }

    [Fact]
    public void ShowCenterlines_DefaultsFalse_ChangeInvalidates()
    {
        CanvasViewModel vm = Fitted();
        Assert.False(vm.ShowCenterlines);
        int invalidated = 0;
        vm.RenderInvalidated += () => invalidated++;

        vm.ShowCenterlines = true;
        Assert.Equal(1, invalidated);
        vm.ShowCenterlines = true; // 同值不重复重绘
        Assert.Equal(1, invalidated);
        vm.ShowCenterlines = false;
        Assert.Equal(2, invalidated);
    }

    [Fact]
    public void SelectByHandle_SelectsParentAndExpandedChildren()
    {
        CanvasViewModel vm = Fitted();
        Assert.True(vm.SelectByHandle("A1"));
        Assert.Equal("A1", vm.SelectedHandle);
        Assert.Contains("A1", vm.HighlightedHandles);
        Assert.Contains("A1:0", vm.HighlightedHandles);
        // CenterOn：命中并集中心 (5,5) 落在视口中心。
        (double cx, double cy) = vm.WorldToScreen(5, 5);
        Assert.Equal(400, cx, 3);
        Assert.Equal(300, cy, 3);
    }

    [Fact]
    public void SelectByHandle_Unknown_ReturnsFalseAndKeepsSelection()
    {
        CanvasViewModel vm = Fitted();
        Assert.False(vm.SelectByHandle("ZZ"));
        Assert.False(vm.SelectByHandle(""));
        Assert.Null(vm.SelectedIndex);
    }

    [Fact]
    public void HitTest_OnCenterlineOverlayMidpoint_ReturnsNoEntity()
    {
        // 真实图元在 y=10，中心线叠加在 y=5：叠加中点不得命中任何图元（只读叠加不参与命中）。
        var vm = new CanvasViewModel(new[] { Line(0, 10, 10, 10, "A1") });
        vm.Fit((0, 0, 10, 10), 800, 600);
        vm.SetCenterlines(new[] { new CenterlineSeg("A1", 0, 5, 10, 5) });
        vm.ShowCenterlines = true;

        Assert.Equal(0, vm.HitTest(5, 10, 0.2)); // 对照：真实图元命中
        Assert.Null(vm.HitTest(5, 5, 0.2)); // 中心线叠加中点 → 无命中
    }

    [AvaloniaFact]
    public void Canvas_RendersDashedCenterlineOverlay_WhenShown()
    {
        // 空心场景（仅中心线）：关态帧无青色像素；开态帧出现青色虚线像素。
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 0, "A1") });
        var canvas = new DrawingCanvas(vm);
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.SetCenterlines(new[] { new CenterlineSeg("A1", 0, 5, 100, 5) });

            window.CaptureRenderedFrame();
            WriteableBitmap? off = window.CaptureRenderedFrame();
            Assert.NotNull(off);
            Assert.Equal(0, CountAccentPixels(off!));

            vm.ShowCenterlines = true;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            WriteableBitmap? on = window.CaptureRenderedFrame();
            Assert.NotNull(on);
            Assert.True(CountAccentPixels(on!) > 100, "虚线中心线须实际落帧（青色像素可见）");
        }
        finally { window.Close(); }
    }

    /// <summary>统计青色叠加笔画像素：三通道一低两高（近 0/229/255；不依赖帧通道序），
    /// 背景（30,30,30）与白线（255,255,255）均不满足。</summary>
    private static int CountAccentPixels(WriteableBitmap frame)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int count = 0;
        for (int y = 0; y < fb.Size.Height; y++)
            for (int x = 0; x < fb.Size.Width; x++)
            {
                int offset = y * fb.RowBytes + x * 4;
                int c0 = Marshal.ReadByte(fb.Address, offset);
                int c1 = Marshal.ReadByte(fb.Address, offset + 1);
                int c2 = Marshal.ReadByte(fb.Address, offset + 2);
                int min = Math.Min(c0, Math.Min(c1, c2));
                int max = Math.Max(c0, Math.Max(c1, c2));
                if (min < 80 && max > 200 && c0 + c1 + c2 - min - max > 150) count++;
            }
        return count;
    }
}
