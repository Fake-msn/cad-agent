using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;

namespace CadAgent.Data.Tests;

public class TextLineweightCheckerTests : IDisposable
{
    private readonly string _path;
    public TextLineweightCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "合规", Height = 5 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "违规", Height = 4.2 });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0) }); // 默认线宽 ByLayer→跳过
    });
    public void Dispose() => File.Delete(_path);

    private static Rule HeightRule() => new("R-50001-5.0.2",
        new RuleSource("GB/T 50001-2017", "5.0.2", "文字的字高，应从表 5.0.2 中选用。", "source-text"),
        "应／宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("text-height-series", new Dictionary<string, object>
        {
            ["hanVector"] = new List<object> { 3.5, 5, 7, 10, 14, 20 },
            ["other"] = new List<object> { 3, 4, 6, 8, 10, 14, 20 }
        }), "字高改选系列值");

    private static Rule LineweightRule() => new("R-50001-4.0.1",
        new RuleSource("GB/T 50001-2017", "4.0.1", "图线的基本线宽 b……", "source-text"),
        "宜／应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("lineweight-group", new Dictionary<string, object>
        {
            ["groups"] = new List<object> { 1.4, 1.0, 0.7, 0.5, 0.35, 0.25, 0.18, 0.13 }
        }), "线宽改选线宽组值");

    [Fact]
    public void TextHeight_OffSeries_Flagged_InSeries_Pass()
    {
        var session = DrawingSession.Load(_path);
        var issues = new TextHeightChecker().Check(HeightRule(), session, new SemanticOverlay());
        Assert.Single(issues);
        Assert.Contains("4.2", issues[0].Message);
        Assert.Contains("14.0.4", issues[0].DetectionBasis);
    }

    [Fact]
    public void Lineweight_ByLayer_Skipped_NoIssue()
    {
        var rule = new Rule("R-50001-4.0.1",
            new RuleSource("GB/T 50001-2017", "4.0.1", "图线的基本线宽 b……", "source-text"),
            "宜／应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("lineweight-group", new Dictionary<string, object>
            {
                ["groups"] = new List<object> { 1.4, 1.0, 0.7, 0.5, 0.35, 0.25, 0.18, 0.13 }
            }), "线宽改选线宽组值");
        var session = DrawingSession.Load(_path);
        var issues = new LineweightChecker().Check(rule, session, new SemanticOverlay());
        Assert.Empty(issues); // ByLayer 不报
    }

    [Fact]
    public void Lineweight_ExplicitOffGroup_Flagged_InGroup_Pass()
    {
        // ACadSharp 3.8.35 核实：Entity.LineWeight 为 LineWeightType 枚举（short），
        // W35=0.35mm（组内→通过），W40=0.40mm（组外→违规）；DXF 370 码往返保留显式值。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line
            {
                StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0),
                LineWeight = LineWeightType.W35
            });
            doc.Entities.Add(new Line
            {
                StartPoint = (XYZ)new XY(0, 10), EndPoint = (XYZ)new XY(100, 10),
                LineWeight = LineWeightType.W40
            });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var issues = new LineweightChecker().Check(LineweightRule(), session, new SemanticOverlay());
            Assert.Single(issues);
            Assert.Contains("0.4", issues[0].Message);
            Assert.Contains("4.0.1", issues[0].Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TextHeight_MText_OffSeries_Flagged()
    {
        // MText : Entity（非 TextEntity），两者都有 Height——独立分支处理。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new MText { InsertPoint = (XYZ)new XY(0, 0), Value = "多行合规", Height = 5 });
            doc.Entities.Add(new MText { InsertPoint = (XYZ)new XY(10, 0), Value = "多行违规", Height = 4.2 });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var issues = new TextHeightChecker().Check(HeightRule(), session, new SemanticOverlay());
            Assert.Single(issues);
            Assert.Contains("4.2", issues[0].Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TextHeight_Tolerance_0p01mm()
    {
        // 容差 0.01mm：5.009 与系列值 5 差 0.009→通过；5.02 差 0.02→违规。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "容差内", Height = 5.009 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "容差外", Height = 5.02 });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var issues = new TextHeightChecker().Check(HeightRule(), session, new SemanticOverlay());
            Assert.Single(issues);
            Assert.Contains("5.02", issues[0].Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
