using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3b-ii T4：REPL 交互会话——持久图纸会话+意图编排+确认+:undo/:status/:quit（StringReader 全离线）。
/// </summary>
public class ReplTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"repl-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public ReplTests()
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

    private void SetupProfile()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        CommandRouter.Run(new[] { "llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-replkey12345678", "--project", _project }, outW, errW);
        CommandRouter.Run(new[] { "llm", "use", "--name", "p", "--project", _project }, outW, errW);
    }

    private sealed class PlanClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(request.Messages[0].Content.Contains("验证智能体") ? "{\"verdict\":\"ok\"}" : "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":7,\"y2\":7}}]}", new TokenUsage(5, 3), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private int Repl(string script, out string stdout)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.ReplLoop(
            new[] { "repl", "--drawing", _drawing, "--project", _project },
            new StringReader(script), outW, errW);
        stdout = outW.ToString();
        return code;
    }

    [Fact]
    public void Repl_IntentConfirmUndoStatusQuit()
    {
        SetupProfile();
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();
        int code = Repl("画一条线\ny\ny\n:status\n:undo\n:status\n:quit\n", out string stdout);
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        Assert.Contains("entities=1", stdout.Replace(" ", ""));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "step-undone");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "repl-session");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Repl_DeclinePlan_NoExecution()
    {
        SetupProfile();
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();
        int code = Repl("画线\ny\nn\n:quit\n", out string stdout);
        Assert.Equal(0, code);
        Assert.Contains("RejectedByHuman", stdout);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities);
    }

    [Fact]
    public void Repl_Eof_Exits()
    {
        SetupProfile();
        int code = Repl("", out _);
        Assert.Equal(0, code);
    }
}