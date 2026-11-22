using System.Text.Json;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3d T2：layout-lighting 第七处理器——确定性网格布灯（x+spacing/2 起步），
/// 声明 Room 语义需求（闸口实战在 Cli 侧）；块不存在/非法参数→失败结果。
/// </summary>
public class LightingHandlerTests : IDisposable
{
    private readonly string _path;
    public LightingHandlerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("LUM");
        block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
        doc.BlockRecords.Add(block);
    });
    public void Dispose() => File.Delete(_path);

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Layout_Grid_2x3_AtSpacing1000()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, P(
            """{"x":0,"y":0,"width":2000,"depth":3000,"spacing":1000,"block":"LUM"}"""));
        Assert.True(r.Ok);
        var inserts = s.Doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(6, inserts.Count);
        Assert.Contains(inserts, i => i.InsertPoint.X == 500 && i.InsertPoint.Y == 500);
        Assert.Contains(inserts, i => i.InsertPoint.X == 1500 && i.InsertPoint.Y == 2500);
        Assert.Equal(inserts[0].Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void Layout_MissingBlock_Fails()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, P(
            """{"x":0,"y":0,"width":2000,"depth":3000,"spacing":1000,"block":"NOPE"}"""));
        Assert.False(r.Ok);
        Assert.Contains("块不存在", r.Error);
        Assert.Empty(s.Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void Layout_NonPositiveSpacing_Fails()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, P(
            """{"x":0,"y":0,"width":2000,"depth":3000,"spacing":0,"block":"LUM"}"""));
        Assert.False(r.Ok);
        Assert.Empty(s.Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void Layout_TooManyPoints_Fails()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, P(
            """{"x":0,"y":0,"width":1000000,"depth":1000000,"spacing":1,"block":"LUM"}"""));
        Assert.False(r.Ok);
        Assert.Contains("超限", r.Error);
        Assert.Empty(s.Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void Layout_OptionalLayer_Applied()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, P(
            """{"x":0,"y":0,"width":1000,"depth":1000,"spacing":1000,"block":"LUM","layer":"E-LITE"}"""));
        Assert.True(r.Ok);
        Assert.All(s.Doc.Entities.OfType<Insert>(),
            i => Assert.Equal("E-LITE", i.Layer?.Name));
    }

    /// <summary>R10 双幅面夹具：S1 (0,0)-(1000,700)、S2 (2000,0)-(3000,700) + LUM 块。</summary>
    private static string TwoSheetPath() => DxfFixture.CreateTempDxf(doc =>
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        for (int i = 0; i < 2; i++)
        {
            var fi = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
            fi.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
            doc.Entities.Add(fi);
        }
        var block = new BlockRecord("LUM");
        block.Entities.Add(new Line { StartPoint = new XYZ(-100, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        doc.BlockRecords.Add(block);
    });

    [Fact]
    public void Layout_SheetId_RegionWithinDeclaredSheet_Ok()
    {
        string path = TwoSheetPath();
        try
        {
            var s = DrawingSession.Load(path);
            string s1 = s.Sheets[0].SheetId;
            // 区域 [100,900]×[100,600] 含于 S1 (0,0)-(1000,700)
            var r = new LayoutLightingHandler().Apply(s, P($$"""{"x":100,"y":100,"width":800,"depth":500,"spacing":400,"block":"LUM","sheetId":"{{s1}}"}"""));
            Assert.True(r.Ok);
            var inserts = s.Doc.Entities.OfType<Insert>().Where(i => i.Block?.Name == "LUM").ToList();
            Assert.NotEmpty(inserts);
            Assert.All(inserts, i => Assert.True(i.InsertPoint.X >= 0 && i.InsertPoint.X <= 1000
                && i.InsertPoint.Y >= 0 && i.InsertPoint.Y <= 700)); // 全部落 S1
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Layout_SheetId_RegionCrossesSheet_FailsWithBoundsError()
    {
        string path = TwoSheetPath();
        try
        {
            var s = DrawingSession.Load(path);
            string s1 = s.Sheets[0].SheetId;
            // 区域 [500,2500]×[100,500] 跨入 S2 → 超出 S1 bounds → 拒绝（零落图）
            var r = new LayoutLightingHandler().Apply(s, P($$"""{"x":500,"y":100,"width":2000,"depth":400,"spacing":500,"block":"LUM","sheetId":"{{s1}}"}"""));
            Assert.False(r.Ok);
            Assert.Contains("区域超出幅面", r.Error);
            Assert.Contains(s1, r.Error);
            Assert.Empty(s.Doc.Entities.OfType<Insert>().Where(i => i.Block?.Name == "LUM"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Layout_SheetId_UnknownSheet_Fails()
    {
        string path = TwoSheetPath();
        try
        {
            var s = DrawingSession.Load(path);
            var r = new LayoutLightingHandler().Apply(s, P("""{"x":0,"y":0,"width":500,"depth":500,"spacing":250,"block":"LUM","sheetId":"NOPE"}"""));
            Assert.False(r.Ok);
            Assert.Contains("幅面不存在", r.Error);
            Assert.Empty(s.Doc.Entities.OfType<Insert>().Where(i => i.Block?.Name == "LUM"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Handler_DeclaresRoomSemantics()
    {
        var h = new LayoutLightingHandler();
        Assert.Equal("layout-lighting", h.Kind);
        Assert.Contains(SemanticClass.Room, h.RequiredSemantics);
    }

    [Fact]
    public void Registry_ContainsLayoutLighting()
    {
        Assert.Contains("layout-lighting", HandlerRegistry.All().Select(h => h.Kind));
    }
}
