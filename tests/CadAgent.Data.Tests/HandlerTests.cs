using System.Text.Json;
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
    public void Registry_ContainsAllFiveKinds()
    {
        var kinds = HandlerRegistry.All().Select(h => h.Kind).ToHashSet();
        Assert.Equal(new[] { "add-line", "add-text", "delete-entity", "move-entity", "change-layer" }.ToHashSet(), kinds);
    }
}