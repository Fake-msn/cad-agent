using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 1: RenderItemIndex (uniform grid broad phase) + CanvasViewModel wiring.
/// The index must be pure C# (no Avalonia) and must never change hit semantics: the
/// exact-hit math is the old CanvasViewModel.Hits, so FirstHit is compared against a
/// local linear reference over random points x tolerances.
/// </summary>
public class RenderItemIndexTests
{
    private const double TextWidthPerHeight = 0.6;

    private static RenderItem Line(double x0, double y0, double x1, double y1, string layer = "0", string handle = "1") =>
        new(RenderKind.Line, new[] { (x0, y0), (x1, y1) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem Circle(double cx, double cy, double r, string layer = "0", string handle = "1") =>
        new(RenderKind.Circle, new[] { (cx, cy), (r, 0.0) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem Arc(double cx, double cy, double r, double startDeg, double endDeg, string layer = "0", string handle = "1") =>
        new(RenderKind.Arc, new[] { (cx, cy), (r, startDeg), (endDeg, 0.0) }, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem Polyline(string layer, string handle, params (double X, double Y)[] pts) =>
        new(RenderKind.Polyline, pts, layer, 0xFFFFFF, null, 0, 0, handle);

    private static RenderItem TextItem(string value, double x, double y, double height, string layer = "0", string handle = "1") =>
        new(RenderKind.Text, new[] { (x, y) }, layer, 0xFFFFFF, value, height, 0, handle);

    private static RenderItem Hatch(double[] xs, double[] ys, string layer = "0", string handle = "1")
    {
        var pts = new List<(double X, double Y)>();
        for (int i = 0; i < xs.Length; i++) pts.Add((xs[i], ys[i]));
        return new(RenderKind.Hatch, pts, layer, 0xFFFFFF, null, 0, 0, handle, Fill: true);
    }

    private static void AssertBounds(
        (double MinX, double MinY, double MaxX, double MaxY) actual,
        double minX, double minY, double maxX, double maxY)
    {
        Assert.Equal(minX, actual.MinX, 9);
        Assert.Equal(minY, actual.MinY, 9);
        Assert.Equal(maxX, actual.MaxX, 9);
        Assert.Equal(maxY, actual.MaxY, 9);
    }

    // ---- bbox per kind ----

    [Fact]
    public void BoundsOf_LinePolylineCircleArcText()
    {
        var items = new[]
        {
            Line(1, 2, 5, 8),                        // 0
            Circle(10, 10, 3),                       // 1
            Arc(0, 0, 2, 30, 120),                   // 2
            Polyline("0", "1", (0, 0), (2, 5), (-3, 1)), // 3
            TextItem("ABC", 5, 5, 2),                // 4
        };
        var index = new RenderItemIndex(items);

        AssertBounds(index.BoundsOf(0), 1, 2, 5, 8);
        AssertBounds(index.BoundsOf(1), 7, 7, 13, 13);
        // Arc 保守盒 = 整圆盒（圆心±半径，与 RenderModelBuilder.Bounds 同口径）
        AssertBounds(index.BoundsOf(2), -2, -2, 2, 2);
        AssertBounds(index.BoundsOf(3), -3, 0, 2, 5);
        // 字盒：插入点 + 宽(字符数×字高×0.6) × 高
        AssertBounds(index.BoundsOf(4), 5, 5, 5 + 3 * 2 * TextWidthPerHeight, 7);
    }

    [Fact]
    public void BoundsOf_Hatch_SkipsNaNSeparators()
    {
        // 两条边界路径以 (NaN,NaN) 分隔：NaN 不是坐标，不得污染包围盒。
        var hatch = Hatch(
            new[] { 0.0, 10, double.NaN, 4, 6 },
            new[] { 0.0, 0, double.NaN, 4, 6 });
        var index = new RenderItemIndex(new[] { hatch });
        AssertBounds(index.BoundsOf(0), 0, 0, 10, 6);
    }

    [Fact]
    public void BoundsOf_DegenerateItems_AreSafe()
    {
        var items = new[]
        {
            Polyline("0", "1"),               // 0: 空点
            Polyline("0", "1", (3, 4)),       // 1: 单点
            Circle(2, 2, 0),                  // 2: 零半径
            Line(7, 7, 7, 7),                 // 3: 退化线段
            new RenderItem(RenderKind.Circle, Array.Empty<(double, double)>(), "0", 0xFFFFFF, null, 0, 0, "1"), // 4: 无点圆
        };
        var index = new RenderItemIndex(items);

        // 无坐标 → 退化零盒（不参与查询，见 Query_... 测试）
        AssertBounds(index.BoundsOf(0), 0, 0, 0, 0);
        AssertBounds(index.BoundsOf(1), 3, 4, 3, 4);
        AssertBounds(index.BoundsOf(2), 2, 2, 2, 2);
        AssertBounds(index.BoundsOf(3), 7, 7, 7, 7);
        AssertBounds(index.BoundsOf(4), 0, 0, 0, 0);
    }

    // ---- Query ----

    [Fact]
    public void Query_DenseItemsAndLongSpanningItem()
    {
        var items = new List<RenderItem>();
        for (int i = 0; i < 500; i++) items.Add(Line(i * 10, 0, i * 10 + 1, 0));
        int longIndex = items.Count;
        items.Add(Line(-100, 0, 100000, 0)); // 跨越整幅 → overflow 候选
        int hatchIndex = items.Count;
        items.Add(Hatch(new[] { 2000.0, 2010, 2010, 2000 }, new[] { 500.0, 500, 510, 510 }));
        var index = new RenderItemIndex(items);

        // 远处小矩形：只命中第 3 项 + 长线（overflow，bbox 相交）
        var near = index.Query((29, -5, 32, 5));
        Assert.Equal(new[] { 3, longIndex }, near);

        // Hatch 区域（查询矩形跨过 y=0 上的长线；x 1999..2011 附近的短线段也相交）
        Assert.Equal(new[] { 200, 201, longIndex, hatchIndex }, index.Query((1999, -5, 2011, 511)));

        // 全幅查询：升序、无重复、含所有有效项
        var all = index.Query((-1000, -1000, 110000, 1000));
        Assert.Equal(Enumerable.Range(0, items.Count).ToArray(), all);

        // 完全在外：空
        Assert.Empty(index.Query((200000, 200000, 200001, 200001)));
    }

    [Fact]
    public void Query_TightCellBudget_PutsLongItemsInOverflowAndStaysCorrect()
    {
        var items = new List<RenderItem>();
        for (int i = 0; i < 40; i++) items.Add(Line(i * 100, 0, i * 100 + 1, 1));
        int longIndex = items.Count;
        items.Add(Line(0, 0, 4000, 0)); // 覆盖格数 > maxCellsPerItem → overflow
        var index = new RenderItemIndex(items, minCellsPerAxis: 4, maxCellsPerAxis: 8, maxCellsPerItem: 2);

        Assert.Equal(new[] { 7, longIndex }, index.Query((695, -5, 705, 5)));
        Assert.Equal(new[] { longIndex }, index.Query((10, -1, 20, 1)));
        Assert.Empty(index.Query((-100, -100, -50, -50)));
        // overflow 项即使查询矩形不覆盖其格，也按 bbox 过滤后参与
        Assert.Equal(new[] { longIndex }, index.Query((3590, -1, 3595, 1)));
    }

    [Fact]
    public void Query_ExtremeAspectContent_KeepsAxisCellsBounded()
    {
        // 宽 1e6 × 高 1：单轴格数必须仍 ≤ maxCellsPerAxis（否则索引爆炸）
        var items = new[]
        {
            Line(0, 0, 1e6, 0),
            Line(500, 0, 501, 1),
            Line(999000, 0, 999001, 1),
        };
        var index = new RenderItemIndex(items);
        Assert.Equal(new[] { 0, 1 }, index.Query((499, -1, 502, 2)));
        Assert.Equal(new[] { 0, 2 }, index.Query((998999, -1, 999002, 2)));
    }

    [Fact]
    public void Query_FiltersHiddenLayers()
    {
        var items = new[]
        {
            Line(0, 0, 10, 0, layer: "A"),
            Line(1, 0, 11, 0, layer: "B"),
            Line(2, 0, 12, 0, layer: "A"),
        };
        var index = new RenderItemIndex(items);
        var hidden = new HashSet<string>(new[] { "B" }, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { 0, 1, 2 }, index.Query((-1, -1, 13, 1)));
        Assert.Equal(new[] { 0, 2 }, index.Query((-1, -1, 13, 1), hidden));
        Assert.Equal(new[] { 0, 2 }, index.Query((-1, -1, 13, 1), new HashSet<string>(new[] { "b" }, StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Query_ReturnsAscendingUniqueIndices_ForItemsSpanningManyCells()
    {
        var items = new List<RenderItem>();
        for (int i = 0; i < 30; i++) items.Add(Line(i, i, 300 - i, 300 - i)); // 全幅斜线，跨多格
        items.Add(Line(0, 150, 300, 150));
        var index = new RenderItemIndex(items);

        var result = index.Query((0, 0, 300, 300));
        Assert.Equal(Enumerable.Range(0, items.Count).ToArray(), result);
        for (int i = 1; i < result.Count; i++) Assert.True(result[i] > result[i - 1]);
    }

    [Fact]
    public void Query_EmptyPointsItem_IsNeverCandidate()
    {
        var index = new RenderItemIndex(new[] { Polyline("0", "1"), Line(5, 5, 15, 5) });
        Assert.Equal(new[] { 1 }, index.Query((-1e9, -1e9, 1e9, 1e9)));
        Assert.Null(index.FirstHit(0, 0, 1)); // 空点图元不是候选，且 (0,0) 距线 5..15 有 7.07
    }

    [Fact]
    public void Query_EmptyIndexAndInvalidRect()
    {
        var empty = new RenderItemIndex(Array.Empty<RenderItem>());
        Assert.Empty(empty.Query((-1, -1, 1, 1)));
        Assert.Null(empty.FirstHit(0, 0, 1));

        var index = new RenderItemIndex(new[] { Line(0, 0, 10, 0) });
        Assert.Empty(index.Query((5, 5, 1, 1)));   // 反相矩形 = 空（调用方负责归一）
        Assert.Empty(index.Query((double.NaN, 0, 1, 1)));
    }

    // ---- FirstHit 等价（旧线性实现）----

    [Fact]
    public void FirstHit_MatchesLinearReference_OnThreeFixtures()
    {
        var rnd = new Random(20261126);
        (IReadOnlyList<RenderItem> Items, IReadOnlySet<string> Hidden)[] fixtures =
        {
            (SmallMixedFixture(), new HashSet<string>()),
            (LargeMixedFixture(rnd, 300), new HashSet<string>(new[] { "L1", "L4" }, StringComparer.OrdinalIgnoreCase)),
            (DenseFixture(rnd), new HashSet<string>(new[] { "D2" }, StringComparer.OrdinalIgnoreCase)),
        };
        double[] tolerances = { 0.25, 1.5, 6.0 };

        foreach ((IReadOnlyList<RenderItem> items, IReadOnlySet<string> hidden) in fixtures)
        {
            var index = new RenderItemIndex(items);
            int hits = 0;
            for (int p = 0; p < 500; p++)
            {
                (double x, double y) = p % 2 == 0
                    ? (NextRange(rnd, -200, 500), NextRange(rnd, -200, 300))
                    : PointNear(items[rnd.Next(items.Count)], rnd);
                foreach (double tol in tolerances)
                {
                    int? expected = LinearFirstHit(items, x, y, tol, hidden);
                    int? actual = index.FirstHit(x, y, tol, hidden);
                    Assert.True(expected == actual,
                        $"fixture mismatch at ({x:R},{y:R}) tol={tol}: expected={expected} actual={actual}");
                    if (expected is not null) hits++;
                }
            }
            // 等价不只靠"都 miss"：每套 fixture 必须有足量真实命中
            Assert.True(hits > 100, $"fixture too sparse: hits={hits}");
        }
    }

    [Fact]
    public void FirstHit_NegativeTolerance_ClampedLikeLegacy()
    {
        var items = new[] { Line(0, 0, 10, 0) };
        var index = new RenderItemIndex(items);
        Assert.Equal(0, index.FirstHit(5, 0, -3));
        Assert.Null(index.FirstHit(5, 1, -3));
        Assert.Equal(LinearFirstHit(items, 5, 1, -3, null), index.FirstHit(5, 1, -3));
    }

    [Fact]
    public void FirstHit_ReturnsFirstIndexInItemsOrder()
    {
        var items = new[] { Line(0, 0, 10, 0), Line(0, 0, 10, 0), Line(0, 1, 10, 1) };
        var index = new RenderItemIndex(items);
        Assert.Equal(0, index.FirstHit(5, 0, 0.1));
        Assert.Equal(2, index.FirstHit(5, 1, 0.1));
    }

    [Fact]
    public void FirstHit_TextBoxWidthAndArcSweep()
    {
        var items = new[]
        {
            Arc(0, 0, 10, 0, 90),             // 0: 第一象限弧
            TextItem("ABCD", 100, 100, 5),    // 1: 宽 = 4*5*0.6 = 12
        };
        var index = new RenderItemIndex(items);

        Assert.Equal(0, index.FirstHit(Math.Sqrt(50), Math.Sqrt(50), 0.5));   // 45°
        Assert.Null(index.FirstHit(-Math.Sqrt(50), Math.Sqrt(50), 0.5));      // 135° 不在扫掠内
        Assert.Equal(1, index.FirstHit(111.9, 104.9, 0.2));                   // 字盒内
        Assert.Null(index.FirstHit(112.5, 104.9, 0.2));                       // 超出字盒宽
    }

    // ---- CanvasViewModel 接线 ----

    [Fact]
    public void CanvasViewModel_BuildsIndexAndDelegatesHitTest()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 0, layer: "A"), Circle(50, 50, 5, layer: "B") });
        Assert.NotNull(vm.Index);
        Assert.Equal(1, vm.HitTest(55, 50, 0.1));
        Assert.Equal(1, vm.Index.FirstHit(55, 50, 0.1));

        vm.HiddenLayers.Add("B");
        Assert.Null(vm.HitTest(55, 50, 0.1));   // 隐藏层语义保持
        Assert.Equal(0, vm.HitTest(10, 0, 0.1));
    }

    [Fact]
    public void CanvasViewModel_QueryViewport_DelegatesToIndex()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 0), Line(100, 100, 110, 100, layer: "H") });
        Assert.Equal(new[] { 0, 1 }, vm.QueryViewport((-10, -10, 200, 200)));
        vm.HiddenLayers.Add("H");
        Assert.Equal(new[] { 0 }, vm.QueryViewport((-10, -10, 200, 200)));
    }

    [Fact]
    public void CanvasViewModel_Refresh_RebuildsIndex()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 10, 0) });
        Assert.Null(vm.HitTest(50, 50, 0.1));

        vm.Refresh(new[] { Line(0, 0, 10, 0), Circle(50, 50, 5) });
        Assert.Equal(1, vm.HitTest(55, 50, 0.1));
        Assert.Equal(new[] { 0, 1 }, vm.QueryViewport((0, 0, 60, 60)));

        vm.Refresh(new[] { Line(0, 0, 10, 0) });
        Assert.Null(vm.HitTest(55, 50, 0.1));
        Assert.Equal(new[] { 0 }, vm.QueryViewport((0, 0, 60, 60)));
    }

    [Fact]
    public void WorldViewport_IsNormalizedInverseOfScreenRect()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 50) });
        vm.Fit((0, 0, 100, 50), 1000, 500);

        var vp = vm.WorldViewport();
        var topLeft = vm.ScreenToWorld(0, 0);
        var bottomRight = vm.ScreenToWorld(1000, 500);

        // 屏幕 Y-down → 世界 Y-up：左上角是 MaxY，右下角是 MinY
        Assert.Equal(topLeft.X, vp.MinX, 9);
        Assert.Equal(bottomRight.X, vp.MaxX, 9);
        Assert.Equal(bottomRight.Y, vp.MinY, 9);
        Assert.Equal(topLeft.Y, vp.MaxY, 9);
        Assert.True(vp.MinX < vp.MaxX && vp.MinY < vp.MaxY);

        // Zoom/Pan 后仍互逆
        vm.ZoomAt(2.5, 400, 300);
        vm.PanBy(-120, 33);
        vp = vm.WorldViewport();
        topLeft = vm.ScreenToWorld(0, 0);
        bottomRight = vm.ScreenToWorld(1000, 500);
        Assert.Equal(topLeft.X, vp.MinX, 9);
        Assert.Equal(bottomRight.X, vp.MaxX, 9);
        Assert.Equal(bottomRight.Y, vp.MinY, 9);
        Assert.Equal(topLeft.Y, vp.MaxY, 9);
    }

    [Fact]
    public void WorldViewport_Unfitted_ReturnsEmptyRect()
    {
        var vm = new CanvasViewModel(new[] { Line(0, 0, 100, 50) });
        var vp = vm.WorldViewport();
        Assert.True(vp.MinX > vp.MaxX || vp.MinY > vp.MaxY);

        vm.Fit((0, 0, 100, 50), 0, 0);
        vp = vm.WorldViewport();
        Assert.True(vp.MinX > vp.MaxX || vp.MinY > vp.MaxY);
    }

    [Fact]
    public void WorldViewport_ContainsAllVisibleItems_ForFitView()
    {
        var items = new[] { Line(0, 0, 100, 0), Circle(90, 40, 10), TextItem("x", 10, 10, 5) };
        var vm = new CanvasViewModel(items);
        vm.Fit(RenderModelBuilder.Bounds(items), 800, 600);

        var vp = vm.WorldViewport();
        var visible = vm.QueryViewport(vp);
        Assert.Equal(new[] { 0, 1, 2 }, visible);
    }

    // ---- local linear reference = old CanvasViewModel.HitTest + Hits 数学 ----

    private static int? LinearFirstHit(
        IReadOnlyList<RenderItem> items, double x, double y, double tolerance, IReadOnlySet<string>? hidden)
    {
        double tol = Math.Max(0, tolerance);
        for (int i = 0; i < items.Count; i++)
        {
            RenderItem item = items[i];
            if (hidden is not null && hidden.Contains(item.Layer)) continue;
            if (Hits(item, x, y, tol)) return i;
        }
        return null;
    }

    private static bool Hits(RenderItem item, double x, double y, double tol)
    {
        switch (item.Kind)
        {
            case RenderKind.Line:
                return item.Points.Count >= 2
                    && DistToSegment(x, y, item.Points[0], item.Points[1]) <= tol;

            case RenderKind.Circle:
            {
                if (item.Points.Count < 2) return false;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                return Math.Abs(d - r) <= tol;
            }

            case RenderKind.Arc:
            {
                if (item.Points.Count < 3) return false;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                double dx = x - cx, dy = y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(d - r) > tol) return false;
                double startDeg = item.Points[1].Y;
                double span = NormDeg(item.Points[2].X - startDeg);
                if (span < 1e-9) return true;
                double angle = NormDeg(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                double rel = NormDeg(angle - startDeg);
                double slackDeg = r > 1e-12 ? tol / r * 180.0 / Math.PI : 360;
                return rel <= span + slackDeg || rel >= 360 - slackDeg;
            }

            case RenderKind.Polyline:
            {
                if (item.Points.Count == 0) return false;
                if (item.Points.Count == 1) return Dist(x, y, item.Points[0]) <= tol;
                for (int s = 0; s + 1 < item.Points.Count; s++)
                    if (DistToSegment(x, y, item.Points[s], item.Points[s + 1]) <= tol)
                        return true;
                return false;
            }

            case RenderKind.Text:
            {
                if (item.Points.Count < 1) return false;
                (double ix, double iy) = item.Points[0];
                double w = (item.Text?.Length ?? 0) * item.TextHeight * TextWidthPerHeight;
                double h = item.TextHeight;
                return x >= ix - tol && x <= ix + w + tol && y >= iy - tol && y <= iy + h + tol;
            }

            default:
                return false;
        }
    }

    private static double Dist(double x, double y, (double X, double Y) p)
    {
        double dx = x - p.X, dy = y - p.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistToSegment(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        double t = len2 <= 1e-24 ? 0 : ((x - a.X) * abx + (y - a.Y) * aby) / len2;
        t = Math.Clamp(t, 0, 1);
        double px = a.X + t * abx, py = a.Y + t * aby;
        double dx = x - px, dy = y - py;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;

    // ---- fixtures ----

    private static IReadOnlyList<RenderItem> SmallMixedFixture() => new[]
    {
        Line(0, 0, 100, 0),
        Line(0, 0, 0, 100, layer: "L2"),
        Circle(50, 50, 20),
        Arc(30, 30, 15, 200, 340, layer: "L3"),
        Polyline("L1", "9", (0, 0), (10, 40), (20, 5), (40, 60)),
        TextItem("HELLO", 5, 5, 3),
        TextItem("W", 90, 90, 8, layer: "L2"),
        Hatch(new[] { 60.0, 80, 80, 60, double.NaN, 65, 70 }, new[] { 10.0, 10, 30, 30, double.NaN, 15, 15 }),
        Line(-40, 120, 140, 120, layer: "L3"),
    };

    private static IReadOnlyList<RenderItem> LargeMixedFixture(Random rnd, int count)
    {
        var items = new List<RenderItem>(count);
        for (int i = 0; i < count; i++)
        {
            string layer = "L" + (i % 6);
            double x = NextRange(rnd, -100, 400), y = NextRange(rnd, -100, 200);
            switch (i % 5)
            {
                case 0:
                    items.Add(Line(x, y, x + NextRange(rnd, -40, 40), y + NextRange(rnd, -40, 40), layer, i.ToString()));
                    break;
                case 1:
                    items.Add(Circle(x, y, NextRange(rnd, 0.5, 30), layer, i.ToString()));
                    break;
                case 2:
                {
                    double start = NextRange(rnd, 0, 360);
                    items.Add(Arc(x, y, NextRange(rnd, 1, 25), start, start + NextRange(rnd, 5, 355), layer, i.ToString()));
                    break;
                }
                case 3:
                {
                    int n = rnd.Next(2, 6);
                    var pts = new (double X, double Y)[n];
                    for (int k = 0; k < n; k++) pts[k] = (x + NextRange(rnd, -20, 20), y + NextRange(rnd, -20, 20));
                    items.Add(new RenderItem(RenderKind.Polyline, pts, layer, 0xFFFFFF, null, 0, 0, i.ToString()));
                    break;
                }
                default:
                {
                    int len = rnd.Next(1, 9);
                    items.Add(TextItem(new string('T', len), x, y, NextRange(rnd, 1, 6), layer, i.ToString()));
                    break;
                }
            }
        }
        return items;
    }

    private static IReadOnlyList<RenderItem> DenseFixture(Random rnd)
    {
        var items = new List<RenderItem>();
        for (int i = 0; i < 120; i++)
        {
            string layer = "D" + (i % 4);
            double x = NextRange(rnd, 0, 20), y = NextRange(rnd, 0, 20);
            switch (i % 4)
            {
                case 0: items.Add(Line(x, y, x + NextRange(rnd, -8, 8), y + NextRange(rnd, -8, 8), layer, i.ToString())); break;
                case 1: items.Add(Circle(x, y, NextRange(rnd, 0.2, 12), layer, i.ToString())); break;
                case 2: items.Add(Arc(x, y, NextRange(rnd, 0.2, 12), NextRange(rnd, 0, 360), NextRange(rnd, 0, 360), layer, i.ToString())); break;
                default: items.Add(TextItem(new string('A', rnd.Next(1, 6)), x, y, NextRange(rnd, 0.5, 3), layer, i.ToString())); break;
            }
        }
        items.Add(Line(-1000, 10, 1000, 10)); // overflow
        items.Add(TextItem("CENTER", 9, 9, 4, "D1"));
        return items;
    }

    private static (double X, double Y) PointNear(RenderItem item, Random rnd)
    {
        double jx = NextRange(rnd, -2, 2), jy = NextRange(rnd, -2, 2);
        switch (item.Kind)
        {
            case RenderKind.Circle:
            case RenderKind.Arc:
            {
                if (item.Points.Count < 2) break;
                double r = Math.Abs(item.Points[1].X);
                double a = rnd.NextDouble() * Math.PI * 2;
                return (item.Points[0].X + r * Math.Cos(a) + jx, item.Points[0].Y + r * Math.Sin(a) + jy);
            }
            case RenderKind.Text:
            {
                (double ix, double iy) = item.Points[0];
                double w = (item.Text?.Length ?? 0) * item.TextHeight * TextWidthPerHeight;
                return (ix + rnd.NextDouble() * w + jx, iy + rnd.NextDouble() * item.TextHeight + jy);
            }
            default:
            {
                for (int k = 0; k < item.Points.Count; k++)
                {
                    (double px, double py) = item.Points[rnd.Next(item.Points.Count)];
                    if (!double.IsNaN(px) && !double.IsNaN(py)) return (px + jx, py + jy);
                }
                break;
            }
        }
        return (NextRange(rnd, -40, 60), NextRange(rnd, -40, 60));
    }

    private static double NextRange(Random rnd, double min, double max) => min + rnd.NextDouble() * (max - min);
}
