namespace CadAgent.Core.Tests;

/// <summary>
/// 2b-ii-a T5 结构化跳过报告：SkipReason(Category/RuleId/Message) + SkipCategories 常量；
/// overlay 空且 checker 为 IOverlayDependent → NotEvaluable 跳过（不跑空）；
/// 其余跳过路径（Exempt/PendingOriginal/NoChecker/CheckerError）结构化；
/// CheckSummary.SkippedReasons 为投影 "{RuleId}: {Message}"——Message 保留旧文案子串，
/// 既有字符串断言兼容（投影兼容是本任务的关键契约）。
/// </summary>
public class StructuredSkipTests
{
    private sealed class FakeSession : IDrawingSession
    {
        public string SourcePath => "f.dxf";
        public string ComputeHash() => "h";
        public void SaveWorking() { }
        public void Reload() { }
        public byte[] CaptureState() => System.Array.Empty<byte>();
        public void RestoreState(byte[] state) { }
    }
    private sealed class OverlayChecker : IRuleChecker, IOverlayDependent
    {
        public string CheckType => "overlay-dep";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            new[] { new Issue(r.Id, "1", "S", "e", r.Force, r.Severity, new[] { "h" }, "m", "s", "b") };
    }
    private static Rule R(string id) => new(id, new RuleSource("S", "1", "e", "source-text"),
        "应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("overlay-dep", new()), "s");

    [Fact]
    public void EmptyOverlay_OverlayDependent_NotEvaluableSkip()
    {
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Equal(0, summary.RulesRun);
        Assert.Equal(1, summary.RulesSkipped);
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.NotEvaluable, skip.Category);
        Assert.Equal("R1", skip.RuleId);
        Assert.Contains("R1", summary.SkippedReasons[0]); // 投影兼容
    }

    [Fact]
    public void NonEmptyOverlay_OverlayDependent_Runs()
    {
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation("h", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "t"));
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), overlay, new AdjudicationSet());
        Assert.Equal(1, summary.RulesRun);
        Assert.Single(summary.Issues);
    }

    [Fact]
    public void ExemptSkip_StructuredCategory()
    {
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "豁免", null, true));
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), new SemanticOverlay(), adj);
        Assert.Equal(SkipCategories.Exempt, summary.StructuredSkips![0].Category);
        Assert.Contains("已豁免", summary.SkippedReasons[0]); // 旧文案子串兼容
    }

    // ---- 补充覆盖：其余跳过路径的结构化类别 + 旧文案子串逐一核对 ----

    private sealed class PlainChecker : IRuleChecker
    {
        public string CheckType => "plain";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            new[] { new Issue(r.Id, "1", "S", "e", r.Force, r.Severity, new[] { "h" }, "m", "s", "b") };
    }
    private sealed class ThrowingChecker : IRuleChecker
    {
        public string CheckType => "boom";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            throw new InvalidOperationException("检查器爆炸");
    }
    private sealed class NullReturningChecker : IRuleChecker
    {
        public string CheckType => "null";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) => null!;
    }
    private static Rule Typed(string id, string type, string verification = "source-text") =>
        new(id, new RuleSource("S", "1", "e", verification), "应",
            RuleForce.Recommended, RuleSeverity.High, new RuleScope(), new CheckSpec(type, new()), "s");

    [Fact]
    public void NotEvaluable_Message_PreservesInterimWording()
    {
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Contains("无语义标注，未评估", summary.StructuredSkips![0].Message);
        Assert.Contains("未评估", summary.SkippedReasons[0]); // 2b-i interim stderr 提示文案兼容
    }

    [Fact]
    public void PendingOriginalSkip_StructuredCategory_OldWording()
    {
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { Typed("R1", "overlay-dep", "pending-original") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.PendingOriginal, skip.Category);
        Assert.Contains("待原件核验", skip.Message);
        Assert.Contains("待原件核验", summary.SkippedReasons[0]); // 旧断言子串兼容
    }

    [Fact]
    public void NoCheckerSkip_StructuredCategory_OldWording()
    {
        var engine = new RuleEngine(Array.Empty<IRuleChecker>());
        var summary = engine.Evaluate(new[] { Typed("R1", "unknown-type") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.NoChecker, skip.Category);
        Assert.Contains("无匹配检查器", skip.Message);
        Assert.Contains("unknown-type", skip.Message);
    }

    [Fact]
    public void CheckerThrows_StructuredCategory_OldWording()
    {
        var engine = new RuleEngine(new[] { new ThrowingChecker() });
        var summary = engine.Evaluate(new[] { Typed("R1", "boom") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.CheckerError, skip.Category);
        Assert.Contains("检查器爆炸", summary.SkippedReasons[0]); // 旧断言子串兼容
    }

    [Fact]
    public void CheckerReturnsNull_StructuredCategory_OldWording()
    {
        var engine = new RuleEngine(new[] { new NullReturningChecker() });
        var summary = engine.Evaluate(new[] { Typed("R1", "null") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.CheckerError, skip.Category);
        Assert.Contains("检查器返回 null", skip.Message);
        Assert.Contains("null", summary.SkippedReasons[0]); // 旧断言子串兼容
    }

    [Fact]
    public void EmptyOverlay_NonOverlayDependent_Runs()
    {
        // NotEvaluable 仅针对 IOverlayDependent 标记检查器；几何级检查器空 overlay 照跑。
        var engine = new RuleEngine(new[] { new PlainChecker() });
        var summary = engine.Evaluate(new[] { Typed("R1", "plain") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Equal(1, summary.RulesRun);
        Assert.Equal(0, summary.RulesSkipped);
        Assert.Empty(summary.StructuredSkips!);
    }

    [Fact]
    public void Exempt_TakesPrecedence_OverNotEvaluable()
    {
        // 豁免优先于一切其他判定（含 NotEvaluable）：空 overlay + 豁免 → Exempt。
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "不适用", null, true));
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), new SemanticOverlay(), adj);
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.Exempt, skip.Category);
    }
}
