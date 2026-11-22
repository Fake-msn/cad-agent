using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Tests;

public class SheetRegistryTests
{
    // 合成双幅面夹具：两个图框块（含属性 图号/图纸名称/比例）+ 幅面名文字 + 框内实体
    private static CadDocument TwoSheetDoc()
    {
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        for (int i = 0; i < 2; i++)
        {
            var ins = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
            ins.Attributes.Add(new AttributeEntity { Tag = "比例", Value = "1:100" });
            ins.Attributes.Add(new AttributeEntity { Tag = "日期", Value = "2026.09" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(i * 2000 + 500, 650, 0), Value = i == 0 ? "一层电力平面图" : "二层电力平面图", Height = 30, Layer = new Layer("图框") });
            doc.Entities.Add(new Line { StartPoint = new XYZ(i * 2000 + 100, 100, 0), EndPoint = new XYZ(i * 2000 + 200, 100, 0), Layer = new Layer("E-WIRE") });
        }
        return doc;
    }
    // 注意：Insert.Attributes 可加性/AttributeEntity 构造以编译为准（探针核定属性存在可读）；
    // Layer 构造 new Layer(name) 既有夹具先例（RenderModelTests）；doc.Entities.Add 自动挂层表若否先 doc.Layers.Add。

    [Fact]
    public void Build_TwoFrames_TwoSheetsWithAttributesAndNames()
    {
        var sheets = SheetRegistryBuilder.Build(TwoSheetDoc());
        Assert.Equal(2, sheets.Count);
        Assert.Equal("一层电力平面图", sheets[0].Name);
        Assert.Equal("Z-E01", sheets[0].Number);
        Assert.Equal("1:100", sheets[0].Scale);
        Assert.Equal("2026.09", sheets[0].Date);
        Assert.Equal("二层电力平面图", sheets[1].Name);
    }

    [Fact]
    public void SheetIdForPoint_AssignsEntitiesToCorrectSheet()
    {
        var sheets = SheetRegistryBuilder.Build(TwoSheetDoc());
        Assert.Equal(sheets[0].SheetId, SheetRegistryBuilder.SheetIdForPoint(sheets, 150, 100));
        Assert.Equal(sheets[1].SheetId, SheetRegistryBuilder.SheetIdForPoint(sheets, 2150, 100));
        Assert.Null(SheetRegistryBuilder.SheetIdForPoint(sheets, 5000, 5000)); // 框外（远端残元）→null
    }

    [Fact]
    public void SheetIdForPoint_ModelPoint_NeverAttributedToLayoutSheet()
    {
        // R11：模型框 + 纸视口数值 bounds 重叠 → stray 模型点（模型框外但纸 bounds 内）
        // 归属须 null（模型点只归模型幅面），不得误归 "L…"（纸面坐标混入模型归属）。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        var ins = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
        doc.Entities.Add(ins);
        // 纸视口 Center(800,350) 1000×700 → 纸 bounds (300,0)-(1300,700)，与模型框 (0,0)-(1000,700) 数值重叠
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(800, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200,
        });
        var sheets = SheetRegistryBuilder.Build(doc);
        Assert.Equal(2, sheets.Count); // 模型幅面 + 布局幅面
        var modelSheet = sheets[0];
        Assert.Null(modelSheet.LayoutName);
        Assert.StartsWith("L", sheets[1].SheetId);
        // 框内模型点 → 模型幅面（不被布局幅面抢占）
        Assert.Equal(modelSheet.SheetId, SheetRegistryBuilder.SheetIdForPoint(sheets, 500, 350));
        // stray 模型点 (1200,350)：模型框外（x>1000）但纸 bounds 内（300..1300）→ null 非 "L…"
        Assert.Null(SheetRegistryBuilder.SheetIdForPoint(sheets, 1200, 350));
    }

    [Fact]
    public void Build_NoFrame_SingleDefaultSheet()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) });
        var sheets = SheetRegistryBuilder.Build(doc);
        Assert.Single(sheets);
        Assert.Equal("S0", sheets[0].SheetId);
    }

    [Fact]
    public void Build_DuplicateAttributeTag_DoesNotThrow_LastValueWins()
    {
        // 真实图纸同 Tag 多属性不罕见：Build 须诚实降级（后值覆盖）而非抛 ArgumentException
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        var ins = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
        ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01b" });
        ins.Attributes.Add(new AttributeEntity { Tag = "比例", Value = "1:100" });
        doc.Entities.Add(ins);
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(500, 650, 0), Value = "一层电力平面图", Height = 30, Layer = new Layer("图框") });

        var sheets = SheetRegistryBuilder.Build(doc); // 不抛
        Assert.Single(sheets);
        Assert.Equal("Z-E01b", sheets[0].Number); // 后值覆盖
        Assert.Equal("Z-E01b", sheets[0].TitleAttributes["图号"]);
        Assert.Equal("1:100", sheets[0].Scale);
        Assert.Equal("一层电力平面图", sheets[0].Name);
    }

    [Fact]
    public void Session_Sheets_InvalidatedOnRestoreState()
    {
        // 自审补强：Sheets 懒加载缓存须在 RestoreState 整体替换文档后失效重建（undo 场景）
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-sheet-{Guid.NewGuid():N}.dxf");
        try
        {
            using (var fs = File.Create(path)) ACadSharp.IO.DxfWriter.Write(fs, TwoSheetDoc());
            var session = DrawingSession.Load(path);
            Assert.Equal(2, session.Sheets.Count);
            var single = new CadDocument();
            single.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) });
            using var ms = new MemoryStream();
            ACadSharp.IO.DxfWriter.Write(ms, single);
            session.RestoreState(ms.ToArray());
            Assert.Single(session.Sheets);
            Assert.Equal("S0", session.Sheets[0].SheetId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Session_Sheets_RebuiltAfterInPlaceFrameAdd()
    {
        // T4 接住 T1 minor：handler 就地编辑 Doc（加图框）后 Sheets 缓存不得陈旧；
        // 指纹未变时复用缓存（同一实例），变化则重建
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-sheet-{Guid.NewGuid():N}.dxf");
        try
        {
            var doc = new CadDocument();
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) });
            using (var fs = File.Create(path)) ACadSharp.IO.DxfWriter.Write(fs, doc);
            var session = DrawingSession.Load(path);
            Assert.Equal("S0", Assert.Single(session.Sheets).SheetId); // 无图框→默认幅面
            Assert.Same(session.Sheets, session.Sheets); // 指纹未变→缓存命中

            // 就地加图框（不 Reload/RestoreState）
            var frame = new BlockRecord("院标准图框_A0");
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
            session.Doc.BlockRecords.Add(frame);
            var ins = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
            session.Doc.Entities.Add(ins);

            var sheets = session.Sheets;
            Assert.Equal(ins.Handle.ToString(), Assert.Single(sheets).SheetId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Session_Sheets_RebuiltAfterFrameMove()
    {
        // 图框就地移动（句柄集/实体计数不变）→ 指纹含插入点 → bounds 跟随更新
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-sheet-{Guid.NewGuid():N}.dxf");
        try
        {
            using (var fs = File.Create(path)) ACadSharp.IO.DxfWriter.Write(fs, TwoSheetDoc());
            var session = DrawingSession.Load(path);
            Assert.Equal(0, session.Sheets[0].FrameBounds.MinX, 6);
            var frameIns = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "院标准图框_A0");
            frameIns.InsertPoint = new XYZ(frameIns.InsertPoint.X + 500, frameIns.InsertPoint.Y, 0);
            Assert.Equal(500, session.Sheets[0].FrameBounds.MinX, 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Build_ArcOnlyFrame_BoundsCoverArc_NotInverted()
    {
        // T2 收口：纯 Arc 图框（PointsOf 旧版无 Arc 分支）不得产生倒置哨兵 bounds
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_Arc");
        frame.Entities.Add(new Arc { Center = new XYZ(500, 350, 0), Radius = 300, StartAngle = 0, EndAngle = 2 * Math.PI });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        var sheets = SheetRegistryBuilder.Build(doc);
        var b = Assert.Single(sheets).FrameBounds;
        Assert.True(b.MinX <= b.MaxX && b.MinY <= b.MaxY);
        Assert.Equal(200, b.MinX, 6);
        Assert.Equal(50, b.MinY, 6);
        Assert.Equal(800, b.MaxX, 6);
        Assert.Equal(650, b.MaxY, 6);
    }

    [Fact]
    public void Build_EmptyFrameBlock_FallbackUnitBoundsAtInsertPoint()
    {
        // T2 收口：空几何图框倒置哨兵（minX>maxX）→ 回退插入点单位框（±1）
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_空");
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(100, 200, 0) });
        var b = Assert.Single(SheetRegistryBuilder.Build(doc)).FrameBounds;
        Assert.Equal(99, b.MinX, 6);
        Assert.Equal(199, b.MinY, 6);
        Assert.Equal(101, b.MaxX, 6);
        Assert.Equal(201, b.MaxY, 6);
    }

    [Fact]
    public void Build_NestedInsertFrame_ExpandsNestedGeometry()
    {
        // T2 收口：图框块内嵌套 Insert 递归展开（InsertAffine+Compose），scale 2 → bounds 翻倍
        var doc = new CadDocument();
        var inner = new BlockRecord("INNER");
        inner.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(inner);
        var frame = new BlockRecord("院标准图框_嵌套");
        frame.Entities.Add(new Insert(inner) { InsertPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0), XScale = 2, YScale = 2 });
        var b = Assert.Single(SheetRegistryBuilder.Build(doc)).FrameBounds;
        Assert.Equal(0, b.MinX, 6);
        Assert.Equal(0, b.MinY, 6);
        Assert.Equal(2000, b.MaxX, 6);
        Assert.Equal(1400, b.MaxY, 6);
    }

    [Fact]
    public void Build_Polyline2DFrame_BoundsFromVertices()
    {
        // T2 收口：Polyline2D 顶点参与图框 bounds（旧版 PointsOf 无此分支→倒置哨兵）
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_P2");
        var p2 = new Polyline2D { IsClosed = true };
        p2.Vertices.Add(new Vertex2D { Location = new XYZ(0, 0, 0) });
        p2.Vertices.Add(new Vertex2D { Location = new XYZ(1000, 0, 0) });
        p2.Vertices.Add(new Vertex2D { Location = new XYZ(1000, 700, 0) });
        p2.Vertices.Add(new Vertex2D { Location = new XYZ(0, 700, 0) });
        frame.Entities.Add(p2);
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        var b = Assert.Single(SheetRegistryBuilder.Build(doc)).FrameBounds;
        Assert.Equal(0, b.MinX, 6);
        Assert.Equal(0, b.MinY, 6);
        Assert.Equal(1000, b.MaxX, 6);
        Assert.Equal(700, b.MaxY, 6);
    }

    [SkippableFact]
    public void RealDrawing_NineSheets_NamesAndAttributes()
    {
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native\电施（强电）-2#厂房电力+普通照明平面图.dwg";
        Skip.If(!File.Exists(path), "真实图纸不在本机（仓库不携带），跳过");
        using var fs = File.OpenRead(path);
        var doc = new ACadSharp.IO.DwgReader(fs).Read();
        var sheets = SheetRegistryBuilder.Build(doc);
        Assert.Equal(9, sheets.Count);
        Assert.Contains(sheets, s => s.Name == "一层电力平面图");
        Assert.Contains(sheets, s => s.Name == "屋顶层电力平面图");
        Assert.Contains(sheets, s => s.Name == "四层普通照明平面图");
        Assert.All(sheets, s => Assert.Equal("2026.09", s.Date));
        Assert.All(sheets, s => Assert.Equal("01", s.TitleAttributes["版本号"]));
    }

    // ===== T5：布局视口幅面登记 =====

    /// <summary>模型线 + 纸空间布局（withViewport=true 挂真实视口；false 仅空布局）。
    /// 探针核定：new Layout(name) 自动建关联纸空间块；AddViewport 自动附加纸边界视口
    /// （Id=1 RepresentsPaper=true ViewHeight=0）+ 真实视口（Id=2 RepresentsPaper=false）。</summary>
    private static CadDocument LayoutDoc(bool withViewport)
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        if (withViewport)
        {
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0),
                Width = 1000,
                Height = 700,
                ViewTarget = new XYZ(0, 0, 0),
                ViewHeight = 200,
            });
        }
        return doc;
    }

    [Fact]
    public void Build_PaperLayoutWithViewport_RegistersLayoutSheet()
    {
        var doc = LayoutDoc(withViewport: true);
        var sheets = SheetRegistryBuilder.Build(doc);
        // 模型幅面在前（无图框→S0），布局幅面在后；纸边界视口（RepresentsPaper）不登记
        Assert.Equal(2, sheets.Count);
        Assert.Equal("S0", sheets[0].SheetId);
        Assert.Null(sheets[0].LayoutName);
        var layoutSheet = sheets[1];
        Assert.Equal("布局1", layoutSheet.LayoutName);
        Assert.Equal("布局1", layoutSheet.Name);
        Assert.StartsWith("L", layoutSheet.SheetId);
        // FrameBounds=视口纸面 bounds：Center(500,350) ± Width/2(500), Height/2(350)
        Assert.Equal(0, layoutSheet.FrameBounds.MinX, 6);
        Assert.Equal(0, layoutSheet.FrameBounds.MinY, 6);
        Assert.Equal(1000, layoutSheet.FrameBounds.MaxX, 6);
        Assert.Equal(700, layoutSheet.FrameBounds.MaxY, 6);
        // 模型区域记录于 TitleAttributes
        Assert.Equal("0,0", layoutSheet.TitleAttributes["viewTarget"]);
        Assert.Equal("200", layoutSheet.TitleAttributes["viewHeight"]);
        var vp = doc.Layouts.First(l => l.Name == "布局1").AssociatedBlock!.Entities
            .OfType<Viewport>().First(v => !v.RepresentsPaper);
        Assert.Equal(vp.Handle.ToString(), layoutSheet.TitleAttributes["viewportHandle"]);
    }

    [Fact]
    public void Build_EmptyPaperLayout_NotRegistered_Honest()
    {
        // 空布局（无真实视口）不登记——诚实注记证据缺口；默认 Layout1 仅纸边界视口亦不登记
        var sheets = SheetRegistryBuilder.Build(LayoutDoc(withViewport: false));
        Assert.Single(sheets);
        Assert.Equal("S0", sheets[0].SheetId);
        Assert.All(sheets, s => Assert.Null(s.LayoutName));
    }

    [Fact]
    public void Build_PaperBoundaryViewport_OnlyRealViewportRegistered()
    {
        // AddViewport 自动附加纸边界视口（RepresentsPaper=true）——仅真实视口登记一个布局幅面
        var doc = LayoutDoc(withViewport: true);
        var layout = doc.Layouts.First(l => l.Name == "布局1");
        Assert.Equal(2, layout.AssociatedBlock!.Entities.OfType<Viewport>().Count()); // 纸边界 + 真实
        var layoutSheets = SheetRegistryBuilder.Build(doc).Where(s => s.LayoutName is not null).ToList();
        Assert.Single(layoutSheets);
    }

    [Fact]
    public void Build_LayoutSheetRoundtripsDxf()
    {
        // 布局幅面经 DXF write/load roundtrip 仍登记（视口属性 survives）
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            var layout = new Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0),
                Width = 1000,
                Height = 700,
                ViewTarget = new XYZ(0, 0, 0),
                ViewHeight = 200,
            });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var layoutSheet = Assert.Single(session.Sheets, s => s.LayoutName == "布局1");
            Assert.Equal("200", layoutSheet.TitleAttributes["viewHeight"]);
            Assert.Equal(1000, layoutSheet.FrameBounds.MaxX, 6);
        }
        finally { File.Delete(path); }
    }

    // ===== T5：T4 minor 收口（指纹边界）=====

    [Fact]
    public void Session_Sheets_RebuiltAfterNoFrameMove()
    {
        // 无框路径：就地移动实体（句柄集/计数不变）→ 指纹含几何极值 → ModelBounds 跟随更新
        string path = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) }));
        try
        {
            var session = DrawingSession.Load(path);
            Assert.Equal(10, session.Sheets[0].FrameBounds.MaxX, 6);
            Assert.Same(session.Sheets, session.Sheets); // 几何未变→缓存命中
            var line = session.Doc.Entities.OfType<Line>().First();
            line.EndPoint = new XYZ(50, 50, 0);
            Assert.Equal(50, session.Sheets[0].FrameBounds.MaxX, 6); // 极值入指纹→重建
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Session_Sheets_NameTextLayerChange_UpdatesName()
    {
        // 幅面名文字改层（图框→0，脱离名称层过滤）→ 指纹含文字图层 → 名称重算（回退默认）
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var frame = new BlockRecord("院标准图框_A0");
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
            doc.BlockRecords.Add(frame);
            var ins = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(500, 650, 0), Value = "一层电力平面图", Height = 30, Layer = new Layer("图框") });
        });
        try
        {
            var session = DrawingSession.Load(path);
            Assert.Equal("一层电力平面图", session.Sheets[0].Name);
            var text = session.Doc.Entities.OfType<TextEntity>().First();
            text.Layer = new Layer("0"); // 脱离名称层过滤
            Assert.Equal("幅面1", session.Sheets[0].Name); // 指纹含文字图层→重建→名称回退
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void RealDrawing_LayoutsRegistered_EmptyLayoutHonest()
    {
        // 真实图：Layouts=2（Model+布局1 空）→ 布局幅面 0 个（空布局不登记）；模型幅面 9
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native\电施（强电）-2#厂房电力+普通照明平面图.dwg";
        Skip.If(!File.Exists(path), "真实图纸不在本机（仓库不携带），跳过");
        using var fs = File.OpenRead(path);
        var doc = new ACadSharp.IO.DwgReader(fs).Read();
        var sheets = SheetRegistryBuilder.Build(doc);
        Assert.Equal(9, sheets.Count);
        Assert.All(sheets, s => Assert.Null(s.LayoutName));
    }
}
