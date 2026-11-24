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
    [InlineData("", "")]                                        // 空串：解析失败→大写原样（恒等）
    [InlineData("ZZZ", "ZZZ")]                                   // 非数值：大写原样
    [InlineData("18446744073709551616", "18446744073709551616")] // ulong 上界+1 溢出：两侧解析均失败→恒等
    public void HandleToHex_Normalizes(string input, string expected)
        => Assert.Equal(expected, ComReplayService.HandleToHex(input));

    // ISTQB Minor 4：HandleString 数值形态边界——Number 走 GetUInt64（非字符串通道）。

    [Fact]
    public void HandleString_NumberForm_ReturnsDecimalString()
    {
        using var doc = JsonDocument.Parse("""{"handle":138752}""");
        Assert.Equal("138752", ComReplayService.HandleString(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"handle":-5}""")]
    [InlineData("""{"handle":1.5}""")]
    public void HandleString_NonUInt64Number_FailsClosed(string json)
    {
        // 负数/小数不属于合法句柄形态：GetUInt64 抛 FormatException——fail-closed 链的
        // 实际异常类型钉住（不得静默成 "" 而把非法句柄当合法空句柄）。
        using var doc = JsonDocument.Parse(json);
        var ex = Assert.ThrowsAny<Exception>(() => ComReplayService.HandleString(doc.RootElement));
        Assert.IsType<FormatException>(ex);
    }
}
