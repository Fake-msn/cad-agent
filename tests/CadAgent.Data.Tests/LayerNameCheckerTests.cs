using CadAgent.Core;
using CadAgent.Data.Checks;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class LayerNameCheckerTests : IDisposable
{
    private readonly string _path;
    public LayerNameCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("W-墙体"));      // 汉英混用违规
        doc.Layers.Add(new Layer("W-WALL"));      // 合规英文
        doc.Layers.Add(new Layer("建筑-墙体-新建")); // 合规汉字3段
        doc.Layers.Add(new Layer("单层"));         // 结构违规：1段
    });
    public void Dispose() => File.Delete(_path);

    private static Rule Make(string mode) =>
        new($"R-50001-13.0.{(mode == "charset" ? "1" : "2")}",
            new RuleSource("GB/T 50001-2017", mode == "charset" ? "13.0.1" : "13.0.2", "摘录", "source-text"),
            "不得／宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = mode }), "建议");

    [Fact]
    public void Charset_MixedHanEnglish_Flagged()
    {
        var issues = new LayerNameChecker().Check(Make("charset"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Single(issues);
        Assert.Contains("W-墙体", issues[0].Message);
    }

    [Fact]
    public void Structure_SegmentCount_Flagged()
    {
        var issues = new LayerNameChecker().Check(Make("structure"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Contains(issues, i => i.Message.Contains("单层"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("W-WALL"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("建筑-墙体-新建"));
    }

    [Fact]
    public void Charset_DigitsNotEnglish_NoIssue()
    {
        string path = DxfFixture.CreateTempDxf(doc => doc.Layers.Add(new Layer("墙体2")));
        try
        {
            var issues = new LayerNameChecker().Check(Make("charset"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Empty(issues); // 汉字+数字不算汉英混用
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultLayer_Zero_Skipped()
    {
        string path = DxfFixture.CreateTempDxf(_ => { }); // 仅默认层
        try
        {
            var session = DrawingSession.Load(path);
            Assert.Empty(new LayerNameChecker().Check(Make("charset"), session, new SemanticOverlay()));
            var structure = new LayerNameChecker().Check(Make("structure"), session, new SemanticOverlay());
            Assert.DoesNotContain(structure, i => i.Message.Contains("图层 \"0\"")); // "0" 1段但跳过
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Structure_BadSegments_Flagged()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("W-WALL_1"));     // 段含下划线：字符集违规
            doc.Layers.Add(new Layer("A--B"));         // 空段违规
            doc.Layers.Add(new Layer("A-B-C-D-E-F"));  // 6段：段数越界
        });
        try
        {
            var issues = new LayerNameChecker().Check(Make("structure"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Contains(issues, i => i.Message.Contains("W-WALL_1"));
            Assert.Contains(issues, i => i.Message.Contains("A--B"));
            Assert.Contains(issues, i => i.Message.Contains("A-B-C-D-E-F"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownMode_ReturnsEmpty()
    {
        var issues = new LayerNameChecker().Check(Make("bogus"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Empty(issues); // 未知模式→空列表（检查器隔离，不抛异常）
    }

    [Fact]
    public void DetectionBasis_IsDirectLayerName()
    {
        var issues = new LayerNameChecker().Check(Make("charset"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal("图层名直接检查", issues[0].DetectionBasis);
    }
}