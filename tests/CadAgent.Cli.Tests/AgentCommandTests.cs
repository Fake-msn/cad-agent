using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3b-i T5：CLI agent 命令——完整编排闭环（--yes 自动确认路径；交互路径手动冒烟）。
/// </summary>
public class AgentCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"agentcli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public AgentCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private void SetupProfile()
    {
        Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1", "--model", "m", "--key", "sk-agentkey12345678");
        Run("llm", "use", "--name", "p");
    }

    private sealed class PlanClient : ILlmClient
    {
        public string Plan { get; set; } = "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":50,\"y2\":50}}]}";
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(request.Messages[0].Content.Contains("验证智能体") ? "{\"verdict\":\"ok\"}" : Plan, new TokenUsage(10, 5), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void Agent_YesFlag_ExecutesPlan_AuditChainGreen()
    {
        SetupProfile();
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "画一条线", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        var session = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(2, session.Summarize().Entities.Count);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "llm-call");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
        Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-agentkey12345678", r.Entry.ParamsJson));
    }

    [Fact]
    public void Agent_NoProfile_ExitOne()
    {
        var (code, _, stderr) = Run("agent", "--drawing", _drawing, "--intent", "x", "--yes");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Agent_LocalOnlyProject_BlocksCloudProfile()
    {
        SetupProfile();
        File.WriteAllText(Path.Combine(_project, ".cadagent", "local-only.txt"), "");
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "x", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("BlockedEgress", stdout);
    }

    [Fact]
    public void Agent_InvalidPlanFromLlm_Failed()
    {
        SetupProfile();
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient { Plan = "我不知道怎么做" };
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "x", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Failed", stdout);
    }
}