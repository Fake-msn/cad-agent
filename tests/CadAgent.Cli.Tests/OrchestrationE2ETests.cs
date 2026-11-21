using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3b-i T6 E2E（离线 fake LLM）：多步计划执行 + 审计全链 + 出境同意持久化（二次调用不再询问）
/// + 配置/审计全文密钥扫描。
/// </summary>
public class OrchestrationE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"orche2e-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public OrchestrationE2ETests()
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

    private sealed class TwoStepClient : ILlmClient
    {
        public int Calls;
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(
                "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}}," +
                "{\"kind\":\"add-text\",\"params\":{\"x\":5,\"y\":5,\"text\":\"标注\",\"height\":3.5}}]}",
                new TokenUsage(20, 10), "m"));
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private const string Key = "sk-orche2e-secret-55555";

    [Fact]
    public void FullOrchestration_MultiStep_AuditChain_ConsentPersists()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "主力", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", Key).code);
        Assert.Equal(0, Run("llm", "use", "--name", "主力").code);
        var client = new TwoStepClient();
        CommandRouter.LlmClientFactory = (p, k) => client;

        // 首次 agent：--yes 自动确认出境+计划
        var (code1, out1, _) = Run("agent", "--drawing", _drawing, "--intent", "画线并标注", "--yes");
        Assert.Equal(0, code1);
        Assert.Contains("Executed", out1);
        Assert.Equal(1, client.Calls);

        // 图纸真的多了线+文字
        var session = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(3, session.Summarize().Entities.Count);
        Assert.Contains(session.Summarize().Entities, e => e.Kind == "TextEntity");

        // 审计全链：egress-consent / llm-call / agent-run / tx-start / step×2 / tx-commit
        using (var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db")))
        {
            var all = audit.ReadAll();
            Assert.Contains(all, r => r.Entry.Kind == "egress-consent");
            Assert.Contains(all, r => r.Entry.Kind == "llm-call");
            Assert.Contains(all, r => r.Entry.Kind == "agent-run");
            Assert.Contains(all, r => r.Entry.Kind == "tx-start");
            Assert.Equal(2, all.Count(r => r.Entry.Kind == "step"));
            Assert.Contains(all, r => r.Entry.Kind == "tx-commit");
            Assert.True(audit.VerifyChain(out _));
            Assert.All(all, r => Assert.DoesNotContain(Key, r.Entry.ParamsJson));
            Assert.All(all, r => Assert.DoesNotContain("画线并标注", r.Entry.ParamsJson)); // 意图不入审计
        }

        // 二次 agent：consent 已持久化（egress-consent 不再新增），计划再执行
        var (code2, out2, _) = Run("agent", "--drawing", _drawing, "--intent", "再来一次", "--yes");
        Assert.Equal(0, code2);
        Assert.Contains("Executed", out2);
        Assert.Equal(2, client.Calls);
        using (var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db")))
        {
            Assert.Equal(1, audit.ReadAll().Count(r => r.Entry.Kind == "egress-consent"));
            Assert.True(audit.VerifyChain(out _));
        }

        // 配置文件全文无密钥
        foreach (string f in Directory.GetFiles(Path.Combine(_project, ".cadagent"), "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".key") || f.EndsWith(".db") || f.EndsWith("-wal") || f.EndsWith("-shm")) continue;
            Assert.DoesNotContain(Key, File.ReadAllText(f));
        }
    }
}