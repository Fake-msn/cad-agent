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

/// <summary>一次规则评估的汇总：发现列表 + 执行/跳过计数 + 结构化跳过（2b-ii-a T5）。</summary>
/// <param name="LegacySkippedReasons">旧式字符串原因（第 4 参；StructuredSkips 为 null 的遗留构造回退用）。
/// 位置记录不能同名共存存储参数与计算属性，故第 4 参更名——既有调用方均按位置传参，源码兼容。</param>
/// <param name="StructuredSkips">结构化跳过列表（加性第 5 参，默认 null 兼容遗留构造）。</param>
public sealed record CheckSummary(
    IReadOnlyList<Issue> Issues,
    int RulesRun,
    int RulesSkipped,
    IReadOnlyList<string> LegacySkippedReasons,
    IReadOnlyList<SkipReason>? StructuredSkips = null)
{
    /// <summary>投影兼容（T5 关键契约）："{RuleId}: {Message}"——Message 保留旧 reason 文案子串
    /// （已豁免/待原件核验/无匹配检查器/检查器异常/检查器返回 null），既有字符串断言投影后仍成立。</summary>
    public IReadOnlyList<string> SkippedReasons => StructuredSkips is not null
        ? StructuredSkips.Select(s => $"{s.RuleId}: {s.Message}").ToList()
        : LegacySkippedReasons;
}
