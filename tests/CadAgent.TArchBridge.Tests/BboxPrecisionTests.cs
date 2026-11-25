using System.Globalization;
using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// A2（技术债批次A）：ENTITY 包围盒以 <c>"R"</c>（全精度、不变文化）写出——三位小数取整
/// （旧 <c>0.###</c>）会把 0.0005 变成 0.000/0.001，"写→解析"往返丢精度，与"bbox 是证据"相悖。
/// 对照面：PRIM/BLOCK 位置仍按 <c>0.###</c>（几何 token 的既定披露粒度不变）。
/// </summary>
public class BboxPrecisionTests
{
    [Fact]
    public void EntityBbox_SubMilliComponent_SurvivesRoundTrip_Exactly()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("21E00", "TDbWall", "ImpCurve", "0dt",
            new[] { 0.0005, -0.0005, 0.0, 100.25, 200.5, 3000.0 }, true));

        var parsed = BridgeResult.Parse(r.Write());

        var bbox = Assert.Single(parsed.Evidence).Bbox;
        Assert.NotNull(bbox);
        Assert.Equal(6, bbox!.Length);
        // 逐位相等（"R" 保证 double 回环；旧 0.### 在 0.0005 上必然落到 0 或 0.001）。
        Assert.Equal(0.0005, bbox[0]);
        Assert.Equal(-0.0005, bbox[1]);
        Assert.Equal(100.25, bbox[3]);
        Assert.Equal(200.5, bbox[4]);
    }

    [Fact]
    public void EntityBbox_IrregularDouble_SurvivesRoundTrip_Exactly()
    {
        // 0.1+0.2 这类不可精确表示的位型：只有 "R"/G17 才能原样回读。
        double v = 0.1 + 0.2;
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("21E00", "TDbWall", null, null,
            new[] { v, v, v, v, v, v }, false));

        var parsed = BridgeResult.Parse(r.Write());

        Assert.Equal(v, parsed.Evidence[0].Bbox![0]);
        Assert.Equal(v.ToString("R", CultureInfo.InvariantCulture),
            r.Write().Split('\n').Single(l => l.StartsWith("ENTITY\t", StringComparison.Ordinal)).Split('\t')[5].Split(',')[0]);
    }

    [Fact]
    public void EntityBbox_Null_StillWritesTwoEmptyColumns()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("21E00", "TDbWall", null, null, null, false));

        var parsed = BridgeResult.Parse(r.Write());

        Assert.Null(Assert.Single(parsed.Evidence).Bbox);
        Assert.Contains("ENTITY\t21E00\tTDbWall\t\t\t\t\t0\n", r.Write());
    }

    // 对照面：BLOCK 位置仍是 0.###（加性变更不得顺带提高其它段的披露粒度）。
    [Fact]
    public void BlockPosition_KeepsThreeDecimals()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Blocks.Add(new BlockDto("21E00", "blk", new[] { 0.0004, 1.23456, 0.0004 }));

        string text = r.Write();

        Assert.Contains("BLOCK\t21E00\tblk\t0,1.235,0\n", text);
    }
}
