namespace CadAgent.Core;

/// <summary>
/// 用量计量与预算（§8.4/8.6）：会话累计 token + 预算帽（超额→LlmBudgetExceededException，
/// 编排层 3b 消费）+ 套餐窗口（总额度/重置天数，窗口从首次 Record 起算，跨窗重置累计）。
/// 纯模型——审计接线（llm-call 事件）在 3b。
/// </summary>
public sealed class UsageMeter
{
    private readonly ProfileLimits _limits;
    private long _planUsedInWindow;

    public UsageMeter(ProfileLimits limits) => _limits = limits;

    public long SessionTokens { get; private set; }

    public TokenPlan? Plan => _limits.Plan;

    /// <summary>套餐窗口起点（首次 Record 记录；测试可注入模拟跨窗）。</summary>
    public DateTimeOffset? PlanWindowStart { get; set; }

    public void Record(TokenUsage usage)
    {
        long tokens = usage.PromptTokens + (long)usage.CompletionTokens;
        SessionTokens += tokens;
        if (_limits.Plan != null)
        {
            PlanWindowStart ??= DateTimeOffset.UtcNow;
            _planUsedInWindow += tokens;
        }
    }

    /// <summary>会话预算帽：SessionTokens ≥ SessionBudgetTokens → 抛（编排层决定降级/停用）。</summary>
    public void EnsureBudget()
    {
        if (SessionTokens >= _limits.SessionBudgetTokens)
            throw new LlmBudgetExceededException(
                $"会话 token 预算已用尽（{SessionTokens}/{_limits.SessionBudgetTokens}）");
    }

    /// <summary>套餐窗口剩余额度；无套餐→long.MaxValue；跨窗（now ≥ 起点+ResetDays）重置累计。</summary>
    public long PlanRemaining(DateTimeOffset now)
    {
        if (_limits.Plan == null) return long.MaxValue;
        if (PlanWindowStart.HasValue && now >= PlanWindowStart.Value.AddDays(_limits.Plan.ResetDays))
        {
            PlanWindowStart = now;
            _planUsedInWindow = 0;
        }
        return Math.Max(0, _limits.Plan.TotalTokens - _planUsedInWindow);
    }
}