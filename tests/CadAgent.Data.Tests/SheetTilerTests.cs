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
/// 幅面驱动瓦片（tile-coverage 根因修复）：可读分辨率规律→可读字高反算区域宽
/// （regionW = viewportW × readableH / targetTextPx，默认 24px 目标字高）；
/// 字高双峰间隙分割（微字峰不得主导）+ 密度格网选瓦（瓦落内容密集格，非幻影锚偏移）
/// + 图签瓦=锚层真实 bbox 中心 + 10% 骑缝重叠边距 + TilingResult 覆盖率披露；
/// 1 overview（FrameBounds）恒首 + N tiles（上限 6 防爆炸）；
/// 无文字/退化→单 overview（诚实：无字高依据不切瓦，HeightBasis="none"）。
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

    // 直构幅面+walked（根因夹具：大数量点阵不经 CadDocument 管线；镜像既有直构口径——
    // TextEntity 设 Height/Layer + WorldX/Y 锚点 + SheetId 归属）。
    private static (SheetRecord Sheet, IReadOnlyList<WalkedEntity> Walked) DirectSetup(
        (double MinX, double MinY, double MaxX, double MaxY) frame,
        params (double X, double Y, double H, string Layer)[] texts)
    {
        var sheet = new SheetRecord("S1", "测试幅面", null, null, null, null, frame, null,
            new Dictionary<string, string>());
        var walked = new List<WalkedEntity>();
        for (int i = 0; i < texts.Length; i++)
        {
            var (x, y, h, layerName) = texts[i];
            var entity = new TextEntity { Value = "字", Height = h, Layer = new Layer(layerName) };
            walked.Add(new WalkedEntity(entity, i.ToString(), layerName, x, y, "S1", new[] { (x, y) }));
        }
        return (sheet, walked);
    }

    // 幻影锚回归夹具：frame (0,0)-(100000,60000)；内容 500 点聚左中
    // (10000..29200, 20000..39000) 字高 250；说明层 20 点聚右上 (80000..90000, 50000..54500)
    // 字高 250；微字 961 点字高 2.4 满幅散布（微字峰主导旧中位数口径）。
    private static (SheetRecord Sheet, IReadOnlyList<WalkedEntity> Walked) ClusterFixture()
    {
        var texts = new List<(double X, double Y, double H, string Layer)>();
        for (int i = 0; i < 25; i++)
            for (int j = 0; j < 20; j++)
                texts.Add((10000 + i * 800, 20000 + j * 1000, 250, "0"));
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 4; j++)
                texts.Add((80000 + i * 2500, 50000 + j * 1500, 250, "说明"));
        for (int i = 0; i < 31; i++)
            for (int j = 0; j < 31; j++)
                texts.Add((1600 + i * 3200, 1000 + j * 1900, 2.4, "0"));
        return DirectSetup((0, 0, 100000, 60000), texts.ToArray());
    }

    // ===== 既有行为保持测试（消费点同步 TilingResult，sanctioned）=====

    [Fact]
    public void Tiles_TextHeightBacksolve_OverviewPlusCappedTiles()
    {
        // 字高 3.5（单峰→中位数口径不变）→ regionW=2800×3.5/24≈408.33，regionH≈255.21；
        // 幅面 1000×700 需多瓦：overview 恒首 + 密度格网瓦（上限 6）；瓦裁剪幅面内且去重。
        var (sheet, walked) = TilerSetup(
            (100, 100, 3.5, "0"), (900, 600, 3.5, "0"), (500, 350, 3.5, "0"));

        var result = SheetTiler.Tiles(sheet, walked);
        var tiles = result.Tiles;

        Assert.Equal(2800 * 3.5 / 24, result.RegionW, 6);
        Assert.Equal(1750 * 3.5 / 24, result.RegionH, 6);
        Assert.Equal("median", result.HeightBasis);
        Assert.True(tiles.Count > 1);
        Assert.True(tiles.Count - 1 <= SheetTiler.MaxTiles);
        Assert.Equal(sheet.SheetId, tiles[0].SheetId);
        Assert.Equal(sheet.FrameBounds, tiles[0].Rect);
        // 密度格网：3 文字各自所在格入选→每个文字被≥1 非 overview 瓦覆盖
        var content = tiles.Skip(1).ToList();
        foreach (var (x, y) in new[] { (100.0, 100.0), (900.0, 600.0), (500.0, 350.0) })
            Assert.Contains(content, t => x >= t.Rect.MinX && x <= t.Rect.MaxX && y >= t.Rect.MinY && y <= t.Rect.MaxY);
        Assert.True(result.ContentCoverage > 0.5);
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
        // 无文字→单 overview（无字高依据不切瓦；HeightBasis="none"、覆盖率 0）。
        var (sheet, walked) = TilerSetup();
        var result = SheetTiler.Tiles(sheet, walked);
        var only = Assert.Single(result.Tiles);
        Assert.Equal(sheet.SheetId, only.SheetId);
        Assert.Equal(sheet.FrameBounds, only.Rect);
        Assert.Equal(0, result.ContentCoverage);
        Assert.Equal("none", result.HeightBasis);
    }

    [Fact]
    public void Tiles_RegionCoversWholeFrame_SingleOverview()
    {
        // 字高 100 → regionW≈11667≥1000、regionH≈7292≥700：单视口已可读，无需切瓦。
        var (sheet, walked) = TilerSetup((500, 350, 100, "0"));
        var result = SheetTiler.Tiles(sheet, walked);
        Assert.Single(result.Tiles);
        Assert.Equal(sheet.FrameBounds, result.Tiles[0].Rect);
        Assert.Equal("none", result.HeightBasis);
    }

    [Fact]
    public void Tiles_AnchorLayer_TitleTile_BboxCenter()
    {
        // 图签瓦=第一梯队锚层（说明）真实 bbox 中心：说明文字 (250/300/350,300) → 中心 (300,300)；
        // WALL 文字 (900,600) 非锚层，不参与图签瓦（密度格网另行选瓦）。
        var (sheet, walked) = TilerSetup(
            (250, 300, 3.5, "说明"), (300, 300, 3.5, "说明"), (350, 300, 3.5, "说明"),
            (900, 600, 3.5, "WALL"));

        var tiles = SheetTiler.Tiles(sheet, walked).Tiles;

        double regionW = 2800 * 3.5 / 24, regionH = 1750 * 3.5 / 24;
        var center = tiles[1].Rect; // 图签瓦恒为首个非 overview 瓦
        Assert.Equal(300 - regionW / 2, center.MinX, 6);
        Assert.Equal(300 + regionW / 2, center.MaxX, 6);
        Assert.Equal(300 - regionH / 2, center.MinY, 6);
        Assert.Equal(300 + regionH / 2, center.MaxY, 6);
    }

    [Fact]
    public void Tiles_MedianHeight_EvenCount_AndMText()
    {
        // 字高 {2,4}（其一为 MText）→ 单峰中位数 3（偶数取中间两值平均，R4 口径逐字保持）
        // → regionW=2800×3/24=350（MText 字高计入，否则中位数 2→233.33）。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(400, 300, 0), Value = "甲", Height = 2 });
        doc.Entities.Add(new MText { InsertPoint = new XYZ(600, 300, 0), Value = "乙", Height = 4 });
        var sheets = SheetRegistryBuilder.Build(doc);
        var walked = BlockWalker.Walk(doc, sheets).ToList();

        var result = SheetTiler.Tiles(sheets[0], walked);

        Assert.Equal(2800 * 3.0 / 24, result.RegionW, 6);
        Assert.Equal("median", result.HeightBasis);
    }

    [Fact]
    public void Tiles_DegenerateTargetPx_SingleOverview()
    {
        // targetTextPx≤0 退化守卫→单 overview（不产生非法区域）。
        var (sheet, walked) = TilerSetup((500, 350, 3.5, "0"));
        var result = SheetTiler.Tiles(sheet, walked, targetTextPx: 0);
        Assert.Single(result.Tiles);
        Assert.Equal("none", result.HeightBasis);
    }

    // ===== 根因钉住测试（微字峰/幻影锚/覆盖率）=====

    [Fact]
    public void ReadableHeight_BimodalMicroAndAnnotation_PicksUpperCluster()
    {
        // 微字峰 972 个（1.5/2.4/2.5/2.7）+ 标注峰 598 个（150/250/400/600）——真实标本分布口径
        var heights = new List<double>();
        heights.AddRange(Enumerable.Repeat(2.4, 477));
        heights.AddRange(Enumerable.Repeat(1.5, 228));
        heights.AddRange(Enumerable.Repeat(2.5, 207));
        heights.AddRange(Enumerable.Repeat(2.7, 60));
        heights.AddRange(Enumerable.Repeat(150.0, 206));
        heights.AddRange(Enumerable.Repeat(250.0, 255));
        heights.AddRange(Enumerable.Repeat(400.0, 82));
        heights.AddRange(Enumerable.Repeat(600.0, 55));
        double h = SheetTiler.ReadableHeight(heights);
        Assert.Equal(250, h, 3); // 上峰加权中位数（非 2.4 微字峰）
    }

    [Fact]
    public void ReadableHeight_Unimodal_KeepsMedian()
    {
        var heights = Enumerable.Repeat(350.0, 100).ToList();
        Assert.Equal(350, SheetTiler.ReadableHeight(heights), 3);
    }

    [Fact]
    public void Tiles_ContentClusterAwayFromAnchor_TilesCoverContent()
    {
        // 幻影锚回归：锚层（说明）点集中右侧；内容实体集中左侧；
        // 旧算法独立轴中位数+偏移→左内容无瓦；新算法密度格网→左内容格入选
        var (sheet, walked) = ClusterFixture();
        var result = SheetTiler.Tiles(sheet, walked);
        var tiles = result.Tiles.Skip(1).ToList(); // 除 overview
        Assert.Contains(tiles, t => t.Rect.MinX <= 15000 && t.Rect.MaxX >= 25000); // 左内容被瓦覆盖
        Assert.True(result.ContentCoverage > 0.5);
        // 根因①连带钉住：微字峰（961×2.4）不得主导 region——上峰 250 反算
        Assert.Equal("bimodal-upper", result.HeightBasis);
        Assert.Equal(2800 * 250.0 / 24, result.RegionW, 6);
    }

    [Fact]
    public void Tiles_CoverageMetric_ExcludesOverview()
    {
        // 单 overview（无文字）→ ContentCoverage==0（overview 不计覆盖）
        var (sheet, _) = DirectSetup((0, 0, 1000, 700));
        var walked = new List<WalkedEntity>
        {
            new(new Line { StartPoint = new XYZ(100, 100, 0), EndPoint = new XYZ(900, 600, 0) },
                "L1", "0", 100, 100, sheet.SheetId, new[] { (100.0, 100.0), (900.0, 600.0) }),
            new(new Line { StartPoint = new XYZ(200, 500, 0), EndPoint = new XYZ(800, 200, 0) },
                "L2", "0", 200, 500, sheet.SheetId, new[] { (200.0, 500.0), (800.0, 200.0) }),
        };
        var result = SheetTiler.Tiles(sheet, walked);
        Assert.Single(result.Tiles);
        Assert.Equal(0, result.ContentCoverage);
        Assert.Equal("none", result.HeightBasis);
    }

    [Fact]
    public void Tiles_OverlapMargin_TenPercent()
    {
        // 瓦矩形较 regionW 扩 10%（夹取幅面内）：相邻瓦重叠>0。
        // 夹具：两簇各 100 点字高 250，分属相邻格 cell(0,0)/cell(1,0)（无锚层→无图签瓦）。
        var texts = new List<(double X, double Y, double H, string Layer)>();
        for (int i = 0; i < 10; i++)
            for (int j = 0; j < 10; j++)
            {
                texts.Add((10000 + i * 1000, 10000 + j * 500, 250, "0")); // 左簇 (10000..19000, 10000..14500)
                texts.Add((35000 + i * 1000, 10000 + j * 500, 250, "0")); // 右簇 (35000..44000, 10000..14500)
            }
        var (sheet, walked) = DirectSetup((0, 0, 100000, 60000), texts.ToArray());
        var result = SheetTiler.Tiles(sheet, walked);
        var tiles = result.Tiles.Skip(1).ToList();
        Assert.True(tiles.Count >= 2);
        // 断言存在两瓦相交面积>0（骑缝闭合）
        bool overlapped = false;
        for (int a = 0; a < tiles.Count; a++)
            for (int b = a + 1; b < tiles.Count; b++)
            {
                double iw = Math.Min(tiles[a].Rect.MaxX, tiles[b].Rect.MaxX) - Math.Max(tiles[a].Rect.MinX, tiles[b].Rect.MinX);
                double ih = Math.Min(tiles[a].Rect.MaxY, tiles[b].Rect.MaxY) - Math.Max(tiles[a].Rect.MinY, tiles[b].Rect.MinY);
                if (iw > 0 && ih > 0) overlapped = true;
            }
        Assert.True(overlapped);
        // 10% 边距精确钉住：未被幅面夹取的格瓦宽 = regionW × 1.2
        Assert.Contains(tiles, t => Math.Abs(t.Rect.MaxX - t.Rect.MinX - result.RegionW * 1.2) < 1e-6);
    }

    [Fact]
    public void Tiles_TitleTile_RealBboxCenter_NotPhantomMedian()
    {
        // 锚层点 L 形分布：横臂 (60000..79000, 50000) 20 点 + 竖臂 (90000, 20000..39000) 20 点；
        // 独立轴中位数合成幻影点 (84500,44500)（落空区）；真实 bbox (60000..90000, 20000..50000)
        // 中心 (75000,35000)→图签瓦以此为中心（断言图签瓦内含≥1 锚层实体）。
        var texts = new List<(double X, double Y, double H, string Layer)>();
        for (int i = 0; i < 20; i++) texts.Add((60000 + i * 1000, 50000, 300, "说明"));
        for (int j = 0; j < 20; j++) texts.Add((90000, 20000 + j * 1000, 300, "说明"));
        var (sheet, walked) = DirectSetup((0, 0, 100000, 60000), texts.ToArray());

        var result = SheetTiler.Tiles(sheet, walked);

        double regionW = 2800 * 300.0 / 24, regionH = 1750 * 300.0 / 24; // 35000×21875
        var title = result.Tiles[1].Rect; // 图签瓦恒为首个非 overview 瓦
        Assert.Equal(75000 - regionW / 2, title.MinX, 6);
        Assert.Equal(75000 + regionW / 2, title.MaxX, 6);
        Assert.Equal(35000 - regionH / 2, title.MinY, 6);
        Assert.Equal(35000 + regionH / 2, title.MaxY, 6);
        // 图签瓦内含≥1 锚层实体（竖臂 y≥25000 点落瓦内；幻影中位数中心则偏离锚簇）
        int anchorsInside = walked.Count(w => (w.Entity.Layer?.Name ?? "") == "说明"
            && w.WorldX >= title.MinX && w.WorldX <= title.MaxX
            && w.WorldY >= title.MinY && w.WorldY <= title.MaxY);
        Assert.True(anchorsInside >= 1);
    }

    [Fact]
    public void Tiles_MaxTilesCap_And_OverviewFirst_Kept()
    {
        var (sheet, walked) = ClusterFixture();
        var result = SheetTiler.Tiles(sheet, walked);
        Assert.True(result.Tiles.Count - 1 <= SheetTiler.MaxTiles);
        Assert.Equal(sheet.FrameBounds, result.Tiles[0].Rect);
    }
}
