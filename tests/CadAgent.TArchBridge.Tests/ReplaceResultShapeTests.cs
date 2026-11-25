using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class ReplaceResultShapeTests
{
    [Fact]
    public void Replace_And_Verify_ParseIntoReplaceDto()
    {
        // TSV 里的反斜杠须按 TsCodec.Escape 规则双写（"\\\\"），否则 Unescape 会把 "\o"/"\x" 吞成 "o"/"x"。
        // 故 C# 字面量写成 "E:\\\\out\\\\x.live.dwg"，往返后 SavedPath 为 E:\out\x.live.dwg。
        var parsed = BridgeResult.Parse(
            "status\tok\nREPLACED\t21E00\t4\tE:\\\\out\\\\x.live.dwg\nVERIFY\t21E00\t1\t0.001\n");
        Assert.NotNull(parsed.Replace);
        Assert.Equal(4, parsed.Replace!.NewCount);
        Assert.Equal(@"E:\out\x.live.dwg", parsed.Replace.SavedPath);
        Assert.True(parsed.Replace.BboxMatch);
        Assert.Equal(0.001, parsed.Replace.Tolerance);
    }

    [Fact]
    public void Replace_FailureShape_StatusFail()
    {
        var parsed = BridgeResult.Parse("status\tfail\nerror\tInvalidOperationException: bbox 不匹配\n");
        Assert.Null(parsed.Replace);
        Assert.Contains("bbox", parsed.Error);
    }
}
