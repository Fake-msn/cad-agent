namespace CadAgent.Core;

/// <summary>规则检查器：按 CheckSpec.Type 匹配；session 为 Core 接口，Data 检查器自行 cast 到 DrawingSession。</summary>
public interface IRuleChecker
{
    string CheckType { get; }
    IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay);
}

/// <summary>规则引擎：逐规则应用裁定→跳过记账→调 checker→覆写 issue.Force。
/// checker 不感知裁定（收到 rule 原样）；checker 异常只跳过该规则，不崩引擎。</summary>
public sealed class RuleEngine
{
    private readonly Dictionary<string, IRuleChecker> _checkers = new(StringComparer.Ordinal);

    public RuleEngine(IEnumerable<IRuleChecker> checkers)
    {
        foreach (var c in checkers) _checkers[c.CheckType] = c;
    }

    public CheckSummary Evaluate(
        IReadOnlyList<Rule> rules,
        IDrawingSession session,
        SemanticOverlay overlay,
        AdjudicationSet adjudications)
    {
        var issues = new List<Issue>();
        var reasons = new List<string>();
        int run = 0, skipped = 0;

        foreach (var rule in rules)
        {
            // 豁免优先（EffectiveForce null = 跳过信号），先于一切其他判定。
            var force = adjudications.EffectiveForce(rule);
            if (force is null)
            {
                Skip($"规则 {rule.Id} 已豁免");
                continue;
            }

            if (rule.Source.Verification == "pending-original")
            {
                Skip($"规则 {rule.Id} 待原件核验");
                continue;
            }

            if (!_checkers.TryGetValue(rule.Check.Type, out var checker))
            {
                Skip($"规则 {rule.Id} 无匹配检查器（{rule.Check.Type}）");
                continue;
            }

            IReadOnlyList<Issue> produced;
            try
            {
                produced = checker.Check(rule, session, overlay);
            }
            catch (Exception ex)
            {
                Skip($"规则 {rule.Id} 检查器异常：{ex.Message}");
                continue;
            }

            // 隔离守卫（最终审查裁定）：检查器返回 null 不得在 try 外枚举时以 NRE 逃出隔离边界——
            // 视为检查器故障，跳过该规则并记账原因（与异常隔离同一通道）。
            if (produced is null)
            {
                Skip($"规则 {rule.Id} 检查器返回 null");
                continue;
            }

            // Informational 规则同样评估；issue.Force 一律覆写为生效强制力（展示用）。
            foreach (var issue in produced) issues.Add(issue with { Force = force.Value });
            run++;
        }

        return new CheckSummary(issues, run, skipped, reasons.Distinct().ToList());

        void Skip(string reason)
        {
            skipped++;
            reasons.Add(reason);
        }
    }
}
