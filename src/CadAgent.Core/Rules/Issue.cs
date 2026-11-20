namespace CadAgent.Core;

/// <summary>一条检查发现：出处引用 + 生效强制力 + 严重度 + 目标图元 + 问题/建议 + 检测依据。</summary>
/// <param name="Force">生效强制力——引擎按裁定覆写后的值（checker 填规则原值，引擎替换）。</param>
/// <param name="Targets">目标图元 handle 列表。</param>
/// <param name="DetectionBasis">检测依据（由 checker 填写，说明如何检出）。</param>
public sealed record Issue(
    string RuleId,
    string Clause,
    string Standard,
    string Excerpt,
    RuleForce Force,
    RuleSeverity Severity,
    IReadOnlyList<string> Targets,
    string Message,
    string Suggestion,
    string DetectionBasis);

/// <summary>一次规则评估的汇总：发现列表 + 执行/跳过计数 + 去重后的跳过原因。</summary>
public sealed record CheckSummary(
    IReadOnlyList<Issue> Issues,
    int RulesRun,
    int RulesSkipped,
    IReadOnlyList<string> SkippedReasons);