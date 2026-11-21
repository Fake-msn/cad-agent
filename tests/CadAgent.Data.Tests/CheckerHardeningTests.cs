using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-a T4 检查器硬化：未知 mode（非空）→ ArgumentException（引擎隔离记账）；
/// 标高候选窗扩展 ^(±|[+-])?\d+(\.\d+)?$（0 位/4+ 位小数→decimals High）；
/// sign 模式对整数值也查 +/± 前缀（控制者裁定）；CheckParams null 元素跳过（无幻影 0）、
/// 容差比较 &lt;= ToleranceMm + 1e-9。
/// 简报适配：CheckParams.ToDoubleArray → 既有 internal 方法 ToDoubleList
/// （CadAgent.Data.csproj InternalsVisibleTo CadAgent.Data.Tests 已具备，意图不变）。
/// </summary>
public class CheckerHardeningTests : IDisposable
{
    private readonly string _path;
    private readonly SemanticOverlay _overlay = new();
    public CheckerHardeningTests()
    {
        _path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "3", Height = 5 });      // 0位小数
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "3.0000", Height = 5 }); // 4位
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "3.000", Height = 5 });  // 合规
        });
        var session = DrawingSession.Load(_path);
        foreach (var e in session.Summarize().Entities)
            _overlay.UpsertAnnotation(new SemanticAnnotation(e.Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
    }
    public void Dispose() => File.Delete(_path);

    private static Rule ElevRule(string mode) => new("R-E",
        new RuleSource("GB/T 50001-2017", "11.8.4", "摘录", "source-text"), "应",
        RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("elevation-format", new Dictionary<string, object> { ["mode"] = mode }), "建议");

    [Fact]
    public void Elevation_ZeroAndFourDecimals_Flagged()
    {
        var issues = new ElevationChecker().Check(ElevRule("decimals"), DrawingSession.Load(_path), _overlay);
        Assert.Contains(issues, i => i.Message.Contains("\"3\""));
        Assert.Contains(issues, i => i.Message.Contains("3.0000"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("3.000\""));
    }

    [Fact]
    public void Elevation_UnknownMode_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ElevationChecker().Check(ElevRule("bogus"), DrawingSession.Load(_path), _overlay));
    }

    [Fact]
    public void LayerName_UnknownMode_Throws()
    {
        var rule = new Rule("R-L", new RuleSource("GB/T 50001-2017", "13.0.1", "摘录", "source-text"),
            "不得", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = "bogus" }), "建议");
        Assert.Throws<ArgumentException>(() =>
            new LayerNameChecker().Check(rule, DrawingSession.Load(_path), new SemanticOverlay()));
    }

    [Fact]
    public void CheckParams_NullElement_Skipped_NoPhantomZero()
    {
        // 简报 ToDoubleArray 适配为既有 ToDoubleList（internal + InternalsVisibleTo）。
        var values = CheckParams.ToDoubleList(new List<object> { 3.5, null!, 5 });
        Assert.Equal(new[] { 3.5, 5 }, values);
    }

    // ---- 以下为裁定补充覆盖（brief rulings）----

    [Fact]
    public void Elevation_ZeroAndFourDecimals_HighSeverity_ThirdDecimalMessage()
    {
        var issues = new ElevationChecker().Check(ElevRule("decimals"), DrawingSession.Load(_path), _overlay);
        var zero = issues.Single(i => i.Message.Contains("\"3\""));
        var four = issues.Single(i => i.Message.Contains("\"3.0000\""));
        Assert.Equal(RuleSeverity.High, zero.Severity);   // 0 位不降级（2 位才 Low+总平面）
        Assert.Equal(RuleSeverity.High, four.Severity);   // 4+ 位不降级
        Assert.Contains("应注写到小数点后第三位", zero.Message);
        Assert.Contains("应注写到小数点后第三位", four.Message);
    }

    [Fact]
    public void Elevation_SignMode_IntegerValues_PrefixChecked()
    {
        // 控制者裁定：sign 模式对整数值也查 +/± 前缀——"+3" 违规；"-3" 带负号合规（11.8.5）；
        // "3" 正数无符号合规；"±3" ± 仅用于零点 → 违规。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "+3", Height = 5 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "-3", Height = 5 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "3", Height = 5 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "±3", Height = 5 });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var overlay = new SemanticOverlay();
            foreach (var e in session.Summarize().Entities)
                overlay.UpsertAnnotation(new SemanticAnnotation(e.Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
            var issues = new ElevationChecker().Check(ElevRule("sign"), session, overlay);
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("\"+3\""));
            Assert.Contains(issues, i => i.Message.Contains("\"±3\""));
            Assert.DoesNotContain(issues, i => i.Message.Contains("\"-3\""));
            Assert.DoesNotContain(issues, i => i.Message.Contains("\"3\""));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Elevation_EmptyMode_StillChecksBoth()
    {
        // 空 mode 行为不变：小数位+符号全查。"+3.00" → 2 位 Low + 正数带 + High。
        string path = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "+3.00", Height = 5 }));
        try
        {
            var session = DrawingSession.Load(path);
            var overlay = new SemanticOverlay();
            foreach (var e in session.Summarize().Entities)
                overlay.UpsertAnnotation(new SemanticAnnotation(e.Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
            var issues = new ElevationChecker().Check(ElevRule(""), session, overlay);
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Severity == RuleSeverity.Low && i.Message.Contains("总平面"));
            Assert.Contains(issues, i => i.Severity == RuleSeverity.High && i.Message.Contains("\"+\""));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LayerName_EmptyMode_ReturnsEmpty_Unchanged()
    {
        // 空 mode 行为不变（不抛异常、不检查）；仅非空未知 mode 才抛。
        var rule = new Rule("R-L", new RuleSource("GB/T 50001-2017", "13.0.1", "摘录", "source-text"),
            "不得", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = "" }), "建议");
        Assert.Empty(new LayerNameChecker().Check(rule, DrawingSession.Load(_path), new SemanticOverlay()));
    }

    [Fact]
    public void CheckParams_InSeries_ToleranceBoundary_Epsilon()
    {
        // 5.03 - 5.02 的 double 差为 0.010000000000000675 > 0.01——epsilon 1e-9 吸收浮点边界。
        Assert.True(CheckParams.InSeries(5.03, new[] { 5.02 }));
        Assert.False(CheckParams.InSeries(5.04, new[] { 5.02 })); // 真超差仍违规
    }
}
