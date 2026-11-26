using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-b Task 9: insert-block handler (sixth step handler). References an
/// in-drawing BlockRecord to create an Insert entity. Geometric-level only —
/// NOT ISemanticAware (semantic declarations arrive with the plan-3 lighting
/// handler). Missing block → failure result (cross-document symbol import is
/// deferred to plan 3). ACadSharp 3.8.35 Insert API verified via package XML
/// docs + runtime probe: Insert(BlockRecord) is the only ctor; InsertPoint /
/// XScale / YScale / ZScale / Rotation are properties (Rotation in radians);
/// CadDocument.BlockRecords.TryGetValue is case-insensitive.
/// </summary>
public class InsertBlockTests : IDisposable
{
    private readonly string _path;
    private readonly DrawingSession _session;

    public InsertBlockTests()
    {
        _path = DxfFixture.CreateTempDxf(DxfFixture.AddLumBlock);
        _session = DrawingSession.Load(_path);
    }

    public void Dispose() => File.Delete(_path);

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void InsertBlock_ExistingBlock_AddsInsertAtPoint_DefaultScaleRotation()
    {
        var r = new InsertBlockHandler().Apply(_session, P("""{"block":"LUM","x":10,"y":20}"""));
        Assert.True(r.Ok);

        var insert = Assert.Single(_session.Doc.Entities.OfType<Insert>());
        Assert.Equal("LUM", insert.Block.Name);
        Assert.Equal(10, insert.InsertPoint.X, 3);
        Assert.Equal(20, insert.InsertPoint.Y, 3);
        Assert.Equal(0, insert.InsertPoint.Z, 3);
        // scale default 1, rotation default 0.
        Assert.Equal(1, insert.XScale, 3);
        Assert.Equal(1, insert.YScale, 3);
        Assert.Equal(1, insert.ZScale, 3);
        Assert.Equal(0, insert.Rotation, 3);
        Assert.Equal(insert.Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void InsertBlock_ScaleAndRotation_AppliedToAllAxes()
    {
        var r = new InsertBlockHandler().Apply(_session,
            P("""{"block":"LUM","x":1,"y":2,"scale":2.5,"rotation":1.5707963267948966}"""));
        Assert.True(r.Ok);

        var insert = Assert.Single(_session.Doc.Entities.OfType<Insert>());
        Assert.Equal(2.5, insert.XScale, 3);
        Assert.Equal(2.5, insert.YScale, 3);
        Assert.Equal(2.5, insert.ZScale, 3);
        Assert.Equal(Math.PI / 2, insert.Rotation, 6);
    }

    [Fact]
    public void InsertBlock_MissingBlock_FailsWithMessage_NoEntityAdded()
    {
        var r = new InsertBlockHandler().Apply(_session, P("""{"block":"NOPE","x":0,"y":0}"""));
        Assert.False(r.Ok);
        Assert.Equal("块不存在: NOPE（跨文档符号导入留计划3）", r.Error);
        Assert.Empty(r.TargetHandle);
        Assert.Empty(_session.Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void InsertBlock_BlockLookup_IsCaseInsensitive()
    {
        // CAD table names are case-insensitive (ACadSharp BlockRecords table);
        // "lum" must resolve the existing "LUM" record.
        var r = new InsertBlockHandler().Apply(_session, P("""{"block":"lum","x":0,"y":0}"""));
        Assert.True(r.Ok);
        Assert.Equal("LUM", Assert.Single(_session.Doc.Entities.OfType<Insert>()).Block.Name);
    }

    [Fact]
    public void InsertBlock_PersistsAcrossSaveReload()
    {
        var r = new InsertBlockHandler().Apply(_session, P("""{"block":"LUM","x":10,"y":20,"scale":2}"""));
        Assert.True(r.Ok);
        _session.SaveWorking();
        _session.Reload();

        var insert = Assert.Single(_session.Doc.Entities.OfType<Insert>());
        Assert.Equal("LUM", insert.Block.Name);
        Assert.Equal(10, insert.InsertPoint.X, 3);
        Assert.Equal(20, insert.InsertPoint.Y, 3);
        Assert.Equal(2, insert.XScale, 3);
    }

    // ---- 项2 Plan-2 T5：space:"paper"+layout → 布局纸面关联块（与 add-line 同一集合解析）----

    private static (DrawingSession S, ACadSharp.Objects.Layout Layout, string Path) NewPaper()
    {
        var (s, layout, path) = PaperFixtures.PaperSession(doc =>
        {
            var lum = new BlockRecord("LUM");
            lum.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(lum);
        });
        return (s, layout, path);
    }

    [Fact]
    public void InsertBlock_PaperSpace_WritesIntoLayoutBlock_ModelUntouched()
    {
        var (s, layout, path) = NewPaper();
        try
        {
            var r = new InsertBlockHandler().Apply(s, P($$"""
                {"block":"LUM","x":420,"y":297,"scale":2,
                 "space":"paper","layout":"{{PaperFixtures.LayoutName}}"}
                """));
            Assert.True(r.Ok, r.Error);
            var insert = Assert.Single(layout.AssociatedBlock!.Entities.OfType<Insert>());
            Assert.Equal("LUM", insert.Block.Name);
            Assert.Equal(420, insert.InsertPoint.X, 6);
            Assert.Equal(297, insert.InsertPoint.Y, 6);
            Assert.Equal(2, insert.XScale, 6);
            Assert.Equal(insert.Handle.ToString(), r.TargetHandle);
            Assert.Empty(s.Doc.Entities); // 模型空间未写入（空间隔离）

            // 落盘往返（与模型用例同口径）：纸面 Insert 存续，模型空间仍空。
            s.SaveWorking();
            s.Reload();
            var reloaded = s.Doc.Layouts.First(l => l.Name == PaperFixtures.LayoutName);
            var persisted = Assert.Single(reloaded.AssociatedBlock!.Entities.OfType<Insert>());
            Assert.Equal("LUM", persisted.Block.Name);
            Assert.Equal(420, persisted.InsertPoint.X, 6);
            Assert.Equal(297, persisted.InsertPoint.Y, 6);
            Assert.Equal(2, persisted.XScale, 6);
            Assert.Empty(s.Doc.Entities.OfType<Insert>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InsertBlock_UnsupportedSpace_Fails_NoEntityAdded()
    {
        var (s, layout, path) = NewPaper();
        try
        {
            var r = new InsertBlockHandler().Apply(s, P($$"""
                {"block":"LUM","x":1,"y":2,"space":"foo","layout":"{{PaperFixtures.LayoutName}}"}
                """));
            Assert.False(r.Ok);
            Assert.Contains("仅支持 paper", r.Error);
            Assert.Empty(s.Doc.Entities.OfType<Insert>());
            Assert.Empty(layout.AssociatedBlock!.Entities.OfType<Insert>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InsertBlock_IsNotSemanticAware()
    {
        // 几何级处理器：不声明语义需求（语义闸口需求随计划3布灯处理器落地）。
        // 反射判定替代 is 模式匹配——sealed 类未实现接口时 is 恒 false 触发 CS0184
        // 编译警告（死分支）；IsAssignableFrom 保持同一断言意图且无警告。
        Assert.False(typeof(ISemanticAware).IsAssignableFrom(typeof(InsertBlockHandler)));
    }
}
