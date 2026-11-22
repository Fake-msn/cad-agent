using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;
using CadAgent.Data.Vision;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// T6：幅面驱动瓦片。可读分辨率规律→字高中位数反算区域宽
/// （regionW = viewportW × medianH / targetTextPx，默认 24px 目标字高）；
/// 1 overview（FrameBounds）+ N tiles（语义层中位数锚点+左右/上下偏移，上限 6 防爆炸）；
/// 无文字→单 overview（诚实：无字高依据不切瓦）。
/// </summary>
public class SheetTilerTests
{
    // 图框 1000×700 + 指定文字（层/字高/位置由测试给定）。
    private static (SheetRecord Sheet, IReadOnlyList<WalkedEntity> Walked) TilerSetup(
        params (double X, double Y, double H, string Layer)[] texts)
    {
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        foreach (var (x, y, h, layerName) in texts)
        {
            // 同名层复用（默认层 0 已存在；重复 Add 会冲突）
            var layer = doc.Layers.FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
            if (layer is null) { layer = new Layer(layerName); doc.Layers.Add(layer); }
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(x, y, 0), Value = "文字", Height = h, Layer = layer });
        }
        var sheets = SheetRegistryBuilder.Build(doc);
        var walked = BlockWalker.Walk(doc, sheets).ToList();
        return (sheets[0], walked);
    }

    [Fact]
    public void Tiles_TextHeightBacksolve_OverviewPlusCappedTiles()
    {
        // 字高 3.5 → regionW=2800×3.5/24≈408.33，regionH=1750×3.5/24≈255.21；
        // 幅面 1000×700 需多瓦：overview + 上限 6 tiles = 7；中心瓦=锚点（无文字语义层→幅面中心）。
        var (sheet, walked) = TilerSetup(
            (100, 100, 3.5, "0"), (900, 600, 3.5, "0"), (500, 350, 3.5, "0"));

        var tiles = SheetTiler.Tiles(sheet, walked);

        Assert.Equal(7, tiles.Count);
        Assert.Equal(sheet.SheetId, tiles[0].SheetId);
        Assert.Equal(sheet.FrameBounds, tiles[0].Rect);
        double regionW = 2800 * 3.5 / 24, regionH = 1750 * 3.5 / 24;
        var center = tiles[1].Rect;
        Assert.Equal(500 - regionW / 2, center.MinX, 6);
        Assert.Equal(500 + regionW / 2, center.MaxX, 6);
        Assert.Equal(350 - regionH / 2, center.MinY, 6);
        Assert.Equal(350 + regionH / 2, center.MaxY, 6);
        // 全部瓦片在幅面内（裁剪）且互不重复
        foreach (var (_, r) in tiles)
        {
            Assert.True(r.MinX >= sheet.FrameBounds.MinX && r.MaxX <= sheet.FrameBounds.MaxX);
            Assert.True(r.MinY >= sheet.FrameBounds.MinY && r.MaxY <= sheet.FrameBounds.MaxY);
        }
        Assert.Equal(tiles.Count, tiles.Select(t => t.Rect).Distinct().Count());
    }

    [Fact]
    public void Tiles_NoText_SingleOverview()
    {
        // 无文字→单 overview（无字高依据不切瓦）。
        var (sheet, walked) = TilerSetup();
        var tiles = SheetTiler.Tiles(sheet, walked);
        var only = Assert.Single(tiles);
        Assert.Equal(sheet.SheetId, only.SheetId);
        Assert.Equal(sheet.FrameBounds, only.Rect);
    }

    [Fact]
    public void Tiles_RegionCoversWholeFrame_SingleOverview()
    {
        // 字高 100 → regionW≈11667≥1000、regionH≈7292≥700：单视口已可读，无需切瓦。
        var (sheet, walked) = TilerSetup((500, 350, 100, "0"));
        var tiles = SheetTiler.Tiles(sheet, walked);
        Assert.Single(tiles);
        Assert.Equal(sheet.FrameBounds, tiles[0].Rect);
    }

    [Fact]
    public void Tiles_SemanticLayerAnchor_MedianPreferred()
    {
        // 锚点=语义层组中位数（说明层优先）：说明文字 (250/300/350,300) → 锚点 (300,300)；
        // WALL 文字 (900,600) 属第二梯队，不参与（第一梯队非空）。
        var (sheet, walked) = TilerSetup(
            (250, 300, 3.5, "说明"), (300, 300, 3.5, "说明"), (350, 300, 3.5, "说明"),
            (900, 600, 3.5, "WALL"));

        var tiles = SheetTiler.Tiles(sheet, walked);

        double regionW = 2800 * 3.5 / 24, regionH = 1750 * 3.5 / 24;
        var center = tiles[1].Rect;
        Assert.Equal(300 - regionW / 2, center.MinX, 6);
        Assert.Equal(300 + regionW / 2, center.MaxX, 6);
        Assert.Equal(300 - regionH / 2, center.MinY, 6);
        Assert.Equal(300 + regionH / 2, center.MaxY, 6);
    }

    [Fact]
    public void Tiles_MedianHeight_EvenCount_AndMText()
    {
        // 字高 {2,4}（其一为 MText）→ 中位数 3 → regionW=2800×3/24=350。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(400, 300, 0), Value = "甲", Height = 2 });
        doc.Entities.Add(new MText { InsertPoint = new XYZ(600, 300, 0), Value = "乙", Height = 4 });
        var sheets = SheetRegistryBuilder.Build(doc);
        var walked = BlockWalker.Walk(doc, sheets).ToList();

        var tiles = SheetTiler.Tiles(sheets[0], walked);

        double regionW = 2800 * 3.0 / 24;
        var center = tiles[1].Rect;
        Assert.Equal(regionW, center.MaxX - center.MinX, 6);
        Assert.Equal(500 - regionW / 2, center.MinX, 6);
    }

    [Fact]
    public void Tiles_DegenerateTargetPx_SingleOverview()
    {
        // targetTextPx≤0 退化守卫→单 overview（不产生非法区域）。
        var (sheet, walked) = TilerSetup((500, 350, 3.5, "0"));
        var tiles = SheetTiler.Tiles(sheet, walked, targetTextPx: 0);
        Assert.Single(tiles);
    }
}
