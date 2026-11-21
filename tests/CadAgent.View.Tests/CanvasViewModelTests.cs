using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 3: CanvasViewModel (pure C#, no Avalonia). Pins the brief's transform:
/// screenX = (worldX-minX)*baseScale*Zoom + panX + offsetX;
/// screenY = vpH - ((worldY-minY)*baseScale*Zoom + panY) - offsetY (Y flip).
/// Fit: baseScale = min(vpW/w, vpH/h) * 0.9, content centered; Zoom=1 means fit.
/// ZoomAt keeps the world point under the cursor fixed. PanBy takes SCREEN-space
/// deltas (+dy moves content down). HitTest tolerance is in world units.
/// </summary>
public class CanvasViewModelTests
{
    private static RenderItem Line(double x0, double y0, double x1, double y1, string layer = "0", string handle = "1") =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem Circle(double cx, double cy, double r, string layer = "0", string handle = "1") =>
        new(RenderKind.Circle, new[] { (cx, cy), (r, 0.0) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem Arc(double cx, double cy, double r, double startDeg, double endDeg, string layer = "0", string handle = "1") =>
        new(RenderKind.Arc, new[] { (cx, cy), (r, startDeg), (endDeg, 0.0) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem Poly(params (double X, double Y)[] pts) =>
        new(RenderKind.Polyline, pts, "0", 0xFFFFFF, null, 0, 0, "1");

    private static RenderItem Text(string value, double x, double y, double height, string layer = "0", string handle = "1") =>
        new(RenderKind.Text, new[] { (x, y) }, layer, 0xFFFFFF, value, height, 0, handle);

    private static CanvasViewModel Fitted(out (double X, double Y) anchorWorld)
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 50) });
        vm.Fit((0, 0, 100, 50), 1000, 500);
        anchorWorld = vm.ScreenToWorld(300, 200);
        return vm;
    }

    // ---- Fit ----

    [Fact]
    public void Fit_LandscapeBounds_MarginAndCentering()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 50) });
        vm.Fit((0, 0, 100, 50), 1000, 500);
        // baseScale = min(1000/100, 500/50) * 0.9 = 9; offsets (50, 25).
        Assert.Equal(1, vm.Zoom);
        Assert.Equal((0.0, 0.0), vm.Pan);
        Assert.Equal(9, vm.Scale, 6);
        (double x, double y) = vm.WorldToScreen(0, 0);
        Assert.Equal(50, x, 6);
        Assert.Equal(475, y, 6); // world minY at the BOTTOM (Y flip)
        (x, y) = vm.WorldToScreen(100, 50);
        Assert.Equal(950, x, 6);
        Assert.Equal(25, y, 6);
        (x, y) = vm.WorldToScreen(50, 25);
        Assert.Equal(500, x, 6);
        Assert.Equal(250, y, 6); // bounds center -> viewport center
    }

    [Fact]
    public void Fit_HeightLimited_UsesMinAxisScale()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 100) });
        vm.Fit((0, 0, 100, 100), 1000, 500);
        // baseScale = min(10, 5) * 0.9 = 4.5; offsetX = (1000-450)/2 = 275.
        Assert.Equal(4.5, vm.Scale, 6);
        (double x, double y) = vm.WorldToScreen(0, 0);
        Assert.Equal(275, x, 6);
        Assert.Equal(475, y, 6);
        (x, y) = vm.WorldToScreen(100, 100);
        Assert.Equal(725, x, 6);
        Assert.Equal(25, y, 6);
    }

    [Fact]
    public void Fit_ZeroHeight_HorizontalLineCenteredVertically()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 5, 10, 5) });
        vm.Fit((0, 5, 10, 5), 100, 100);
        Assert.Equal(9, vm.Scale, 6); // width-limited: 100/10*0.9
        (double x, double y) = vm.WorldToScreen(0, 5);
        Assert.Equal(5, x, 6);
        Assert.Equal(50, y, 6);
        (x, y) = vm.WorldToScreen(10, 5);
        Assert.Equal(95, x, 6);
        Assert.Equal(50, y, 6);
    }

    [Fact]
    public void Fit_ZeroWidth_VerticalLineCenteredHorizontally()
    {
        var vm = new CanvasViewModel(new[] { Line(3, 0, 3, 10) });
        vm.Fit((3, 0, 3, 10), 100, 200);
        Assert.Equal(18, vm.Scale, 6); // height-limited: 200/10*0.9
        (double x, double y) = vm.WorldToScreen(3, 0);
        Assert.Equal(50, x, 6);
        Assert.Equal(190, y, 6);
        (x, y) = vm.WorldToScreen(3, 10);
        Assert.Equal(50, x, 6);
        Assert.Equal(10, y, 6);
    }

    [Fact]
    public void Fit_DegeneratePointBounds_FallbackScaleOne()
    {
        var vm = new CanvasViewModel(new[] { Line(5, 7, 5, 7) });
        vm.Fit((5, 7, 5, 7), 100, 100);
        Assert.Equal(1, vm.Scale, 6);
        (double x, double y) = vm.WorldToScreen(5, 7);
        Assert.Equal(50, x, 6);
        Assert.Equal(50, y, 6);
    }

    [Fact]
    public void Fit_ResetsZoomAndPan()
    {
        var vm = Fitted(out _);
        vm.ZoomAt(2.5, 300, 200);
        vm.PanBy(40, -25);
        vm.Fit((0, 0, 100, 50), 1000, 500);
        Assert.Equal(1, vm.Zoom);
        Assert.Equal((0.0, 0.0), vm.Pan);
        (double x, double y) = vm.WorldToScreen(0, 0);
        Assert.Equal(50, x, 6);
        Assert.Equal(475, y, 6);
    }

    [Fact]
    public void Fit_RaisesRenderInvalidated()
    {
        var vm = new CanvasViewModel(Array.Empty<RenderItem>());
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.Fit((0, 0, 1, 1), 100, 100);
        Assert.Equal(1, raised);
    }

    // ---- ZoomAt ----

    [Fact]
    public void ZoomAt_ZoomIn_AnchorWorldPointStaysUnderCursor()
    {
        var vm = Fitted(out (double wx, double wy) anchor);
        vm.ZoomAt(1.5, 300, 200);
        Assert.Equal(1.5, vm.Zoom, 6);
        (double x, double y) = vm.WorldToScreen(anchor.wx, anchor.wy);
        Assert.Equal(300, x, 6);
        Assert.Equal(200, y, 6);
    }

    [Fact]
    public void ZoomAt_ZoomOutAfterPan_AnchorStillFixed()
    {
        var vm = Fitted(out _);
        vm.PanBy(40, -25);
        (double wx, double wy) = vm.ScreenToWorld(300, 200);
        vm.ZoomAt(0.4, 300, 200);
        (double x, double y) = vm.WorldToScreen(wx, wy);
        Assert.Equal(300, x, 6);
        Assert.Equal(200, y, 6);
        Assert.Equal(0.4, vm.Zoom, 6);
    }

    [Fact]
    public void ZoomAt_RepeatedZooms_AnchorInvariantAccumulates()
    {
        var vm = Fitted(out (double wx, double wy) anchor);
        vm.ZoomAt(1.2, 300, 200);
        vm.ZoomAt(1.2, 300, 200);
        vm.ZoomAt(0.7, 300, 200);
        (double x, double y) = vm.WorldToScreen(anchor.wx, anchor.wy);
        Assert.Equal(300, x, 6);
        Assert.Equal(200, y, 6);
    }

    [Fact]
    public void ZoomAt_ClampsExtremeFactors_StaysFinite()
    {
        var vm = Fitted(out _);
        vm.ZoomAt(1e300, 300, 200);
        Assert.True(vm.Zoom <= 1e6);
        (double x, double y) = vm.WorldToScreen(50, 25);
        Assert.True(double.IsFinite(x) && double.IsFinite(y));
        vm.ZoomAt(1e-300, 300, 200);
        Assert.True(vm.Zoom >= 1e-6);
        (x, y) = vm.WorldToScreen(50, 25);
        Assert.True(double.IsFinite(x) && double.IsFinite(y));
    }

    [Fact]
    public void ZoomAt_RaisesRenderInvalidated()
    {
        var vm = Fitted(out _);
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.ZoomAt(1.5, 300, 200);
        Assert.Equal(1, raised);
    }

    // ---- PanBy ----

    [Fact]
    public void PanBy_ScreenSpaceDelta_MovesContentExactly()
    {
        var vm = Fitted(out _);
        (double x0, double y0) = vm.WorldToScreen(10, 10);
        vm.PanBy(30, -20);
        (double x1, double y1) = vm.WorldToScreen(10, 10);
        Assert.Equal(x0 + 30, x1, 6);
        Assert.Equal(y0 - 20, y1, 6);
        Assert.Equal((30.0, 20.0), vm.Pan); // Pan.Y is un-flipped content offset
    }

    [Fact]
    public void PanBy_RaisesRenderInvalidated()
    {
        var vm = Fitted(out _);
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.PanBy(1, 2);
        Assert.Equal(1, raised);
    }

    // ---- transform roundtrip ----

    [Fact]
    public void WorldToScreen_ScreenToWorld_RoundtripAfterZoomAndPan()
    {
        var vm = Fitted(out _);
        vm.ZoomAt(2.3, 410, 260);
        vm.PanBy(-17, 42);
        foreach ((double wx, double wy) in new[] { (0.0, 0.0), (100.0, 50.0), (-13.5, 77.25), (50.0, 25.0) })
        {
            (double sx, double sy) = vm.WorldToScreen(wx, wy);
            (double rx, double ry) = vm.ScreenToWorld(sx, sy);
            Assert.Equal(wx, rx, 6);
            Assert.Equal(wy, ry, 6);
        }
    }

    [Fact]
    public void DefaultState_BeforeFit_UnitScaleRoundtrip()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1) });
        Assert.Equal(1, vm.Zoom);
        Assert.Equal((0.0, 0.0), vm.Pan);
        Assert.Equal(1, vm.Scale, 6);
        (double sx, double sy) = vm.WorldToScreen(3, 4);
        (double rx, double ry) = vm.ScreenToWorld(sx, sy);
        Assert.Equal(3, rx, 6);
        Assert.Equal(4, ry, 6);
    }

    // ---- VisibleItems / HiddenLayers ----

    [Fact]
    public void VisibleItems_FiltersHiddenLayers()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1, "A"), Line(2, 2, 3, 3, "B"), Line(4, 4, 5, 5, "A") });
        vm.HiddenLayers.Add("B");
        Assert.Equal(2, vm.VisibleItems.Count());
        Assert.All(vm.VisibleItems, i => Assert.Equal("A", i.Layer));
    }

    [Fact]
    public void HiddenLayers_CaseInsensitive()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1, "WALL") });
        vm.HiddenLayers.Add("wall");
        Assert.Empty(vm.VisibleItems);
    }

    // ---- HitTest ----

    [Fact]
    public void HitTest_Line_PerpendicularDistanceTolerance()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 0) });
        Assert.Equal(0, vm.HitTest(5, 0.4, 0.5));
        Assert.Null(vm.HitTest(5, 0.6, 0.5));
        Assert.Equal(0, vm.HitTest(5, -0.5, 0.5)); // boundary inclusive
    }

    [Fact]
    public void HitTest_Line_SegmentNotInfiniteLine()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 0) });
        Assert.Null(vm.HitTest(11, 0, 0.5));   // beyond end
        Assert.Null(vm.HitTest(-1, 0, 0.5));   // before start
        Assert.Equal(0, vm.HitTest(10.4, 0, 0.5)); // within tolerance of endpoint
    }

    [Fact]
    public void HitTest_Circle_BoundaryDistanceOnly()
    {
        var vm = new CanvasViewModel(new[] { Circle(0, 0, 5) });
        Assert.Equal(0, vm.HitTest(5.2, 0, 0.5));  // near boundary
        Assert.Null(vm.HitTest(0, 0, 0.5));        // center is NOT a hit
        Assert.Null(vm.HitTest(3, 0, 0.5));        // interior is NOT a hit
        Assert.Null(vm.HitTest(6, 0, 0.5));        // outside tolerance
    }

    [Fact]
    public void HitTest_Arc_RadialAndAngularSpan()
    {
        var vm = new CanvasViewModel(new[] { Arc(0, 0, 5, 0, 90) });
        double d = 5 * Math.Sqrt(0.5);
        Assert.Equal(0, vm.HitTest(d, d, 0.1));    // 45 deg, on boundary, inside span
        Assert.Null(vm.HitTest(-5, 0, 0.1));       // 180 deg: on circle but outside span
        Assert.Null(vm.HitTest(6, 0, 0.1));        // inside span but radial miss
        Assert.Equal(0, vm.HitTest(5, 0, 0.1));    // start endpoint
        Assert.Equal(0, vm.HitTest(0, 5, 0.1));    // end endpoint
    }

    [Fact]
    public void HitTest_Polyline_MinSegmentDistance()
    {
        var vm = new CanvasViewModel(new[] { Poly((0, 0), (10, 0), (10, 10)) });
        Assert.Equal(0, vm.HitTest(10, 5, 0.5));   // second segment
        Assert.Equal(0, vm.HitTest(9.7, 9.7, 0.5)); // near corner
        Assert.Null(vm.HitTest(5, 5, 0.5));        // far from both segments
    }

    [Fact]
    public void HitTest_Text_BoundingBoxWidthEstimate()
    {
        // width estimate = len * height * 0.6 = 2 * 2 * 0.6 = 2.4; box (0,0)-(2.4,2).
        var vm = new CanvasViewModel(new[] { Text("AB", 0, 0, 2) });
        Assert.Equal(0, vm.HitTest(1, 1, 0));
        Assert.Equal(0, vm.HitTest(2.4, 2, 0));    // far corner inclusive
        Assert.Null(vm.HitTest(2.5, 1, 0));
        Assert.Null(vm.HitTest(1, -0.1, 0));
        Assert.Equal(0, vm.HitTest(2.6, 1, 0.2));  // tolerance pads the box
    }

    [Fact]
    public void HitTest_SkipsHiddenLayers_ReturnsOriginalIndex()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 0, "H"), Line(0, 0, 10, 0, "V") });
        vm.HiddenLayers.Add("H");
        Assert.Equal(1, vm.HitTest(5, 0, 0.5));
    }

    [Fact]
    public void HitTest_FirstHitWins()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 0), Line(0, 0, 10, 0) });
        Assert.Equal(0, vm.HitTest(5, 0, 0.5));
    }

    // ---- selection ----

    [Fact]
    public void Select_SetsIndexHandleAndItem_RaisesEvent()
    {
        // T4: SelectedHandle is the item's real DXF handle string, not the index.
        var items = new[] { Line(0, 0, 1, 1, handle: "A1"), Line(2, 2, 3, 3, handle: "B2") };
        var vm = new CanvasViewModel(items);
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.Select(1);
        Assert.Equal(1, vm.SelectedIndex);
        Assert.Equal("B2", vm.SelectedHandle);
        Assert.Same(items[1], vm.SelectedItem);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Select_NullOrOutOfRange_ClearsSelection()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1) });
        vm.Select(0);
        vm.Select(null);
        Assert.Null(vm.SelectedIndex);
        Assert.Null(vm.SelectedHandle);
        Assert.Null(vm.SelectedItem);
        vm.Select(5);
        Assert.Null(vm.SelectedIndex);
        vm.Select(-1);
        Assert.Null(vm.SelectedIndex);
    }

    // ---- T5: multi-handle issue highlight + center-on ----

    [Fact]
    public void SelectHandles_SetsHighlightSet_FirstMatchSelected_RaisesOnce()
    {
        var items = new[] { Line(0, 0, 1, 1, handle: "A"), Line(2, 2, 3, 3, handle: "B") };
        var vm = new CanvasViewModel(items);
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.SelectHandles(new[] { "B", "A" });
        Assert.Equal(new[] { "A", "B" }, vm.HighlightedHandles.OrderBy(h => h).ToArray());
        Assert.Equal(0, vm.SelectedIndex); // first item (in Items order) whose handle matches
        Assert.Equal("A", vm.SelectedHandle);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SelectHandles_ReplacesPreviousSet_EmptyClears()
    {
        var items = new[] { Line(0, 0, 1, 1, handle: "A"), Line(2, 2, 3, 3, handle: "B") };
        var vm = new CanvasViewModel(items);
        vm.SelectHandles(new[] { "A", "B" });
        vm.SelectHandles(new[] { "B" });
        Assert.Equal(new[] { "B" }, vm.HighlightedHandles.ToArray());
        Assert.Equal(1, vm.SelectedIndex);
        vm.SelectHandles(Array.Empty<string>());
        Assert.Empty(vm.HighlightedHandles);
        Assert.Null(vm.SelectedIndex);
    }

    [Fact]
    public void SelectHandles_UnknownOrEmptyHandles_NoSelection()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1, handle: "") });
        vm.SelectHandles(new[] { "", "NO-SUCH" });
        Assert.Null(vm.SelectedIndex); // empty item handle never matches
    }

    [Fact]
    public void Select_ClearsIssueHighlights()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 1, 1, handle: "A"), Line(2, 2, 3, 3, handle: "B") });
        vm.SelectHandles(new[] { "A", "B" });
        vm.Select(1); // user takes over selection -> issue highlight set cleared
        Assert.Empty(vm.HighlightedHandles);
        Assert.Equal("B", vm.SelectedHandle);
    }

    [Fact]
    public void CenterOn_MatchedHandles_CentersBounds_KeepsZoom()
    {
        var items = new[] { Line(0, 0, 10, 10, handle: "A"), Line(100, 100, 110, 110, handle: "B") };
        var vm = new CanvasViewModel(items);
        vm.Fit((0, 0, 110, 110), 1000, 500);
        vm.ZoomAt(2, 500, 250);
        double zoomBefore = vm.Zoom;
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.CenterOn(new[] { "B" });
        // Matched bounds (100,100)-(110,110), center (105,105) -> viewport center.
        (double sx, double sy) = vm.WorldToScreen(105, 105);
        Assert.Equal(500, sx, 6);
        Assert.Equal(250, sy, 6);
        Assert.Equal(zoomBefore, vm.Zoom);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void CenterOn_MultipleHandles_CentersUnionBounds()
    {
        var items = new[] { Line(0, 0, 10, 10, handle: "A"), Line(100, 100, 110, 110, handle: "B") };
        var vm = new CanvasViewModel(items);
        vm.Fit((0, 0, 110, 110), 1000, 500);
        vm.PanBy(120, -80); // displace first so centering is observable
        vm.CenterOn(new[] { "A", "B" });
        (double sx, double sy) = vm.WorldToScreen(55, 55); // union center
        Assert.Equal(500, sx, 6);
        Assert.Equal(250, sy, 6);
    }

    [Fact]
    public void CenterOn_NoMatch_NoOpNoEvent()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 10, handle: "A") });
        vm.Fit((0, 0, 10, 10), 1000, 500);
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.CenterOn(new[] { "NO-SUCH" });
        Assert.Equal((0.0, 0.0), vm.Pan);
        Assert.Equal(0, raised);
        vm.CenterOn(Array.Empty<string>());
        Assert.Equal(0, raised);
    }

    [Fact]
    public void CenterOn_BeforeFit_NoOp()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 10, handle: "A") });
        int raised = 0;
        vm.RenderInvalidated += () => raised++;
        vm.CenterOn(new[] { "A" }); // viewport unknown -> no-op
        Assert.Equal((0.0, 0.0), vm.Pan);
        Assert.Equal(0, raised);
    }
}
