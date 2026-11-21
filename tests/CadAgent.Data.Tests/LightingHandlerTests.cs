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

    [Fact]
    public void Handler_DeclaresRoomSemantics()
    {
        var h = new LayoutLightingHandler();
        Assert.Equal("layout-lighting", h.Kind);
        Assert.Contains(SemanticClass.Room, h.RequiredSemantics);
    }

    [Fact]
    public void Registry_ContainsSevenKinds()
    {
        Assert.Contains("layout-lighting", HandlerRegistry.All().Select(h => h.Kind));
        Assert.Equal(7, HandlerRegistry.All().Count);
    }
}
