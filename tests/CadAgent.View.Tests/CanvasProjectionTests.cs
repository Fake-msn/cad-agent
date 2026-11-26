using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 2: CanvasProjection（绘制器专用变换快照）。From(vm) 必须与
/// CanvasViewModel.WorldToScreen 完全一致（含 Y 翻转/offset/pan/zoom），
/// ExtraOffset 只平移屏幕输出、不参与 ScreenToWorld 逆变换（调用方自行扣除），
/// WorldViewport 归一且未 Fit 时返回反相空标记（与 VM 同口径）。
/// </summary>
public class CanvasProjectionTests
{
    private static RenderItem Line(double x0, double y0, double x1, double y1) =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, "0", 0xFFFFFF, null, 0, 0, "1");

    private static CanvasViewModel Fitted(double zoom = 1)
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 50), Line(20, 10, 80, 40) });
        vm.Fit((0, 0, 100, 50), 800, 600);
        if (zoom != 1) vm.ZoomAt(zoom, 400, 300);
        return vm;
    }

    [Fact]
    public void From_MatchesViewModel_WorldToScreen_AtCenterAndCorners()
    {
        CanvasViewModel vm = Fitted();
        CanvasProjection p = CanvasProjection.From(vm);

        Assert.Equal(vm.Scale, p.Scale, 12);
        Assert.Equal(vm.Pan.X, p.PanX, 12);
        Assert.Equal(vm.Pan.Y, p.PanY, 12);

        foreach ((double x, double y) in new[] { (50.0, 25.0), (0.0, 0.0), (100.0, 50.0), (-7.5, 123.0) })
        {
            (double ex, double ey) = vm.WorldToScreen(x, y);
            (double ax, double ay) = p.WorldToScreen(x, y);
            Assert.Equal(ex, ax, 9);
            Assert.Equal(ey, ay, 9);
            (double wx, double wy) = p.ScreenToWorld(ax, ay);
            Assert.Equal(x, wx, 9);
            Assert.Equal(y, wy, 9);
        }
    }

    [Fact]
    public void From_ZoomedAndPanned_StillMatchesViewModel()
    {
        CanvasViewModel vm = Fitted();
        vm.ZoomAt(2.75, 123, 456);
        vm.PanBy(-40, 25);
        CanvasProjection p = CanvasProjection.From(vm);

        (double ex, double ey) = vm.WorldToScreen(30.5, 12.25);
        (double ax, double ay) = p.WorldToScreen(30.5, 12.25);
        Assert.Equal(ex, ax, 9);
        Assert.Equal(ey, ay, 9);
        (double wx, double wy) = p.ScreenToWorld(ax, ay);
        Assert.Equal(30.5, wx, 9);
        Assert.Equal(12.25, wy, 9);
    }

    [Fact]
    public void ExtraOffset_ShiftsScreenOnly_AndInverseStillUsesBase()
    {
        const double ex = 17.5, ey = -9.25;
        CanvasViewModel vm = Fitted();
        CanvasProjection baseP = CanvasProjection.From(vm);
        CanvasProjection shifted = CanvasProjection.From(vm, extraOffsetX: ex, extraOffsetY: ey);

        (double bx, double by) = baseP.WorldToScreen(40, 20);
        (double sx, double sy) = shifted.WorldToScreen(40, 20);
        Assert.Equal(bx + ex, sx, 9);
        Assert.Equal(by - ey, sy, 9); // ExtraOffsetY 与 OffsetY 同号语义（正值上移）

        // ScreenToWorld 不含 ExtraOffset：调用方先扣除额外偏移，再逆变换。
        (double wx, double wy) = shifted.ScreenToWorld(sx - ex, sy + ey);
        Assert.Equal(40, wx, 9);
        Assert.Equal(20, wy, 9);
    }

    [Fact]
    public void FitView_MatchesViewModelFit_SameBoundsAndViewport()
    {
        // 交叉断言：FitView（快照投影）与 CanvasViewModel.Fit 同数学 → 同 scale/offset，
        // 且任意世界点 WorldToScreen 完全一致（同 bounds/同视口）。
        var items = new[] { Line(0, 0, 100, 50), Line(20, 10, 80, 40) };
        (double minX, double minY, double maxX, double maxY) = RenderModelBuilder.Bounds(items);
        var vm = new CanvasViewModel(items);
        vm.Fit((minX, minY, maxX, maxY), 800, 600);
        CanvasProjection fit = CanvasProjection.FitView((minX, minY, maxX, maxY), 800, 600);
        CanvasProjection from = CanvasProjection.From(vm);

        Assert.Equal(vm.Scale, fit.Scale, 12);
        Assert.Equal(from.OffsetX, fit.OffsetX, 12);
        Assert.Equal(from.OffsetY, fit.OffsetY, 12);
        Assert.Equal(from.PanX, fit.PanX, 12);
        Assert.Equal(from.PanY, fit.PanY, 12);
        foreach ((double x, double y) in new[] { (0.0, 0.0), (100.0, 50.0), (40.5, 12.25), (-7.0, 88.0) })
        {
            (double vx, double vy) = vm.WorldToScreen(x, y);
            (double fx, double fy) = fit.WorldToScreen(x, y);
            Assert.Equal(vx, fx, 9);
            Assert.Equal(vy, fy, 9);
        }
    }

    [Fact]
    public void FitView_NonPositiveViewport_DegeneratesLikeViewModelFit()
    {
        var items = new[] { Line(0, 0, 100, 50) };
        var vm = new CanvasViewModel(items);
        (double minX, double minY, double maxX, double maxY) = RenderModelBuilder.Bounds(items);
        vm.Fit((minX, minY, maxX, maxY), 0, 0);
        CanvasProjection fit = CanvasProjection.FitView((minX, minY, maxX, maxY), 0, 0);
        Assert.Equal(1, fit.Scale);
        Assert.Equal(0, fit.OffsetX);
        Assert.Equal(0, fit.OffsetY);
        Assert.Equal(vm.Scale, fit.Scale, 12);
        Assert.Equal(vm.WorldToScreen(10, 10).X, fit.WorldToScreen(10, 10).X, 9);
        Assert.Equal(vm.WorldToScreen(10, 10).Y, fit.WorldToScreen(10, 10).Y, 9);
    }

    [Fact]
    public void WorldViewport_Normalized_MatchesViewModel()
    {
        CanvasViewModel vm = Fitted();
        CanvasProjection p = CanvasProjection.From(vm);
        (double minX, double minY, double maxX, double maxY) = p.WorldViewport();
        (double vmMinX, double vmMinY, double vmMaxX, double vmMaxY) = vm.WorldViewport();
        Assert.Equal(vmMinX, minX, 9);
        Assert.Equal(vmMinY, minY, 9);
        Assert.Equal(vmMaxX, maxX, 9);
        Assert.Equal(vmMaxY, maxY, 9);
        Assert.True(minX < maxX && minY < maxY);
    }

    [Fact]
    public void WorldViewport_Unfitted_ReturnsEmptyMarker()
    {
        // 未 Fit（viewport=0）→ (0,0,-1,-1) 反相空矩形（Min>Max），调用方判空。
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1) });
        CanvasProjection p = CanvasProjection.From(vm);
        Assert.Equal((0.0, 0.0, -1.0, -1.0), p.WorldViewport());
    }

    [Fact]
    public void WorldRectOf_WithExtraOffset_ExpandsCacheViewport()
    {
        // 位图缓存：屏幕矩形（缓存位图坐标）含额外偏移 → 世界矩形按 extra/Scale 平移
        // （X 反向外扩，Y 翻转后同向外扩），覆盖视口 + 四周外扩像素。
        CanvasViewModel vm = Fitted();
        CanvasProjection plain = CanvasProjection.From(vm);
        CanvasProjection p = plain with { ExtraOffsetX = 5, ExtraOffsetY = -5 };
        (double minX, double minY, double maxX, double maxY) = p.WorldRectOf(0, 0, p.ViewportW, p.ViewportH);
        (double pMinX, double pMinY, double pMaxX, double pMaxY) = plain.WorldViewport();
        double shift = 5 / vm.Scale;
        Assert.Equal(pMinX - shift, minX, 9);
        Assert.Equal(pMaxX - shift, maxX, 9);
        Assert.Equal(pMinY + shift, minY, 9);
        Assert.Equal(pMaxY + shift, maxY, 9);
    }
}
