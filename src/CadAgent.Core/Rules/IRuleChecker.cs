namespace CadAgent.Core;

/// <summary>规则检查器：按 CheckSpec.Type 匹配；session 为 Core 接口，Data 检查器自行 cast 到 DrawingSession。</summary>
public interface IRuleChecker
{
    string CheckType { get; }
    IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay);
}

/// <summary>标记接口（2b-ii-a T5）：检查器依赖语义叠加层标注——overlay 无任何标注时
/// 引擎记 NotEvaluable 结构化跳过（不跑空），不得与"已查且干净"不可区分。</summary>
public interface IOverlayDependent { }

/// <summary>结构化跳过原因（2b-ii-a T5）：类别常量 + 规则 Id + 人读消息。
/// Message 保留旧 reason 文案子串——CheckSummary.SkippedReasons 投影后既有字符串断言兼容。</summary>
public sealed record SkipReason(string Category, string RuleId, string Message);

/// <summary>跳过类别常量（值=名称原样，供 stderr "[Category]" 与审计 category 字段）。</summary>
public static class SkipCategories
{
    public const string Exempt = "Exempt";
    public const string PendingOriginal = "PendingOriginal";
    public const string NoChecker = "NoChecker";
    public const string CheckerError = "CheckerError";
    public const string NotEvaluable = "NotEvaluable";
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
        var skips = new List<SkipReason>();
        int run = 0, skipped = 0;

        foreach (var rule in rules)
        {
            // 豁免优先（EffectiveForce null = 跳过信号），先于一切其他判定。
            var force = adjudications.EffectiveForce(rule);
            if (force is null)
            {
                Skip(SkipCategories.Exempt, rule.Id, $"规则 {rule.Id} 已豁免");
                continue;
            }

            if (rule.Source.Verification == "pending-original")
            {
                Skip(SkipCategories.PendingOriginal, rule.Id, $"规则 {rule.Id} 待原件核验");
                continue;
            }

            if (!_checkers.TryGetValue(rule.Check.Type, out var checker))
            {
                Skip(SkipCategories.NoChecker, rule.Id, $"规则 {rule.Id} 无匹配检查器（{rule.Check.Type}）");
                continue;
            }

            // NotEvaluable（2b-ii-a T5）：overlay 依赖检查器在无语义标注时不跑空——
            // 结构化跳过记账，不得与"已查且干净"不可区分（先于 checker 执行判定）。
            if (overlay.Annotations.Count == 0 && checker is IOverlayDependent)
            {
                Skip(SkipCategories.NotEvaluable, rule.Id, $"规则 {rule.Id} 无语义标注，未评估");
                continue;
            }

            IReadOnlyList<Issue> produced;
            try
            {
                produced = checker.Check(rule, session, overlay);
            }
            catch (Exception ex)
            {
                Skip(SkipCategories.CheckerError, rule.Id, $"规则 {rule.Id} 检查器异常：{ex.Message}");
                continue;
            }

            // 隔离守卫（最终审查裁定）：检查器返回 null 不得在 try 外枚举时以 NRE 逃出隔离边界——
            // 视为检查器故障，跳过该规则并记账原因（与异常隔离同一通道）。
            if (produced is null)
            {
                Skip(SkipCategories.CheckerError, rule.Id, $"规则 {rule.Id} 检查器返回 null");
                continue;
            }

            // Informational 规则同样评估；issue.Force 一律覆写为生效强制力（展示用）。
            foreach (var issue in produced) issues.Add(issue with { Force = force.Value });
            run++;
        }

        // 结构化跳过为唯一事实源；第 4 参传同内容投影（StructuredSkips 非 null 时
        // CheckSummary.SkippedReasons 以投影属性重算，二者一致）。去重沿用旧 Distinct 语义。
        var structured = skips.Distinct().ToList();
        return new CheckSummary(issues, run, skipped,
            structured.Select(s => $"{s.RuleId}: {s.Message}").ToList(), structured);

        void Skip(string category, string ruleId, string message)
        {
            skipped++;
            skips.Add(new SkipReason(category, ruleId, message));
        }
    }
}
