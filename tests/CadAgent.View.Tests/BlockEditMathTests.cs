using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan-3 Task 3：块局部增量逆解（Insert 链复合仿射）。夹具一律走真实
/// DxfWriter/DxfReader 往返（DxfFixture 口径），句柄在落盘后由写入器分配。
/// 期望值一律经正向仿射 <see cref="Affine.InsertAffine"/> 推导（不写死手算），
/// 仅纯轴缩放用例断言 (10,6)→(5,2) 的解析解。
/// </summary>
public class BlockEditMathTests
{
    private const double Tol = 1e-9;

    private static void WithSession(Action<CadDocument> populate, Action<DrawingSession> test)
    {
        string path = DxfFixture.CreateTempDxf(populate);
        try { test(DrawingSession.Load(path)); }
        finally { File.Delete(path); }
    }

    private static string HandleOf(Entity e) => e.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>强制句柄（ACadSharp Handle setter 为 internal；脏图夹具专用）。</summary>
    private static void SetHandle(Entity e, ulong handle) =>
        typeof(ACadSharp.CadObject).GetProperty(nameof(ACadSharp.CadObject.Handle))!
            .GetSetMethod(nonPublic: true)!.Invoke(e, new object[] { handle });

    /// <summary>增量（向量）只经线性部映射：插入点平移不参与（平移只影响点坐标）。</summary>
    private static (double X, double Y) ForwardDelta(Affine t, double x, double y) =>
        (t.A * x + t.B * y, t.C * x + t.D * y);

    [Fact]
    public void WorldDeltaToBlockDelta_AxisScaledInsert_DividesByScale()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("B1");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(0, 0, 0), Rotation = 0, XScale = 2, YScale = 3, ZScale = 1
            });
        }, s =>
        {
            RenderItem child = Assert.Single(RenderModelBuilder.Build(s), i => i.BlockName is not null);
            IReadOnlyList<string> chain = child.ParentInsertChain!;
            Assert.Single(chain);
            Assert.True(ulong.Parse(chain[0]) > 0, "句柄应由 DxfWriter 落盘后分配"); // 句柄非零

            var (ok, lx, ly, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, chain, 10, 6);
            Assert.True(ok, err);
            Assert.Equal(5, lx, Tol); // 10 / XScale(2)
            Assert.Equal(2, ly, Tol); // 6 / YScale(3)
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_RotatedScaledInsert_RoundtripsForwardMappedDelta()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("B2");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(12, -7, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 3, ZScale = 1
            });
        }, s =>
        {
            RenderItem child = Assert.Single(RenderModelBuilder.Build(s), i => i.BlockName is not null);
            IReadOnlyList<string> chain = child.ParentInsertChain!;
            Insert ins = s.Doc.Entities.OfType<Insert>().Single();
            Affine t = Affine.InsertAffine(ins);
            (double wx, double wy) = ForwardDelta(t, 1.0, 0.5); // 正向推导期望世界增量

            var (ok, lx, ly, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, chain, wx, wy);
            Assert.True(ok, err);
            Assert.Equal(1.0, lx, Tol);
            Assert.Equal(0.5, ly, Tol);
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_NestedInserts_RoundtripsThroughComposedChain()
    {
        WithSession(doc =>
        {
            var inner = new BlockRecord("INNER");
            inner.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(inner);

            var outer = new BlockRecord("OUTER");
            outer.Entities.Add(new Insert(inner)
            {
                InsertPoint = new XYZ(3, 4, 0), Rotation = Math.PI / 6, XScale = 1.5, YScale = 2, ZScale = 1
            });
            doc.BlockRecords.Add(outer);

            doc.Entities.Add(new Insert(outer)
            {
                InsertPoint = new XYZ(5, 7, 0), Rotation = Math.PI / 3, XScale = 2, YScale = 3, ZScale = 1
            });
        }, s =>
        {
            RenderItem child = Assert.Single(RenderModelBuilder.Build(s), i => i.BlockName == "INNER");
            IReadOnlyList<string> chain = child.ParentInsertChain!;
            Assert.Equal(2, chain.Count);

            Insert outerIns = s.Doc.Entities.OfType<Insert>().Single();
            Insert innerIns = outerIns.Block.Entities.OfType<Insert>().Single();
            Assert.Equal(HandleOf(outerIns), chain[0]);
            Assert.Equal(HandleOf(innerIns), chain[1]);
            // 外层→内层同序复合（与渲染器一致）
            Affine t = Affine.InsertAffine(outerIns).Compose(Affine.InsertAffine(innerIns));
            (double wx, double wy) = ForwardDelta(t, 2.0, -1.0);

            var (ok, lx, ly, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, chain, wx, wy);
            Assert.True(ok, err);
            Assert.Equal(2.0, lx, Tol);
            Assert.Equal(-1.0, ly, Tol);
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_PaperLayoutInsert_UsesPaperCoordinates()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("PTITLE");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);

            var layout = new ACadSharp.Objects.Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0), Width = 1000, Height = 700,
                ViewTarget = new XYZ(0, 0, 0), ViewHeight = 200
            });
            layout.AssociatedBlock!.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 3, ZScale = 1
            });
        }, s =>
        {
            RenderItem child = Assert.Single(
                RenderModelBuilder.BuildPaperSpace(s.Doc, "布局1"), i => i.BlockName is not null);
            Assert.Equal(RenderSpace.PaperSpace, child.Space);
            IReadOnlyList<string> chain = child.ParentInsertChain!;

            var layout = s.Doc.Layouts.First(l => l.Name == "布局1");
            Insert ins = layout.AssociatedBlock!.Entities.OfType<Insert>().Single();
            Affine t = Affine.InsertAffine(ins);
            (double wx, double wy) = ForwardDelta(t, 1.5, -2.0); // 纸面坐标世界增量

            var (ok, lx, ly, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, chain, wx, wy);
            Assert.True(ok, err);
            Assert.Equal(1.5, lx, Tol);
            Assert.Equal(-2.0, ly, Tol);
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_MirroredNormalInsert_MatchesRenderedOcsGeometry()
    {
        // 镜像法向（Normal=(0,0,-1)）的 Insert 复合含 OCS 映射（OcsAffine）——逆解必须与
        // 渲染几何同口径。期望世界增量由渲染出的块基向量实测（不调用内部 OcsAffine）：
        // 块内两条线 (0,0)→(1,0) / (0,0)→(0,1) 的渲染端点差即 t 的两列。
        WithSession(doc =>
        {
            var block = new BlockRecord("MIRB");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(0, 1, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(4, -9, 0), Rotation = Math.PI / 4,
                XScale = 2, YScale = 3, ZScale = 1, Normal = new XYZ(0, 0, -1)
            });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s).Where(i => i.BlockName == "MIRB").ToList();
            Assert.Equal(2, items.Count);
            RenderItem xLine = items.Single(i => i.Handle.EndsWith(":0", StringComparison.Ordinal));
            RenderItem yLine = items.Single(i => i.Handle.EndsWith(":1", StringComparison.Ordinal));
            double dxX = xLine.Points[1].X - xLine.Points[0].X, dxY = xLine.Points[1].Y - xLine.Points[0].Y;
            double dyX = yLine.Points[1].X - yLine.Points[0].X, dyY = yLine.Points[1].Y - yLine.Points[0].Y;

            const double lx = 0.75, ly = -1.5;
            var (ok, bx, by, err) = BlockEditMath.WorldDeltaToBlockDelta(
                s.Doc, xLine.ParentInsertChain!, lx * dxX + ly * dyX, lx * dxY + ly * dyY);
            Assert.True(ok, err);
            Assert.Equal(lx, bx, Tol);
            Assert.Equal(ly, by, Tol);
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_RootHandleSharedByModelAndPaper_ReturnsAmbiguousError()
    {
        // 脏图：合法 DXF 句柄全文档唯一，同句柄跨空间 = 无法判定宿主空间 → fail-closed。
        // Handle setter 为 internal，测试用反射强制同一句柄（模型 Insert 与纸面 Insert）。
        WithSession(doc =>
        {
            var block = new BlockRecord("AMB");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);

            doc.Entities.Add(new Insert(block) { XScale = 2, YScale = 3, ZScale = 1 });

            var layout = new ACadSharp.Objects.Layout("布局2");
            doc.Layouts.Add(layout);
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0), Width = 1000, Height = 700,
                ViewTarget = new XYZ(0, 0, 0), ViewHeight = 200
            });
            layout.AssociatedBlock!.Entities.Add(new Insert(block) { XScale = 5, YScale = 7, ZScale = 1 });
        }, s =>
        {
            Insert modelIns = s.Doc.Entities.OfType<Insert>().Single();
            Insert paperIns = s.Doc.Layouts.First(l => l.Name == "布局2")
                .AssociatedBlock!.Entities.OfType<Insert>().Single();
            SetHandle(paperIns, modelIns.Handle);

            var (ok, dx, dy, err) = BlockEditMath.WorldDeltaToBlockDelta(
                s.Doc, new[] { HandleOf(modelIns) }, 10, 6);
            Assert.False(ok);
            Assert.Equal(0, dx);
            Assert.Equal(0, dy);
            Assert.Equal($"插入链解析失败: {HandleOf(modelIns)}（多空间命中）", err);
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_EmptyOrNullChain_ReturnsIdentitySuccess()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("B5");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { XScale = 2, YScale = 2, ZScale = 1 });
        }, s =>
        {
            foreach (IReadOnlyList<string>? chain in new IReadOnlyList<string>?[]
                     { Array.Empty<string>(), null })
            {
                var (ok, lx, ly, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, chain!, 7, -3);
                Assert.True(ok, err);
                Assert.Equal(7, lx, Tol);
                Assert.Equal(-3, ly, Tol);
            }
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_NullDocument_ReturnsArgumentError()
    {
        var (ok, dx, dy, err) = BlockEditMath.WorldDeltaToBlockDelta(null!, new[] { "1A" }, 1, 1);
        Assert.False(ok);
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
        Assert.Equal("图纸为空", err);
    }

    [Fact]
    public void WorldDeltaToBlockDelta_MissingChainHandle_ReturnsSpecificError()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("B3");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { XScale = 1, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var (ok, dx, dy, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, new[] { "FFFF" }, 1, 1);
            Assert.False(ok);
            Assert.Equal(0, dx);
            Assert.Equal(0, dy);
            Assert.Equal("插入链解析失败: FFFF", err);
        });
    }

    [Fact]
    public void WorldDeltaToBlockDelta_NearZeroScale_ReturnsIrreversibleError()
    {
        // 零缩放 Insert 不可构造（ACadSharp 拒绝 XScale=0），用近零缩放 1e-13：
        // det=1e-26 < 1e-12 阈值 → TryInvertLinear 拒绝，落在"不可逆"文案。
        WithSession(doc =>
        {
            var block = new BlockRecord("B4");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { XScale = 1e-13, YScale = 1e-13, ZScale = 1 });
        }, s =>
        {
            string h = HandleOf(s.Doc.Entities.OfType<Insert>().Single());
            var (ok, _, _, err) = BlockEditMath.WorldDeltaToBlockDelta(s.Doc, new[] { h }, 1, 1);
            Assert.False(ok);
            Assert.Equal("块插入变换不可逆（零缩放）", err);
        });
    }
}
