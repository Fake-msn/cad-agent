using ACadSharp;
using ACadSharp.Entities;
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
}
