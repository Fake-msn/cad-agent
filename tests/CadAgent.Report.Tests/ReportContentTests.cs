using CadAgent.Core;
using CadAgent.Report;

namespace CadAgent.Report.Tests;

public sealed class ReportContentTests
{
    private static Issue MakeIssue(string ruleId, RuleForce force, string message = "m") =>
        new(ruleId, "8.1", "GB 50001-2017", "摘录", force, RuleSeverity.Medium,
            new[] { "1A0" }, message, "建议", "检测依据");

    private static ReviewReportInput MakeInput(
        IReadOnlyList<Issue> issues,
        IReadOnlyDictionary<string, string>? verifications = null) => new()
    {
        DrawingPath = @"C:\tmp\x.dwg",
        ProjectDir = @"C:\tmp",
        Identity = new ReportIdentity("u", "Review"),
        IssuedAt = DateTimeOffset.Parse("2026-11-26T10:00:00+08:00"),
        Issues = issues,
        RuleVerifications = verifications ?? new Dictionary<string, string>()
    };

    [Fact]
    public void Build_GroupsByForceInMandatoryRecommendedInformationalOrder()
    {
        var content = ReportContent.Build(MakeInput(new[]
        {
            MakeIssue("R3", RuleForce.Informational),
            MakeIssue("R1", RuleForce.Mandatory),
            MakeIssue("R2", RuleForce.Recommended)
        }));

        Assert.Equal(new[] { RuleForce.Mandatory, RuleForce.Recommended, RuleForce.Informational },
            content.Groups.Select(g => g.Force));
        Assert.Equal(new[] { "R1" }, content.Groups[0].Issues.Select(i => i.RuleId));
    }

    [Fact]
    public void Build_SkipsEmptyForceGroups_AndZeroCounts()
    {
        var content = ReportContent.Build(MakeInput(new[] { MakeIssue("R1", RuleForce.Mandatory) }));

        Assert.Single(content.Groups);
        Assert.Equal(RuleForce.Mandatory, content.Groups[0].Force);
        Assert.Equal(1, content.Total);
        Assert.Equal(1, content.Mandatory);
        Assert.Equal(0, content.Recommended);
        Assert.Equal(0, content.Informational);
    }

    [Fact]
    public void Build_MapsVerificationByRuleId_AndLeavesOtherIssuesNull()
    {
        var content = ReportContent.Build(MakeInput(
            new[] { MakeIssue("R1", RuleForce.Mandatory), MakeIssue("R2", RuleForce.Mandatory) },
            new Dictionary<string, string> { ["R1"] = "source-text" }));

        Assert.Equal("source-text", content.Groups[0].Issues.Single(i => i.RuleId == "R1").Verification);
        Assert.Null(content.Groups[0].Issues.Single(i => i.RuleId == "R2").Verification);
    }

    [Fact]
    public void Build_EmptyIssues_ProducesNoGroups()
    {
        var content = ReportContent.Build(MakeInput(Array.Empty<Issue>()));

        Assert.Empty(content.Groups);
        Assert.Equal(0, content.Total);
    }
}
