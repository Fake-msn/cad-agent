using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;

namespace CadAgent.Data.Tests;

public class AxisElevationCheckerTests : IDisposable
{
    private readonly string _path;
    private readonly SemanticOverlay _overlay = new();
    public AxisElevationCheckerTests()
    {
        _path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "I", Height = 5 });   // 违规轴号
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "A", Height = 5 });  // 合规轴号
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "+3.000", Height = 5 }); // 正数带+违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "-0.600", Height = 5 }); // 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "3.00", Height = 5 });   // 2位小数
        });
        var session = DrawingSession.Load(_path);
        var es = session.Summarize().Entities;
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[0].Handle, SemanticClass.Axis, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[1].Handle, SemanticClass.Axis, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[2].Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[3].Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[4].Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
    }
    public void Dispose() => File.Delete(_path);

    private static Rule Make(string id, string type, string clause) =>
        Make(id, type, clause, new Dictionary<string, object>());

    private static Rule Make(string id, string type, string clause, Dictionary<string, object> checkParams) =>
        new(id, new RuleSource("GB/T 50001-2017", clause, "摘录", "source-text"),
            "应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec(type, checkParams), "建议");

    [Fact]
    public void AxisLetter_IOZ_Flagged_ValidPass()
    {
        var issues = new AxisLetterChecker().Check(Make("R-50001-8.0.4", "axis-letter", "8.0.4"),
            DrawingSession.Load(_path), _overlay);
        Assert.Single(issues);
        Assert.Contains("I", issues[0].Message);
        Assert.Contains("HumanConfirmed", issues[0].DetectionBasis);
    }

    [Fact]
    public void Elevation_PlusSign_Flagged()
    {
        var issues = new ElevationChecker().Check(Make("R-50001-11.8.5", "elevation-format", "11.8.5"),
            DrawingSession.Load(_path), _overlay);
        Assert.Contains(issues, i => i.Message.Contains("+3.000"));
    }

    [Fact]
    public void Elevation_TwoDecimals_LowSeverityNote()
    {
        var issues = new ElevationChecker().Check(Make("R-50001-11.8.4", "elevation-format", "11.8.4"),
            DrawingSession.Load(_path), _overlay);
        // 简报原谓词 Contains("3.00") 存在子串碰撞："+3.000" 亦含 "3.00"，
        // check-all 模式下两条 issue 均命中致 Single 抛异常。消息中值以引号包裹，
        // 谓词收紧为带引号的 "3.00"（"+3.000" 中 3 前为 + 号，不命中）。
        var two = issues.Single(i => i.Message.Contains("\"3.00\""));
        Assert.Equal(RuleSeverity.Low, two.Severity);
        Assert.Contains("总平面", two.Message);
    }

    [Fact]
    public void AxisLetter_NoOverlay_Empty()
    {
        var issues = new AxisLetterChecker().Check(Make("R-50001-8.0.4", "axis-letter", "8.0.4"),
            DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Empty(issues);
    }

    [Fact]
    public void Elevation_DecimalsMode_SignNotChecked()
    {
        var rule = Make("R-50001-11.8.4", "elevation-format", "11.8.4",
            new Dictionary<string, object> { ["mode"] = "decimals" });
        var issues = new ElevationChecker().Check(rule, DrawingSession.Load(_path), _overlay);
        Assert.Single(issues); // 仅 "3.00" 小数位问题；"+3.000" 符号问题不在本模式
        Assert.Contains("3.00", issues[0].Message);
        Assert.DoesNotContain(issues, i => i.Message.Contains("+3.000"));
        Assert.Equal(RuleSeverity.Low, issues[0].Severity);
    }

    [Fact]
    public void Elevation_SignMode_ZeroMustBePlusMinus()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "0.000", Height = 5 });   // 零点未写 ±0.000 → 违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "±0.000", Height = 5 }); // 零点规范写法 → 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "3.00", Height = 5 });   // sign 模式不查小数位
        });
        try
        {
            var session = DrawingSession.Load(path);
            var es = session.Summarize().Entities;
            var overlay = new SemanticOverlay();
            foreach (var e in es)
                overlay.UpsertAnnotation(new SemanticAnnotation(e.Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
            var rule = Make("R-50001-11.8.5", "elevation-format", "11.8.5",
                new Dictionary<string, object> { ["mode"] = "sign" });
            var issues = new ElevationChecker().Check(rule, session, overlay);
            Assert.Single(issues);
            Assert.Contains("0.000", issues[0].Message);
            Assert.Contains("±0.000", issues[0].Message);
            Assert.Contains("HumanConfirmed", issues[0].DetectionBasis);
            Assert.Equal(RuleSeverity.High, issues[0].Severity); // 符号问题不降级
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 最终审查修复 1（控制者裁定）：8.0.4 摘录限定"英文字母作为轴线号时"——
    // 纯数字（8.0.3 阿拉伯数字）、附加轴分数（8.0.6：1/A、分母 0A）、分区编号（8.0.5：1-A）
    // 不是字母轴线号 → 跳过不算违规；含字母的轴线号值仍查（I 禁用、a 小写 → issue）。
    [Fact]
    public void AxisLetter_NumericFractionZoneForms_Skipped_LetterValuesStillChecked()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "1", Height = 5 });    // 8.0.3 数字 → 不查
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "10", Height = 5 });  // 8.0.3 数字 → 不查
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "1/A", Height = 5 }); // 8.0.6 附加轴分数 → 不查
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "0A", Height = 5 });  // 8.0.6 前轴分母形式 → 不查
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "1-A", Height = 5 }); // 8.0.5 分区编号 → 不查
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(50, 0), Value = "I", Height = 5 });   // 禁用字母 → issue
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(60, 0), Value = "a", Height = 5 });   // 小写 → issue
        });
        try
        {
            var session = DrawingSession.Load(path);
            var es = session.Summarize().Entities;
            var overlay = new SemanticOverlay();
            foreach (var e in es)
                overlay.UpsertAnnotation(new SemanticAnnotation(e.Handle, SemanticClass.Axis, Provenance.HumanConfirmed, 1.0, "t"));
            var issues = new AxisLetterChecker().Check(Make("R-50001-8.0.4", "axis-letter", "8.0.4"), session, overlay);
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("\"I\""));
            Assert.Contains(issues, i => i.Message.Contains("\"a\""));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AxisLetter_DoubleFootnote_Valid_Lowercase_Flagged_LowConfidenceIgnored()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "AA", Height = 5 });  // 双大写 → 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "A1", Height = 5 }); // 字母+数字注脚 → 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "b", Height = 5 });  // 小写 → 违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "Z", Height = 5 });  // 禁用字母 → 违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "O", Height = 5 });  // 置信度 0.5 < 0.6 → 不查
        });
        try
        {
            var session = DrawingSession.Load(path);
            var es = session.Summarize().Entities;
            var overlay = new SemanticOverlay();
            for (int i = 0; i < 4; i++)
                overlay.UpsertAnnotation(new SemanticAnnotation(es[i].Handle, SemanticClass.Axis, Provenance.HumanConfirmed, 1.0, "t"));
            overlay.UpsertAnnotation(new SemanticAnnotation(es[4].Handle, SemanticClass.Axis, Provenance.GeometryInference, 0.5, "t"));
            var issues = new AxisLetterChecker().Check(Make("R-50001-8.0.4", "axis-letter", "8.0.4"), session, overlay);
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("\"b\""));
            Assert.Contains(issues, i => i.Message.Contains("\"Z\""));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
