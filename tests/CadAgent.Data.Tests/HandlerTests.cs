using System.Text.Json;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Handlers;

namespace CadAgent.Data.Tests;

public class HandlerTests : IDisposable
{
    private readonly string _path;
    private readonly DrawingSession _session;
    public HandlerTests()
    {
        _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _session = DrawingSession.Load(_path);
    }
    public void Dispose() => File.Delete(_path);

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;
    private ulong FirstHandle => ulong.Parse(_session.Summarize().Entities[0].Handle);

    [Fact]
    public void AddLine_AddsEntity()
    {
        var r = new AddLineHandler().Apply(_session, P("""{"x1":0,"y1":0,"x2":5,"y2":5}"""));
        Assert.True(r.Ok);
        Assert.Equal(2, _session.Summarize().Entities.Count);
    }

    [Fact]
    public void AddText_AddsEntityWithContent()
    {
        var r = new AddTextHandler().Apply(_session, P("""{"x":1,"y":2,"text":"测试","height":3.5}"""));
        Assert.True(r.Ok);
        Assert.Contains(_session.Summarize().Entities, e => e.Kind == "TextEntity");
    }

    [Fact]
    public void DeleteEntity_RemovesByHandle()
    {
        var r = new DeleteEntityHandler().Apply(_session, P($"{{\"handle\":{FirstHandle}}}"));
        Assert.True(r.Ok);
        Assert.Empty(_session.Summarize().Entities);
    }

    [Fact]
    public void DeleteEntity_UnknownHandle_Fails()
    {
        var r = new DeleteEntityHandler().Apply(_session, P("""{"handle":999999}"""));
        Assert.False(r.Ok);
    }

    [Fact]
    public void MoveEntity_ShiftsLine()
    {
        var r = new MoveEntityHandler().Apply(_session, P($"{{\"handle\":{FirstHandle},\"dx\":10,\"dy\":20}}"));
        Assert.True(r.Ok);
        var line = (ACadSharp.Entities.Line)_session.Doc.Entities.First();
        Assert.Equal(10, line.StartPoint.X, 3);
        Assert.Equal(20, line.StartPoint.Y, 3);
    }

    [Fact]
    public void ChangeLayer_CreatesLayerAndAssigns()
    {
        var r = new ChangeLayerHandler().Apply(_session, P($"{{\"handle\":{FirstHandle},\"layer\":\"E-LITE\"}}"));
        Assert.True(r.Ok);
        Assert.Contains("E-LITE", _session.Summarize().Layers);
        Assert.Equal("E-LITE", _session.Summarize().Entities[0].Layer);
    }

    [Fact]
    public void MoveEntity_PreservesLineZ()
    {
        // Real drawings carry elevations; move must not flatten Z to 0.
        var line = (ACadSharp.Entities.Line)_session.Doc.Entities.First();
        line.StartPoint = new CSMath.XYZ(0, 0, 5);
        line.EndPoint = new CSMath.XYZ(100, 100, 5);
        var r = new MoveEntityHandler().Apply(_session, P($"{{\"handle\":{FirstHandle},\"dx\":10,\"dy\":20}}"));
        Assert.True(r.Ok);
        Assert.Equal(10, line.StartPoint.X, 3);
        Assert.Equal(20, line.StartPoint.Y, 3);
        Assert.Equal(5, line.StartPoint.Z, 3);
        Assert.Equal(5, line.EndPoint.Z, 3);
    }

    [Fact]
    public void MoveEntity_PreservesTextZ()
    {
        var add = new AddTextHandler().Apply(_session, P("""{"x":1,"y":2,"text":"标高","height":3.5}"""));
        Assert.True(add.Ok);
        var text = (ACadSharp.Entities.TextEntity)_session.Doc.Entities.First(e => e is ACadSharp.Entities.TextEntity);
        text.InsertPoint = new CSMath.XYZ(1, 2, 5);
        var r = new MoveEntityHandler().Apply(_session, P($"{{\"handle\":{text.Handle},\"dx\":3,\"dy\":4}}"));
        Assert.True(r.Ok);
        Assert.Equal(4, text.InsertPoint.X, 3);
        Assert.Equal(6, text.InsertPoint.Y, 3);
        Assert.Equal(5, text.InsertPoint.Z, 3);
    }

    [Fact]
    public void ChangeLayer_NameMatchIsCaseInsensitive_NoDuplicateLayer()
    {
        // CAD layer names are case-insensitive: "e-lite" must resolve to existing "E-LITE".
        var r1 = new ChangeLayerHandler().Apply(_session, P($"{{\"handle\":{FirstHandle},\"layer\":\"E-LITE\"}}"));
        Assert.True(r1.Ok);
        var add = new AddLineHandler().Apply(_session, P("""{"x1":0,"y1":0,"x2":1,"y2":1}"""));
        Assert.True(add.Ok);
        var r2 = new ChangeLayerHandler().Apply(_session, P($"{{\"handle\":{add.TargetHandle},\"layer\":\"e-lite\"}}"));
        Assert.True(r2.Ok);
        Assert.Equal(1, _session.Doc.Layers.Count(l => string.Equals(l.Name, "e-lite", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("E-LITE", _session.Summarize().Entities.First(e => e.Handle == add.TargetHandle).Layer);
    }

    [Fact]
    public void Registry_ContainsAllNineKinds()
    {
        var kinds = HandlerRegistry.All().Select(h => h.Kind).ToHashSet();
        Assert.Equal(new[] { "add-line", "add-text", "add-circle", "delete-entity", "move-entity", "change-layer", "insert-block", "layout-lighting", "label-luminaires" }.ToHashSet(), kinds);
    }

    [Fact]
    public void AddCircle_CreatesCircle_RejectsNonPositiveRadius()
    {
        var add = new AddCircleHandler().Apply(_session, P("""{"x":5,"y":6,"radius":2.5}"""));
        Assert.True(add.Ok);
        var c = Assert.Single(_session.Doc.Entities.OfType<Circle>());
        Assert.Equal(2.5, c.Radius);
        Assert.Equal(5, c.Center.X);
        Assert.False(new AddCircleHandler().Apply(_session, P("""{"x":0,"y":0,"radius":0}""")).Ok);
    }
}