using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// A1（技术债批次A·协议下沉）+ B0（技术债批次B·去 System 垫片）：块定义行聚合
/// <see cref="BlockDefAggregator.Aggregate"/>。
/// 契约：跳过 IsLayout 行（布局块不是块定义）；按 (块名, 类) 聚合计数；
/// 输出按块名 → 类名 **Ordinal** 定序（同一图纸多次读取行序稳定，且不随区域文化漂移）；空输入=空表。
/// B0：输入/输出均为具名记录（<see cref="BlockDefScanRow"/> / <see cref="BlockDefAggRow"/>）——
/// 公开签名不再引用 System.ValueTuple（net45 垫片已删除），聚合语义逐项不变。
/// </summary>
public class BlockDefAggregatorTests
{
    private static BlockDefScanRow Row(string name, string rx, bool layout = false) =>
        new(name, rx, layout);

    [Fact]
    public void LayoutRows_AreSkipped()
    {
        var rows = new[]
        {
            Row("*Model_Space", "TDbWall", layout: true),
            Row("$equip$00002832", "TDbECabletry"),
            Row("*Paper_Space0", "TDbDoor", layout: true),
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
            Row("门块A", "TDbEFitting"),
            Row("门块A", "TDbEFitting"),
            Row("门块A", "TDbECabletry"),
            Row("块B", "TDbEFitting"),
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
            Row("b", "TDbWall"),
            Row("a", "TDbWall"),
            Row("a", "TDbDoor"),
            Row("B", "TDbECabletry"),
        };

        var result = BlockDefAggregator.Aggregate(rows);

        Assert.Equal(
            new[] { ("B", "TDbECabletry"), ("a", "TDbDoor"), ("a", "TDbWall"), ("b", "TDbWall") },
            result.Select(r => (r.BlockName, r.RxClass)).ToArray());
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(BlockDefAggregator.Aggregate(Array.Empty<BlockDefScanRow>()));
        Assert.Empty(BlockDefAggregator.Aggregate(Enumerable.Empty<BlockDefScanRow>()));
    }

    [Fact]
    public void AllRowsLayout_ReturnsEmpty()
    {
        var rows = new[] { Row("*Model_Space", "TDbWall", layout: true), Row("*Paper_Space", "TDbDoor", layout: true) };
        Assert.Empty(BlockDefAggregator.Aggregate(rows));
    }

    // B0：输出是具名记录（静态类型即 BlockDefAggRow；值语义相等可钉）。
    [Fact]
    public void Aggregate_ReturnsNamedRecordRows()
    {
        var result = BlockDefAggregator.Aggregate(new[] { Row("块A", "TDbWall") });

        BlockDefAggRow row = Assert.Single(result);
        Assert.Equal(new BlockDefAggRow("块A", "TDbWall", 1), row);   // record 值语义
        Assert.Equal("块A", row.BlockName);
        Assert.Equal("TDbWall", row.RxClass);
        Assert.Equal(1, row.Count);
    }
}
