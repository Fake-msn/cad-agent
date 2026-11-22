using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.View.Rendering;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 幅面感知 T5：布局视口读图数学与裁剪。
/// ViewportTransform.ModelToPaper（Core，T1 落地零测试覆盖——本任务补钉）；
/// RenderModelBuilder.BuildViewport（模型区域按 ViewTarget/ViewHeight×纸面宽高比裁剪 +
/// 仿射映射到纸面坐标，复用既有 Append 管线）；CropToFrame（模型幅面按锚点框内裁剪）。
/// Viewport 构造以编译为准（探针核定：new Viewport{Center/Width/Height/ViewTarget/ViewHeight}，
/// Center/Width/Height 纸面尺寸，ViewTarget/ViewHeight 模型区域）。
/// </summary>
public class ViewportRenderTests
{
    [Fact]
    public void ModelToPaper_KnownTransform()
    {
        // viewTarget=(100,100) viewHeight=200 paperCenter=(500,350) paperHeight=700
        // → 缩放 s=3.5；model(100,300) 相对 target dy=200 → paper dy=700 → (500,1050)
        var p = ViewportTransform.ModelToPaper((100, 300), (100, 100), 200, (500, 350), 700);
        Assert.Equal(500, p.X, 6);
        Assert.Equal(1050, p.Y, 6);
        // target 自身映射到纸面中心
        var c = ViewportTransform.ModelToPaper((100, 100), (100, 100), 200, (500, 350), 700);
        Assert.Equal(500, c.X, 6);
        Assert.Equal(350, c.Y, 6);
    }

    [Fact]
    public void ModelToPaper_ZeroViewHeight_FallsBackToUnitScale()
    {
        // 退化 viewHeight（≤1e-12）→ s=1（不除零），仅平移 target→paperCenter
        var p = ViewportTransform.ModelToPaper((130, 140), (100, 100), 0, (500, 350), 700);
        Assert.Equal(530, p.X, 6);
        Assert.Equal(390, p.Y, 6);
    }

    /// <summary>模型线 (0,0)-(1000,0) 与 (5000,5000)-(6000,5000)；视口 viewTarget=(0,0) viewHeight=200。</summary>
    private static CadDocument TwoLineDoc()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(6000, 5000, 0) });
        return doc;
    }

    private static Viewport PaperViewport() => new()
    {
        Center = new XYZ(500, 350, 0),
        Width = 1000,
        Height = 700,
        ViewTarget = new XYZ(0, 0, 0),
        ViewHeight = 200,
    };

    [Fact]
    public void BuildViewport_CropsModelRegion_AndMapsToPaper()
    {
        // 裁剪半径=viewHeight/2×aspect（aspect=纸面宽高比 1000/700）：半高 100、半宽≈142.86。
        // 首线锚点 (0,0) 在区域内 → 入；远端线锚点 (5000,5000) 在区域外 → 不入。
        var doc = TwoLineDoc();
        var vp = PaperViewport();
        var items = RenderModelBuilder.BuildViewport(doc, vp);
        var item = Assert.Single(items);
        Assert.Equal(RenderKind.Line, item.Kind);
        // s=700/200=3.5；model(0,0)→paper(500,350)；model(1000,0)→(500+3500,350)
        Assert.Equal(500, item.Points[0].X, 6);
        Assert.Equal(350, item.Points[0].Y, 6);
        Assert.Equal(4000, item.Points[1].X, 6);
        Assert.Equal(350, item.Points[1].Y, 6);
        // 句柄口径不变（源实体 DXF 句柄）
        Assert.Equal(doc.Entities.First().Handle.ToString(), item.Handle);
    }

    [Fact]
    public void BuildViewport_TextHeightScalesToPaper()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Value = "注记", Height = 10 });
        var items = RenderModelBuilder.BuildViewport(doc, PaperViewport());
        var item = Assert.Single(items);
        Assert.Equal(RenderKind.Text, item.Kind);
        Assert.Equal("注记", item.Text);
        Assert.Equal(35, item.TextHeight, 6); // 10 × s(3.5)
        Assert.Equal(500, item.Points[0].X, 6);
        Assert.Equal(350, item.Points[0].Y, 6);
    }

    [Fact]
    public void BuildViewport_InsertInRegion_ExpandsWithPaperAffine()
    {
        var doc = new CadDocument();
        var block = new BlockRecord("SYM");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
        var items = RenderModelBuilder.BuildViewport(doc, PaperViewport());
        var item = Assert.Single(items);
        Assert.Equal(RenderKind.Line, item.Kind);
        // 插入点 (0,0)→(500,350)；子线端点 (10,0)→(500+35,350)
        Assert.Equal(500, item.Points[0].X, 6);
        Assert.Equal(350, item.Points[0].Y, 6);
        Assert.Equal(535, item.Points[1].X, 6);
        // Insert 展开子图元句柄="{insertHandle}:{childIndex}"
        Assert.Equal(doc.Entities.First().Handle + ":0", item.Handle);
    }

    [Fact]
    public void BuildViewport_OutOfRegionInsert_Excluded()
    {
        var doc = new CadDocument();
        var block = new BlockRecord("SYM");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(9000, 9000, 0) });
        Assert.Empty(RenderModelBuilder.BuildViewport(doc, PaperViewport()));
    }

    [Fact]
    public void CropToFrame_KeepsOnlyAnchorInsideBounds()
    {
        // 模型幅面裁剪：锚点（Points[0]）框内保留，框外剔除（与 SheetIdForPoint 同口径）
        var items = new[]
        {
            new RenderItem(RenderKind.Line, new[] { (100.0, 100.0), (200.0, 100.0) }, "0", 0xFFFFFF, null, 0, 0, "1"),
            new RenderItem(RenderKind.Line, new[] { (2100.0, 100.0), (2200.0, 100.0) }, "0", 0xFFFFFF, null, 0, 0, "2"),
            new RenderItem(RenderKind.Text, new[] { (500.0, 650.0) }, "图框", 0xFFFFFF, "一层", 30, 0, "3"),
        };
        var cropped = RenderModelBuilder.CropToFrame(items, (0, 0, 1000, 700));
        Assert.Equal(2, cropped.Count);
        Assert.Equal(new[] { "1", "3" }, cropped.Select(i => i.Handle).ToArray());
    }
}
