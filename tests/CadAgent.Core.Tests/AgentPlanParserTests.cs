using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

/// <summary>
/// 3b-i T3：LLM 响应→步骤计划提取（围栏/裸 JSON）+ planId 注入 + kind 白名单。
/// </summary>
public class AgentPlanParserTests
{
    private static readonly HashSet<string> Kinds = new() { "add-line", "add-text" };

    [Fact]
    public void Extract_FencedJson_Works()
    {
        string resp = "好的，计划如下：\n```json\n{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}}]}\n```\n请确认。";
        var plan = AgentPlanParser.ExtractPlan(resp, Kinds);
        Assert.Single(plan.Steps);
        Assert.StartsWith("llm-", plan.PlanId);
    }

    [Fact]
    public void Extract_RawJson_WithPlanId_Works()
    {
        string resp = "{\"planId\":\"p9\",\"steps\":[{\"kind\":\"add-text\",\"params\":{\"x\":0,\"y\":0,\"text\":\"T\",\"height\":3.5}}]}";
        var plan = AgentPlanParser.ExtractPlan(resp, Kinds);
        Assert.Equal("p9", plan.PlanId);
    }

    [Fact]
    public void Extract_UnknownKind_Rejected()
    {
        string resp = "{\"steps\":[{\"kind\":\"drop-table\",\"params\":{}}]}";
        var ex = Assert.Throws<InvalidPlanException>(() => AgentPlanParser.ExtractPlan(resp, Kinds));
        Assert.Contains("drop-table", ex.Message);
    }

    [Fact]
    public void Extract_NoJson_Rejected()
    {
        Assert.Throws<InvalidPlanException>(() => AgentPlanParser.ExtractPlan("我无法完成这个任务", Kinds));
    }

    [Fact]
    public void Extract_MalformedJson_Rejected()
    {
        Assert.Throws<InvalidPlanException>(() => AgentPlanParser.ExtractPlan("{\"steps\": [broken", Kinds));
    }
}