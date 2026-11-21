using System.Text.Json;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Handlers;

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

    [Fact]
    public void InsertBlock_IsNotSemanticAware()
    {
        // Geometric-level handler: semantic requirements arrive with plan-3 lighting.
        Assert.False(new InsertBlockHandler() is ISemanticAware);
    }
}
