using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class TsCodecTests
{
    [Fact]
    public void Escape_ThenUnescape_RoundTripsSpecialChars()
    {
        string raw = "E:\\图纸\\16#1116.dwg\t线\n名\\";
        Assert.Equal(raw, TsCodec.Unescape(TsCodec.Escape(raw)));
    }

    [Fact]
    public void SplitLine_EscapedTab_IsNotDelimiter()
    {
        Assert.Equal(new[] { "a", "b\tc", "d" }, TsCodec.SplitLine("a\tb\\tc\td"));
    }

    [Fact]
    public void Request_RoundTrip_ChinesePathAndNulls()
    {
        var req = new BridgeRequest("explode-replace", "E:\\图纸\\16#1116.dwg", "21E00", 4, "E:\\out\\x.live.dwg");
        var parsed = BridgeRequest.Parse(req.Write());
        Assert.Equal(req.Op, parsed.Op);
        Assert.Equal(req.Dwg, parsed.Dwg);
        Assert.Equal("21E00", parsed.Handle);
        Assert.Equal(4, parsed.MaxDepth);
        Assert.Equal(req.LiveOut, parsed.LiveOut);
    }

    [Fact]
    public void Request_MissingOp_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => BridgeRequest.Parse("dwg\tx.dwg\n"));
        Assert.Contains("op", ex.Message);
    }

    [Fact]
    public void Result_RoundTrip_AllRecordKinds()
    {
        var r = new BridgeResult
        {
            Status = "ok",
            Evidence = { new EvidenceDto("21E00", "TDbWall", "ImpCurve", "0dt",
                new[] { 1.0, 2, 0, 3, 4, 3000 }, true) },
            Prims = { new PrimDto("21E00", 0, "AcDbLine", new[] { "start=1,2,0 end=3,4,0" }) },
            XData = { new XDataDto("3EB5D", 1000, "HYDRANT_RADIUS") },
            Blocks = { new BlockDto("1A2B", "$TWTSYS$00000508", new[] { 5.0, 6, 0 }) },
            Fails = { new FailDto("321A", "eInvalidInput") },
            Replace = new ReplaceDto("21E00", 4, "E:\\out\\x.live.dwg", true, 1e-3),
        };
        var parsed = BridgeResult.Parse(r.Write());
        Assert.Equal("ok", parsed.Status);
        Assert.Single(parsed.Evidence);
        Assert.Equal(3000, parsed.Evidence[0].Bbox![5]);
        Assert.True(parsed.Evidence[0].FullyExplodable);
        Assert.Equal("AcDbLine", parsed.Prims[0].RxClass);
        Assert.Equal("HYDRANT_RADIUS", parsed.XData[0].Value);
        Assert.Equal("$TWTSYS$00000508", parsed.Blocks[0].Name);
        Assert.Equal("eInvalidInput", parsed.Fails[0].Reason);
        Assert.True(parsed.Replace!.BboxMatch);
        Assert.Equal(1e-3, parsed.Replace.Tolerance);
    }

    [Fact]
    public void Result_StatusFail_CarriesError()
    {
        var r = BridgeResult.Parse("status\tfail\nerror\tSystem.Exception: boom\n");
        Assert.Equal("fail", r.Status);
        Assert.Equal("System.Exception: boom", r.Error);
    }

    [Fact]
    public void Result_UnknownRecord_Throws()
    {
        Assert.Throws<FormatException>(() => BridgeResult.Parse("status\tok\nWAT\tx\n"));
    }
}
