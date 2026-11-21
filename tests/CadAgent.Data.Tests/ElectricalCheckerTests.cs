using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-b 电气规则检查器共享测试类（T3 起）。简报测试逐字保留；
/// 简报适配：补 using CadAgent.Data.Checks（简报代码块遗漏，TextHeightMinChecker 所在命名空间）。
/// </summary>
public class ElectricalCheckerTests : IDisposable
{
    protected readonly string Path_;
    protected SemanticOverlay Overlay = new();
    public ElectricalCheckerTests() => Path_ = DxfFixture.CreateTempDxf(_ => { });
    public void Dispose() => File.Delete(Path_);

    protected static Rule R(string id, string type, Dictionary<string, object>? p = null) =>
        new(id, new RuleSource("GB/T 50786-2012", "5.1.3", "摘录", "source-text"),
            "不应／不宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec(type, p ?? new()), "建议");

    [Fact]
    public void TextHeightMin_LatinBelow25_Flagged_HanBelow35_Medium()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "WL1", Height = 2.0 });  // 拉丁违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "照明", Height = 3.0 }); // 汉字违规 medium
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "WL2", Height = 2.5 });  // 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "说明", Height = 3.5 });  // 合规
        });
        try
        {
            var issues = new TextHeightMinChecker().Check(
                R("R-50786-5.1.3", "text-height-min", new() { ["minLatin"] = 2.5, ["minHan"] = 3.5 }),
                DrawingSession.Load(path), new SemanticOverlay());
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("WL1") && i.Severity == RuleSeverity.High);
            Assert.Contains(issues, i => i.Message.Contains("照明") && i.Severity == RuleSeverity.Medium);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextHeightMin_MText_MixedHan_EmptySkipped()
    {
        // MText : Entity（非 TextEntity）独立分支；混合文本按汉字判（较严）；空值跳过（无幻影问题）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new MText { InsertPoint = (XYZ)new XY(0, 0), Value = "WL3", Height = 2.0 });      // MText 拉丁违规
            doc.Entities.Add(new MText { InsertPoint = (XYZ)new XY(10, 0), Value = "配电", Height = 3.0 });    // MText 汉字违规 medium
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "WL4照明", Height = 3.0 }); // 混合：3.0≥2.5 但 <3.5→按汉字违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "", Height = 1.0 });   // 空值跳过
        });
        try
        {
            var issues = new TextHeightMinChecker().Check(
                R("R-50786-5.1.3", "text-height-min", new() { ["minLatin"] = 2.5, ["minHan"] = 3.5 }),
                DrawingSession.Load(path), new SemanticOverlay());
            Assert.Equal(3, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("WL3") && i.Severity == RuleSeverity.High);
            Assert.Contains(issues, i => i.Message.Contains("配电") && i.Severity == RuleSeverity.Medium);
            Assert.Contains(issues, i => i.Message.Contains("WL4照明") && i.Severity == RuleSeverity.Medium);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextHeightMin_MessageQuotesValueHeightThreshold_DetectionBasis()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "WL1", Height = 2.0 });
        });
        try
        {
            var issues = new TextHeightMinChecker().Check(
                R("R-50786-5.1.3", "text-height-min", new() { ["minLatin"] = 2.5, ["minHan"] = 3.5 }),
                DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Contains("\"WL1\"", issue.Message);       // 消息引号包裹值（既有约定）
            Assert.Contains("2mm", issue.Message);           // 实际字高
            Assert.Contains("2.5mm", issue.Message);         // 阈值
            Assert.Equal("实体字高直接比对；出图比例换算留计划3；主导专业用房 3.0mm 变体需图类判定（deferred）",
                issue.DetectionBasis);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextHeightMin_EpsilonBoundary()
    {
        // 最小值比较仅用 1e-9 epsilon（非系列 0.01mm 容差）：2.5-5e-10 合规；2.49 违规。
        Assert.True(CheckParams.AtLeast(2.5 - 5e-10, 2.5));
        Assert.False(CheckParams.AtLeast(2.49, 2.5));
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "EPS", Height = 2.49 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "OK", Height = 2.5 });
        });
        try
        {
            var issues = new TextHeightMinChecker().Check(
                R("R-50786-5.1.3", "text-height-min", new() { ["minLatin"] = 2.5, ["minHan"] = 3.5 }),
                DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Contains("EPS", issue.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextHeightMin_MissingParams_Defaults_2p5_3p5()
    {
        // 缺键回退标准默认值 minLatin=2.5 / minHan=3.5（Scalar 语义）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "WL9", Height = 2.0 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "回路", Height = 3.0 });
        });
        try
        {
            var issues = new TextHeightMinChecker().Check(
                R("R-50786-5.1.3", "text-height-min"),
                DrawingSession.Load(path), new SemanticOverlay());
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Severity == RuleSeverity.High);
            Assert.Contains(issues, i => i.Severity == RuleSeverity.Medium);
        }
        finally { File.Delete(path); }
    }
}
