using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>B1（边界补齐）：块定义 TDb 记录（<c>BDEF</c>，4 列）——与 ENTITY（模型空间实例）**分离**的独立段。
/// 逐项钉住：往返保真、名称转义（TAB/反斜杠不得撕裂列）、列数非法响亮拒绝。</summary>
public class BlockDefRecordTests
{
    [Fact]
    public void BlockDef_RoundTrip_PreservesAllColumns()
    {
        var r = new BridgeResult { Status = "ok" };
        r.BlockDefs.Add(new BlockDefDto("$equip$00002832", "TDbECabletry", 1364));
        r.BlockDefs.Add(new BlockDefDto("门块A", "TDbEFitting", 2));

        var parsed = BridgeResult.Parse(r.Write());

        Assert.Equal(2, parsed.BlockDefs.Count);
        Assert.Equal("$equip$00002832", parsed.BlockDefs[0].BlockName);
        Assert.Equal("TDbECabletry", parsed.BlockDefs[0].RxClass);
        Assert.Equal(1364, parsed.BlockDefs[0].Count);
        Assert.Equal("门块A", parsed.BlockDefs[1].BlockName);
        Assert.Equal("TDbEFitting", parsed.BlockDefs[1].RxClass);
        Assert.Equal(2, parsed.BlockDefs[1].Count);
        // 块定义行不冒充实体证据（分离段的核心理由：定义计数 ≠ 实例计数）。
        Assert.Empty(parsed.Evidence);
    }

    [Fact]
    public void BlockDef_EscapedNames_RoundTripWithoutColumnSplit()
    {
        var r = new BridgeResult { Status = "ok" };
        r.BlockDefs.Add(new BlockDefDto("块\t名\\带转义", "TDbECabletry", 1));

        string text = r.Write();
        var parsed = BridgeResult.Parse(text);

        Assert.Contains("BDEF\t", text);
        var b = Assert.Single(parsed.BlockDefs);
        Assert.Equal("块\t名\\带转义", b.BlockName);
        Assert.Equal("TDbECabletry", b.RxClass);
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void BlockDef_WrongColumnCount_Throws()
    {
        Assert.Throws<FormatException>(() => BridgeResult.Parse("status\tok\nBDEF\t块A\tTDbECabletry\n"));
        Assert.Throws<FormatException>(() => BridgeResult.Parse("status\tok\nBDEF\t块A\tTDbECabletry\t1\textra\n"));
    }

    [Fact]
    public void BlockDef_NegativeOrNonNumericCount_Throws()
    {
        Assert.Throws<FormatException>(() => BridgeResult.Parse("status\tok\nBDEF\t块A\tTDbECabletry\tx\n"));
        // PR32：定义内实体计数语义上不可能为负——负值一律按协议非法拒绝（不静默接受）。
        Assert.Throws<FormatException>(() => BridgeResult.Parse("status\tok\nBDEF\t块A\tTDbECabletry\t-1\n"));
    }
}
