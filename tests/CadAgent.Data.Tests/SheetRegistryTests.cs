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
