using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// A1（技术债批次A·协议下沉）：块定义行聚合 <see cref="BlockDefAggregator.Aggregate"/>。
/// 契约：跳过 IsLayout 行（布局块不是块定义）；按 (块名, 类) 聚合计数；
/// 输出按块名 → 类名 **Ordinal** 定序（同一图纸多次读取行序稳定，且不随区域文化漂移）；空输入=空表。
/// </summary>
public class BlockDefAggregatorTests
{
    [Fact]
    public void LayoutRows_AreSkipped()
    {
        var rows = new[]
        {
            ("*Model_Space", "TDbWall", true),
            ("$equip$00002832", "TDbECabletry", false),
            ("*Paper_Space0", "TDbDoor", true),
        };

        var result = BlockDefAggregator.Aggregate(rows);

        var only = Assert.Single(result);
        Assert.Equal("$equip$00002832", only.BlockName);
        Assert.Equal("TDbECabletry", only.RxClass);
        Assert.Equal(1, only.Count);
    }

    [Fact]
    public void SameNameAndClass_AreAggregated()
    {
        var rows = new[]
        {
            ("门块A", "TDbEFitting", false),
            ("门块A", "TDbEFitting", false),
            ("门块A", "TDbECabletry", false),
            ("块B", "TDbEFitting", false),
        };

        var result = BlockDefAggregator.Aggregate(rows);

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Single(r => r.BlockName == "门块A" && r.RxClass == "TDbEFitting").Count);
        Assert.Equal(1, result.Single(r => r.BlockName == "门块A" && r.RxClass == "TDbECabletry").Count);
        Assert.Equal(1, result.Single(r => r.BlockName == "块B" && r.RxClass == "TDbEFitting").Count);
    }

    // Ordinal 定序（不是区域文化序）：码位序 "B"(0x42) < "a"(0x61) < "b"(0x62)；
    // 同类块名内再按类名 Ordinal（"TDbDoor" < "TDbWall"）。
    [Fact]
    public void Output_IsOrdinalOrdered_ByBlockNameThenRxClass()
    {
        var rows = new[]
        {
            ("b", "TDbWall", false),
            ("a", "TDbWall", false),
            ("a", "TDbDoor", false),
            ("B", "TDbECabletry", false),
        };

        var result = BlockDefAggregator.Aggregate(rows);

        Assert.Equal(
            new[] { ("B", "TDbECabletry"), ("a", "TDbDoor"), ("a", "TDbWall"), ("b", "TDbWall") },
            result.Select(r => (r.BlockName, r.RxClass)).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(BlockDefAggregator.Aggregate(Array.Empty<(string, string, bool)>()));
        Assert.Empty(BlockDefAggregator.Aggregate(Enumerable.Empty<(string BlockName, string RxClass, bool IsLayout)>()));
    }

    [Fact]
    public void AllRowsLayout_ReturnsEmpty()
    {
        var rows = new[] { ("*Model_Space", "TDbWall", true), ("*Paper_Space", "TDbDoor", true) };
        Assert.Empty(BlockDefAggregator.Aggregate(rows));
    }
}
