using CadAgent.Core;

namespace CadAgent.Core.Tests;

/// <summary>
/// LLM 基础设施 T6：会话累计 + 预算帽异常 + 套餐窗口重置（窗口从首次 Record 起算）。
/// </summary>
public class UsageMeterTests
{
    [Fact]
    public void Record_AccumulatesPromptAndCompletion()
    {
        var m = new UsageMeter(new ProfileLimits());
        m.Record(new TokenUsage(10, 5));
        m.Record(new TokenUsage(3, 2));
        Assert.Equal(20, m.SessionTokens);
    }

    [Fact]
    public void EnsureBudget_Below_Ok_AtOrAbove_Throws()
    {
        var m = new UsageMeter(new ProfileLimits(SessionBudgetTokens: 100));
        m.Record(new TokenUsage(50, 49));
        m.EnsureBudget(); // 99 < 100
        m.Record(new TokenUsage(1, 0));
        Assert.Throws<LlmBudgetExceededException>(() => m.EnsureBudget()); // 100 >= 100
    }

    [Fact]
    public void PlanRemaining_NoPlan_MaxValue()
    {
        var m = new UsageMeter(new ProfileLimits());
        m.Record(new TokenUsage(10, 10));
        Assert.Equal(long.MaxValue, m.PlanRemaining(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PlanRemaining_WithinWindow_Subtracts()
    {
        var m = new UsageMeter(new ProfileLimits(Plan: new TokenPlan(1000, 30)));
        m.Record(new TokenUsage(100, 200));
        Assert.Equal(700, m.PlanRemaining(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PlanRemaining_WindowExpired_Resets()
    {
        var m = new UsageMeter(new ProfileLimits(Plan: new TokenPlan(1000, 30)));
        m.Record(new TokenUsage(100, 200));
        Assert.Equal(700, m.PlanRemaining(DateTimeOffset.UtcNow));
        // 模拟窗口起点在 31 天前 → 跨窗重置
        m.PlanWindowStart = DateTimeOffset.UtcNow.AddDays(-31);
        Assert.Equal(1000, m.PlanRemaining(DateTimeOffset.UtcNow));
        // 重置后新用量重新累计
        m.Record(new TokenUsage(50, 50));
        Assert.Equal(900, m.PlanRemaining(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PlanRemaining_AtWindowBoundary_StillOldWindow()
    {
        var m = new UsageMeter(new ProfileLimits(Plan: new TokenPlan(1000, 30)));
        m.Record(new TokenUsage(10, 0));
        var start = m.PlanWindowStart!.Value;
        Assert.Equal(990, m.PlanRemaining(start.AddDays(30).AddSeconds(-1)));
        Assert.Equal(1000, m.PlanRemaining(start.AddDays(30)));
    }
}