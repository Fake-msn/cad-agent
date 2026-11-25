using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class ReplaceResultShapeTests
{
    [Fact]
    public void Replace_And_Verify_ParseIntoReplaceDto()
    {
        var parsed = BridgeResult.Parse(
            "status\tok\nREPLACED\t21E00\t4\tE:\\out\\x.live.dwg\nVERIFY\t21E00\t1\t0.001\n");
        Assert.NotNull(parsed.Replace);
        Assert.Equal(4, parsed.Replace!.NewCount);
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
