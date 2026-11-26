using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
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

    // ===== 项2 F：纸面空间渲染 + 锁定视口框 + 布局合成 =====

    /// <summary>
    /// 布局合成：纸面 → 视口内模型 → 视口框（框在末尾）。限定到真实视口：
    /// AddViewport 自动附加纸边界视口（RepresentsPaper，ViewHeight=0），退化视口
    /// 不参与合成（与 SheetRegistryBuilder.BuildLayoutSheets 同口径）。
    /// </summary>
    [Fact]
    public void ComposeLayout_IncludesPaperEntitiesAttributesAndLockedViewportFrame()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            var title = new BlockRecord("图框");
            title.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
            // Ruling R8：ATTDEF 定义不渲染（模板），实例值仅经 Insert.Attributes
            title.Entities.Add(new AttributeDefinition { Tag = "DEF", Value = "DEFVAL" });
            doc.BlockRecords.Add(title);
            var layout = new ACadSharp.Objects.Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0), Width = 1000, Height = 700,
                ViewTarget = new XYZ(0, 0, 0), ViewHeight = 200
            });
            // AddViewport 之后 AssociatedBlock 才可用（既有夹具口径）
            var ins = new Insert(title) { InsertPoint = new XYZ(10, 10, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01", InsertPoint = new XYZ(20, 20, 0), Height = 3 });
            layout.AssociatedBlock.Entities.Add(ins);
        });
        var session = DrawingSession.Load(path);
        try
        {
            Viewport vp = session.Doc.Layouts.First(l => l.Name == "布局1").AssociatedBlock.Entities
                .OfType<Viewport>().First(v => !v.RepresentsPaper);
            var items = RenderModelBuilder.ComposeLayout(session.Doc, "布局1", vp);
            Assert.Contains(items, i => i.Space == RenderSpace.PaperSpace && i.AttributeTag == "图号" && i.Text == "Z-E01");
            var frame = items.Where(i => i.Locked).ToList();
            Assert.Equal(4, frame.Count);
            Assert.All(frame, f =>
            {
                Assert.Equal(RenderSpace.PaperSpace, f.Space);
                Assert.Equal(vp.Handle.ToString(), f.SourceHandle); // 框线溯源到视口实体
            });
            Assert.Contains(items, i => i.Space == RenderSpace.ModelSpace); // 视口内模型线
            // Ruling R8：ATTDEF 定义不展开成文本——DEFVAL 只出现一次（Insert 构造时
            // 由 ATTDEF 自动生成的 ATTRIB 实例，属可见实例值），不带 AttributeTag 的
            // 「定义默认值」文本必须为零（否则真实图框出现定义值+实例值重叠）。
            var defTexts = items.Where(i => i.Text == "DEFVAL").ToList();
            Assert.Single(defTexts);
            Assert.Equal("DEF", defTexts[0].AttributeTag);
            // 顺序：纸面 → 模型 → 框（框在列表末尾）
            Assert.True(items[^1].Locked);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BuildPaperSpace_ExcludesViewport_AndStampsPaperSpace()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var layout = new ACadSharp.Objects.Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AddViewport(PaperViewport());
            layout.AssociatedBlock.Entities.Add(new TextEntity
            {
                InsertPoint = new XYZ(5, 5, 0), Value = "纸面注记", Height = 2,
            });
        });
        var session = DrawingSession.Load(path);
        try
        {
            var items = RenderModelBuilder.BuildPaperSpace(session.Doc, "布局1");
            var item = Assert.Single(items);
            Assert.Equal(RenderKind.Text, item.Kind);
            Assert.Equal("纸面注记", item.Text);
            Assert.Equal(RenderSpace.PaperSpace, item.Space);
            Assert.Null(item.AttributeTag);
            // 顶层纸面实体：Handle/SourceHandle 均为实体自身句柄
            Assert.False(string.IsNullOrEmpty(item.SourceHandle));
            Assert.Equal(item.Handle, item.SourceHandle);
            // 未知布局名 → 空（不抛）
            Assert.Empty(RenderModelBuilder.BuildPaperSpace(session.Doc, "不存在"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BuildViewportFrame_FourLockedSegments_TracePaperCorners()
    {
        var vp = PaperViewport(); // Center(500,350) 1000×700 → 纸面 (0,0)-(1000,700)
        var frame = RenderModelBuilder.BuildViewportFrame(vp);
        Assert.Equal(4, frame.Count);
        string h = vp.Handle.ToString();
        Assert.All(frame, f =>
        {
            Assert.Equal(RenderKind.Line, f.Kind);
            Assert.Equal(RenderSpace.PaperSpace, f.Space);
            Assert.True(f.Locked);
            Assert.Equal(h, f.Handle);
            Assert.Equal(h, f.SourceHandle);
            Assert.Equal(2, f.Points.Count);
        });
        Assert.Equal((0.0, 0.0), frame[0].Points[0]);
        Assert.Equal((1000.0, 0.0), frame[0].Points[1]);
        Assert.Equal((1000.0, 700.0), frame[2].Points[0]);
        Assert.Equal((0.0, 700.0), frame[3].Points[0]); // 左边自上而下
        Assert.Equal((0.0, 0.0), frame[3].Points[1]);
    }

    /// <summary>
    /// Ruling R8：块内 ATTDEF（模板定义）不展开成文本，也不计数 skipped。
    /// 夹具口径：Insert 先于 ATTDEF 构造——ACadSharp `new Insert(block)` 会按块内
    /// 既有 ATTDEF 自动生成 ATTRIB 实例（探针核定），先建 Insert 才能隔离"定义"与"实例"。
    /// </summary>
    [Fact]
    public void Build_BlockAttributeDefinition_NotRenderedAndNotCounted()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var frame = new BlockRecord("FRAME");
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
            doc.BlockRecords.Add(frame);
            doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
            frame.Entities.Add(new AttributeDefinition { Tag = "DEF", Value = "DEFVAL" });
        });
        var session = DrawingSession.Load(path);
        try
        {
            var items = RenderModelBuilder.Build(session, out int skipped);
            Assert.Single(items); // 仅块内 Line
            Assert.DoesNotContain(items, i => i.Text == "DEFVAL");
            Assert.Equal(0, skipped); // 定义不计数（不是被跳过的实体）
        }
        finally { File.Delete(path); }
    }
}
