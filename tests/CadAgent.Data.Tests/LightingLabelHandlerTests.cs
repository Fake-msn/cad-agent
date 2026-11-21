using System.Text.Json;
using ACadSharp;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3e T1：label-luminaires 第八处理器——设备索引符号（JGJ/T 244-2011 3.6.6-4：
/// 正六边形+水平内径线、细实线、长轴8~12mm、内注编号+品种代号）。
/// 声明 Lighting 语义需求（Lighting 类首次实战）；编号确定性（Handle 升序）。
/// </summary>
public class LightingLabelHandlerTests : IDisposable
{
    private readonly string _path;
    public LightingLabelHandlerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("LUM");
        block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
        doc.BlockRecords.Add(block);
        var other = new BlockRecord("OTHER");
        other.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, -100), EndPoint = (XYZ)new XY(0, 100) });
        doc.BlockRecords.Add(other);
        doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(0, 0) });
        doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(2000, 0) });
    });
    public void Dispose() => File.Delete(_path);

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Label_TwoInserts_NumbersAndHexagons()
    {
        var s = DrawingSession.Load(_path);
        var r = new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L"}"""));
        Assert.True(r.Ok);
        var texts = s.Doc.Entities.OfType<TextEntity>().OrderBy(t => t.Handle).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(new[] { "L1", "L2" }, texts.Select(t => t.Value));
        Assert.All(texts, t => Assert.Equal(2.5, t.Height));
        // 每符号 6 边 + 1 水平内径线 = 7 Line × 2
        Assert.Equal(14, s.Doc.Entities.OfType<Line>().Count());
        Assert.All(s.Doc.Entities.OfType<Line>(), l => Assert.Equal(LineWeightType.W25, l.LineWeight));
        Assert.Equal(texts[0].Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void Label_HexagonLongAxis10_HorizontalDiameter()
    {
        var s = DrawingSession.Load(_path);
        new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L","symbolSize":10}"""));
        // 第一个 Insert(0,0)→符号中心(10,10)；水平内径线端点 (5,10)-(15,10)，长轴=10
        var diameter = s.Doc.Entities.OfType<Line>()
            .First(l => l.StartPoint.Y == 10 && l.EndPoint.Y == 10);
        Assert.Equal(5, diameter.StartPoint.X, 3);
        Assert.Equal(15, diameter.EndPoint.X, 3);
    }

    [Fact]
    public void Label_SizeOutOfRange_Fails()
    {
        var s = DrawingSession.Load(_path);
        Assert.False(new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L","symbolSize":13}""")).Ok);
        Assert.False(new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L","symbolSize":7}""")).Ok);
        Assert.Empty(s.Doc.Entities.OfType<TextEntity>());
    }

    [Fact]
    public void Label_BlockFilter_OnlyMatching()
    {
        var s = DrawingSession.Load(_path);
        s.Doc.Entities.Add(new Insert(s.Doc.BlockRecords.First(b => b.Name == "OTHER")) { InsertPoint = (XYZ)new XY(5000, 0) });
        var r = new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L","block":"LUM"}"""));
        Assert.True(r.Ok);
        Assert.Equal(2, s.Doc.Entities.OfType<TextEntity>().Count());
    }

    [Fact]
    public void Label_StartIndex()
    {
        var s = DrawingSession.Load(_path);
        new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L","start":5}"""));
        Assert.Equal(new[] { "L5", "L6" },
            s.Doc.Entities.OfType<TextEntity>().OrderBy(t => t.Handle).Select(t => t.Value));
    }

    [Fact]
    public void Label_MissingPrefix_Fails()
    {
        var s = DrawingSession.Load(_path);
        Assert.False(new LabelLuminairesHandler().Apply(s, P("""{"symbolSize":10}""")).Ok);
        Assert.False(new LabelLuminairesHandler().Apply(s, P("""{"prefix":""}""")).Ok);
    }

    [Fact]
    public void Label_NoInserts_Fails()
    {
        string empty = DxfFixture.CreateTempDxf(_ => { });
        try
        {
            var s = DrawingSession.Load(empty);
            var r = new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L"}"""));
            Assert.False(r.Ok);
            Assert.Contains("无灯具", r.Error);
        }
        finally { File.Delete(empty); }
    }

    [Fact]
    public void Label_OptionalLayer_AppliedToSymbolEntities()
    {
        var s = DrawingSession.Load(_path);
        new LabelLuminairesHandler().Apply(s, P("""{"prefix":"L","layer":"E-LITE-IDX"}"""));
        Assert.All(s.Doc.Entities.OfType<TextEntity>(), t => Assert.Equal("E-LITE-IDX", t.Layer?.Name));
    }

    [Fact]
    public void Handler_DeclaresLightingSemantics()
    {
        var h = new LabelLuminairesHandler();
        Assert.Equal("label-luminaires", h.Kind);
        Assert.Contains(SemanticClass.Lighting, h.RequiredSemantics);
    }

    [Fact]
    public void Registry_ContainsLabelLuminaires()
    {
        Assert.Contains("label-luminaires", HandlerRegistry.All().Select(h => h.Kind));
    }
}
