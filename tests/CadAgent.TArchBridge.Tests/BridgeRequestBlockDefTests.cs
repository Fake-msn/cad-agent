using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// A3（技术债批次A）：请求 <c>bdef</c> 可选键——块定义段（BDEF）按需读取。
/// 契约：<c>bdef\t1</c> → IncludeBlockDefs=true；缺 key → false；**其它任何值**（含 "0"）→
/// <see cref="FormatException"/>（不静默当 false：协议不接受"半开"取值）；
/// Write 只在 true 时输出该行（false 请求逐字不变，旧桥/旧日志面不受扰动）。
/// </summary>
public class BridgeRequestBlockDefTests
{
    [Fact]
    public void Parse_BdefOne_SetsIncludeBlockDefs()
    {
        var req = BridgeRequest.Parse("op\tread\ndwg\ta.dwg\nbdef\t1\n");

        Assert.True(req.IncludeBlockDefs);
    }

    [Fact]
    public void Parse_WithoutBdef_DefaultsFalse()
        => Assert.False(BridgeRequest.Parse("op\tread\ndwg\ta.dwg\n").IncludeBlockDefs);

    [Theory]
    [InlineData("bdef\t0")]
    [InlineData("bdef\tx")]
    [InlineData("bdef\ttrue")]
    [InlineData("bdef\t")]
    public void Parse_BdefOtherValues_Throw(string line)
    {
        var ex = Assert.Throws<FormatException>(() =>
            BridgeRequest.Parse("op\tread\ndwg\ta.dwg\n" + line + "\n"));
        Assert.Contains("bdef", ex.Message);
    }

    [Fact]
    public void Write_EmitsBdefOnlyWhenTrue()
    {
        var on = new BridgeRequest("read", "a.dwg", null, 4, null) { IncludeBlockDefs = true };
        var off = new BridgeRequest("read", "a.dwg", null, 4, null);

        Assert.Contains("bdef\t1\n", on.Write());
        Assert.DoesNotContain("bdef", off.Write());
    }

    [Fact]
    public void RoundTrip_TrueAndFalse_Preserved()
    {
        var on = new BridgeRequest("read", "a.dwg", null, 4, null) { IncludeBlockDefs = true };
        var off = new BridgeRequest("read", "a.dwg", null, 4, null);

        var parsedOn = BridgeRequest.Parse(on.Write());
        var parsedOff = BridgeRequest.Parse(off.Write());

        Assert.True(parsedOn.IncludeBlockDefs);
        Assert.False(parsedOff.IncludeBlockDefs);
        // 其余字段不受新键影响。
        Assert.Equal("read", parsedOn.Op);
        Assert.Equal("a.dwg", parsedOn.Dwg);
        Assert.Equal(4, parsedOn.MaxDepth);
    }

    // 未知键规则：bdef 现已入册——含 bdef 的请求不得再被判为"未知请求键"。
    [Fact]
    public void Bdef_IsNoLongerUnknownKey()
    {
        var ex = Record.Exception(() => BridgeRequest.Parse("op\tread\ndwg\ta.dwg\nbdef\t1\n"));
        Assert.Null(ex);
    }
}
