using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Cad;
using Xunit;

namespace CadAgent.Data.Tests;

/// <summary>
/// 切片A Task 8：ComReplayService 编辑类 kind（change-layer/move/delete）句柄寻址映射。
/// RecordingRuntime 记录调用；HandleToHex 归一（十进制串/已 hex 串/0x 前缀）由 theory 钉住。
/// </summary>
public class ComReplayHandleKindTests
{
    public sealed class RecordingRuntime : ICadRuntime
    {
        public readonly List<string> Ops = new();
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void ChangeLayerByHandle(string h, string layer) => Ops.Add($"change-layer:{h}:{layer}");
        public void MoveByHandle(string h, double dx, double dy) => Ops.Add($"move:{h}:{dx}:{dy}");
        public void DeleteByHandle(string h) => Ops.Add($"delete:{h}");
        public void Dispose() { }
    }

    private static StepPlan Plan((string Kind, string Json) step) => new("p1",
        new[] { new StepSpec(step.Kind, JsonDocument.Parse(step.Json).RootElement.Clone()) });

    [Fact]
    public void Replay_ChangeLayer_InvokesHandleOp_WithHexNormalized()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("change-layer", "{\"handle\":\"138752\",\"layer\":\"WALL\"}")), null!);
        Assert.True(r[0].Ok);
        Assert.Equal("change-layer:21E00:WALL", Assert.Single(rt.Ops));
    }

    [Fact]
    public void Replay_Move_InvokesHandleOp()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("move", "{\"handle\":\"21E00\",\"dx\":10,\"dy\":5}")), null!);
        Assert.True(r[0].Ok);
        Assert.Contains("21E00", Assert.Single(rt.Ops));
    }

    [Fact]
    public void Replay_Delete_InvokesHandleOp()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("delete", "{\"handle\":\"21E00\"}")), null!);
        Assert.True(r[0].Ok);
        Assert.Equal("delete:21E00", Assert.Single(rt.Ops));
    }

    [Fact]
    public void Replay_UnknownKind_FailsAndBreaks()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("bogus", "{\"handle\":\"21E00\"}")), null!);
        Assert.False(r[0].Ok);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("138752", "21E00")]
    [InlineData("21e00", "21E00")]
    [InlineData("0x21E00", "21E00")]
    public void HandleToHex_Normalizes(string input, string expected)
        => Assert.Equal(expected, ComReplayService.HandleToHex(input));
}
