namespace CadAgent.Core.Tests;

public class RuleEngineTests
{
    private sealed class FakeSession : IDrawingSession
    {
        public string SourcePath => "fake.dxf";
        public string ComputeHash() => "h";
        public void SaveWorking() { }
        public void Reload() { }
    }

    private sealed class FakeChecker : IRuleChecker
    {
        public string CheckType => "fake";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            new[] { new Issue(r.Id, r.Source.Clause, r.Source.Standard, r.Source.Excerpt,
                r.Force, r.Severity, new[] { "h1" }, "问题", "建议", "fake检测") };
    }

    private sealed class ThrowingChecker : IRuleChecker
    {
        public string CheckType => "boom";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            throw new InvalidOperationException("检查器爆炸");
    }

    // 最终审查修复 4（控制者裁定）：检查器返回 null 不得逃出隔离边界（NRE 崩引擎）。
    private sealed class NullReturningChecker : IRuleChecker
    {
        public string CheckType => "null";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) => null!;
    }

    private static Rule MakeRule(string id, string type, RuleForce f = RuleForce.Recommended, string verification = "source-text") =>
        new(id, new RuleSource("GB/T 50001-2017", "1.0.1", "摘录", verification),
            "应", f, RuleSeverity.High, new RuleScope(), new CheckSpec(type, new()), "建议");

    [Fact]
    public void Evaluate_ProducesIssues_WithEffectiveForce()
    {
        var engine = new RuleEngine(new[] { new FakeChecker() });
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "院标升级", RuleForce.Mandatory));
        var summary = engine.Evaluate(new[] { MakeRule("R1", "fake") }, new FakeSession(), new SemanticOverlay(), adj);
        Assert.Single(summary.Issues);
        Assert.Equal(RuleForce.Mandatory, summary.Issues[0].Force); // 裁定升级生效
        Assert.Equal(1, summary.RulesRun);
    }

    [Fact]
    public void Evaluate_Exempt_Skips()
    {
        var engine = new RuleEngine(new[] { new FakeChecker() });
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "不适用", null, true));
        var summary = engine.Evaluate(new[] { MakeRule("R1", "fake") }, new FakeSession(), new SemanticOverlay(), adj);
        Assert.Empty(summary.Issues);
        Assert.Equal(1, summary.RulesSkipped);
    }

    [Fact]
    public void Evaluate_PendingOriginal_Skips()
    {
        var engine = new RuleEngine(new[] { new FakeChecker() });
        var summary = engine.Evaluate(new[] { MakeRule("R1", "fake", verification: "pending-original") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Empty(summary.Issues);
        Assert.Contains(summary.SkippedReasons, r => r.Contains("待原件核验"));
    }

    [Fact]
    public void Evaluate_CheckerThrows_SkipsNotCrash()
    {
        var engine = new RuleEngine(new[] { new ThrowingChecker() });
        var summary = engine.Evaluate(new[] { MakeRule("R1", "boom") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Empty(summary.Issues);
        Assert.Equal(1, summary.RulesSkipped);
        Assert.Contains(summary.SkippedReasons, r => r.Contains("检查器爆炸"));
    }

    // 最终审查修复 4（控制者裁定）：null 返回视为检查器故障——跳过该规则并记账原因，
    // 引擎不崩溃（与异常隔离同一通道）。
    [Fact]
    public void Evaluate_CheckerReturnsNull_SkipsNotCrash()
    {
        var engine = new RuleEngine(new[] { new NullReturningChecker() });
        var summary = engine.Evaluate(new[] { MakeRule("R1", "null") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Empty(summary.Issues);
        Assert.Equal(1, summary.RulesSkipped);
        Assert.Equal(0, summary.RulesRun);
        Assert.Contains(summary.SkippedReasons, r => r.Contains("null"));
    }

    [Fact]
    public void Evaluate_NoChecker_Skips()
    {
        var engine = new RuleEngine(Array.Empty<IRuleChecker>());
        var summary = engine.Evaluate(new[] { MakeRule("R1", "unknown-type") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Equal(1, summary.RulesSkipped);
    }

    // T2 评审补测：豁免规则的 EffectiveForce 必须返回 null（引擎跳过契约的承重信号），
    // 且豁免优先于 ForceOverride。
    [Fact]
    public void EffectiveForce_Exempt_ReturnsNull_EvenWithOverride()
    {
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "不适用", RuleForce.Mandatory, true));
        Assert.Null(adj.EffectiveForce(MakeRule("R1", "fake")));
    }
}
