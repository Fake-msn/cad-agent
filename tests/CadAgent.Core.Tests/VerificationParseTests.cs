using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

/// <summary>3i T1：读回验证响应解析——verdict ok / 纠正计划 / 空计划 ok / 不可解析抛。</summary>
public class VerificationParseTests
{
    private static readonly HashSet<string> Kinds = new() { "ok-op" };

    [Fact]
    public void VerdictOk_ReturnsOk()
    {
        var (ok, fix) = AgentPlanParser.ExtractVerification("{\"verdict\":\"ok\"}", Kinds);
        Assert.True(ok);
        Assert.Null(fix);
    }

    [Fact]
    public void VerdictOk_Fenced_ReturnsOk()
    {
        var (ok, _) = AgentPlanParser.ExtractVerification("```json\n{\"verdict\":\"OK\"}\n```", Kinds);
        Assert.True(ok);
    }

    [Fact]
    public void FixPlan_ReturnsPlan()
    {
        var (ok, fix) = AgentPlanParser.ExtractVerification(
            "{\"steps\":[{\"kind\":\"ok-op\",\"params\":{}}]}", Kinds);
        Assert.False(ok);
        Assert.NotNull(fix);
        Assert.Single(fix!.Steps);
    }

    [Fact]
    public void EmptyPlan_TreatedAsOk()
    {
        var (ok, fix) = AgentPlanParser.ExtractVerification("{\"steps\":[]}", Kinds);
        Assert.True(ok);
        Assert.Null(fix);
    }

    [Fact]
    public void Unparseable_Throws()
    {
        Assert.Throws<InvalidPlanException>(() =>
            AgentPlanParser.ExtractVerification("看起来没问题", Kinds));
    }

    [Fact]
    public void VerdictFixWithoutSteps_Throws()
    {
        Assert.Throws<InvalidPlanException>(() =>
            AgentPlanParser.ExtractVerification("{\"verdict\":\"fix\"}", Kinds));
    }

    [Fact]
    public void FixPlanUnknownKind_Throws()
    {
        Assert.Throws<InvalidPlanException>(() =>
            AgentPlanParser.ExtractVerification("{\"steps\":[{\"kind\":\"nope\",\"params\":{}}]}", Kinds));
    }
}