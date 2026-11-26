using CadAgent.Core;
using CadAgent.Report;

namespace CadAgent.Report.Tests;

/// <summary>
/// 钉住 PDF 文本投影（ReportText）：只断言纯文本，不解析 PDF——把「生成成功」升级为
/// 「生成内容正确」，防止无效计数/隐藏空段/审计措辞漂移。
/// </summary>
[Collection(ReportStaticStateCollection.Name)]
public sealed class ReviewReportTextTests
{
    private static ReviewReportInput FullInput() => new()
    {
        DrawingPath = @"C:\项目\一层平面.dwg",
        ProjectDir = @"C:\项目",
        Identity = new ReportIdentity("tester", "Review"),
        IssuedAt = DateTimeOffset.Parse("2026-11-26T10:00:00+08:00"),
        Issues = new[]
        {
            new Issue("R-MAND", "8.1.2", "GB 50001-2017", "摘录一", RuleForce.Mandatory,
                RuleSeverity.High, new[] { "1A0" }, "问题一", "建议一", "依据一"),
            new Issue("R-REC", "9.2", "GB 50002-2017", "摘录二", RuleForce.Recommended,
                RuleSeverity.Low, Array.Empty<string>(), "问题二", "建议二", "依据二")
        },
        RulesRun = 5,
        RulesSkipped = 1,
        Skipped = new[] { new SkipReason("Semantic", "R-SKIP", "无匹配检查器") },
        Adjudications = new[]
        {
            new Adjudication("R-MAND", "tester", DateTimeOffset.Parse("2026-11-25T09:00:00+08:00"),
                "现场确认强制", RuleForce.Mandatory)
        },
        Signoffs = new[]
        {
            new ReportSignoff("tx-1234567890", "checker", "Review", true, "同意",
                DateTimeOffset.Parse("2026-11-25T10:00:00+08:00"))
        },
        Audit = new ReportAuditSummary(12, true, null, 12, "DEADBEEF0123456789")
    };

    private static ReviewReportInput EmptyInput() => new()
    {
        DrawingPath = @"C:\项目\空图.dwg",
        ProjectDir = @"C:\项目",
        Identity = new ReportIdentity("tester", "Draw"),
        IssuedAt = DateTimeOffset.Parse("2026-11-26T10:00:00+08:00"),
        Issues = Array.Empty<Issue>(),
        Audit = null
    };

    [Fact]
    public void FullFixture_SummaryAndRulesLines_ReportFiledCounts()
    {
        ReviewReportInput input = FullInput();
        ReportContent content = ReportContent.Build(input);

        Assert.Equal("问题 2 项（必须 1｜应当 1｜可 0）", ReportText.ProblemLine(content));
        Assert.Equal("规则执行 5 条，跳过 1 条", ReportText.RulesLine(input));
    }

    [Fact]
    public void FullFixture_SectionHeaders_ReportFiledCounts()
    {
        ReviewReportInput input = FullInput();

        Assert.Equal("跳过检查 — 1 条", ReportText.SectionHeader("跳过检查", input.Skipped.Count));
        Assert.Equal("裁定记录 — 1 条", ReportText.SectionHeader("裁定记录", input.Adjudications.Count));
        Assert.Equal("签认记录 — 1 条", ReportText.SectionHeader("签认记录", input.Signoffs.Count));
    }

    [Fact]
    public void FullFixture_AuditLine_ReportsRecordCountAndChainOk()
    {
        string line = ReportText.AuditLine(FullInput().Audit);

        Assert.Contains("12 条记录", line);
        Assert.Contains("验链通过", line);
        Assert.DoesNotContain("验链失败", line);
    }

    [Fact]
    public void BrokenChain_AuditLine_ReportsFailureReason()
    {
        string line = ReportText.AuditLine(new ReportAuditSummary(3, false, "broken at seq 2", 3, "H"));

        Assert.Contains("3 条记录", line);
        Assert.Contains("验链失败", line);
        Assert.Contains("broken at seq 2", line);
    }

    [Fact]
    public void EmptyFixture_UsesExplicitEmptyAndMissingMarkers()
    {
        ReviewReportInput input = EmptyInput();
        ReportContent content = ReportContent.Build(input);

        Assert.Equal("问题 0 项（必须 0｜应当 0｜可 0）", ReportText.ProblemLine(content));
        Assert.Equal("未发现问题。", ReportText.NoIssues());
        Assert.Equal("（缩略图未生成）", ReportText.ThumbnailMissing());
        Assert.Equal("无", ReportText.EmptyMarker());
        Assert.Equal("跳过检查 — 0 条", ReportText.SectionHeader("跳过检查", input.Skipped.Count));
        Assert.Contains("不可用", ReportText.AuditLine(input.Audit));
    }
}
