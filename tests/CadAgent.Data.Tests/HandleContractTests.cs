using System.Text.Json;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

/// <summary>
/// Plan 3a T4: handle contract — Find accepts JSON number OR string handles
/// (Summarize exposes decimal strings; LLM plan authoring can feed them back directly).
/// Invalid string → null → graceful "句柄不存在" failure result.
/// </summary>
public class HandleContractTests : IDisposable
{
    private readonly string _path;
    public HandleContractTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Delete_StringHandle_Works()
    {
        var s = DrawingSession.Load(_path);
        string h = s.Summarize().Entities[0].Handle;
        var r = new DeleteEntityHandler().Apply(s, JsonDocument.Parse($"{{\"handle\":\"{h}\"}}").RootElement);
        Assert.True(r.Ok);
        Assert.Empty(s.Summarize().Entities);
    }

    [Fact]
    public void Delete_NumericHandle_StillWorks()
    {
        var s = DrawingSession.Load(_path);
        string h = s.Summarize().Entities[0].Handle;
        var r = new DeleteEntityHandler().Apply(s, JsonDocument.Parse($"{{\"handle\":{h}}}").RootElement);
        Assert.True(r.Ok);
    }

    [Fact]
    public void Delete_InvalidStringHandle_FailsGracefully()
    {
        var s = DrawingSession.Load(_path);
        var r = new DeleteEntityHandler().Apply(s, JsonDocument.Parse("""{"handle":"not-a-number"}""").RootElement);
        Assert.False(r.Ok);
        Assert.Contains("句柄不存在", r.Error);
    }

    [Fact]
    public void Move_StringHandle_Works()
    {
        var s = DrawingSession.Load(_path);
        string h = s.Summarize().Entities[0].Handle;
        var r = new MoveEntityHandler().Apply(s, JsonDocument.Parse($"{{\"handle\":\"{h}\",\"dx\":1,\"dy\":2}}").RootElement);
        Assert.True(r.Ok);
    }
}