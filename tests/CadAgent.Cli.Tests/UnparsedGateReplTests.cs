using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 防假 Green 切片 T3 Fix Round 1（R7）：repl 路径 census-blind 缺口闭合。
/// repl 图纸加载接 sidecar census（LoadWithCensus）；意图编排是 repl 循环唯一产 tx 入口
/// （:undo/:rollback 属撤回处置语义，R7 豁免不改），其前加准入闸——Yellow/Red 拒绝留痕
/// （run-rejected，Source=repl，params level/unparsedCount/guidance）+ stderr guidance，
/// 不执行、不调 LLM；:status 等只读命令与只读 CLI（check）不受影响，会话继续。
/// 无 census Green legacy 执行行为保持。
/// </summary>
public class UnparsedGateReplTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"unparsedgate-repl-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public UnparsedGateReplTests()
    {
        Directory.CreateDirectory(_project);
        // Green 基线：普通 DXF（一条原生 Line，图内 Unknowns=0）。
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
            "--model", "m", "--key", "sk-replgatekey123456", "--project", _project }, outW, errW);
        CommandRouter.Run(new[] { "llm", "use", "--name", "p", "--project", _project }, outW, errW);
    }

    /// <summary>sidecar 注入 census（TCH_WALL×2，源哈希匹配）→ Yellow（有原生+未解析）。</summary>
    private void InjectCensus() =>
        UnparsedCensusStore.Save(_project, _drawing,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));

    /// <summary>--yes 自动确认（拒绝路径不消费确认输入；legacy 路径免交互）。</summary>
    private int Repl(string script, out string stdout, out string stderr)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.ReplLoop(
            new[] { "repl", "--drawing", _drawing, "--project", _project, "--yes" },
            new StringReader(script), outW, errW);
        stdout = outW.ToString();
        stderr = errW.ToString();
        return code;
    }

    private List<AuditRecord> AuditRecords()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    /// <summary>聊天若被调用即置标志并抛异常（准入闸须先于 LLM 聊天调用；factory 构造不算调用）。</summary>
    private sealed class GuardClient : ILlmClient
    {
        public bool Called { get; private set; }
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            Called = true;
            throw new InvalidOperationException("LLM 不应被调用（repl 准入闸须先于编排）");
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    /// <summary>legacy 计划客户端（与 ReplTests.PlanClient 同口径：add-line 计划 + 验证 ok）。</summary>
    private sealed class PlanClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(request.Messages[0].Content.Contains("验证智能体") ? "{\"verdict\":\"ok\"}" : "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":7,\"y2\":7}}]}", new TokenUsage(5, 3), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void Repl_YellowUnparsed_IntentRefused_ReadOnlyStillWorks()
    {
        SetupProfile();
        InjectCensus();
        var client = new GuardClient();
        CommandRouter.LlmClientFactory = (p, k) => client;
        byte[] before = File.ReadAllBytes(_drawing);

        int code = Repl("画一条线\n:status\n:quit\n", out string stdout, out string stderr);

        Assert.Equal(0, code); // 拒绝不杀会话：:quit 正常退出
        Assert.Contains("只读审查", stderr); // Yellow guidance（T2 YellowReadOnlyGuidance）
        Assert.DoesNotContain("Executed", stdout); // 意图未执行
        Assert.Contains("entities=", stdout.Replace(" ", "")); // :status 只读命令仍执行
        Assert.False(client.Called); // 闸先于 LLM 聊天调用
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 图纸未被触碰
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("repl", rejected.Entry.Source);
        Assert.Equal(_drawing, rejected.Entry.Target);
        Assert.Equal(rejected.Entry.BeforeHash, rejected.Entry.AfterHash); // 拒绝不改图
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(2, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind is "tx-start" or "tx-commit" or "llm-call");
        Assert.Contains(records, r => r.Entry.Kind == "repl-session"); // 会话起止审计完整
        // 只读 CLI 命令不受影响：check 照常执行（check-run 留痕，非拒绝 exit 1）。
        var outW = new StringWriter(); var errW = new StringWriter();
        int checkCode = CommandRouter.Run(new[] { "check", "--drawing", _drawing, "--project", _project }, outW, errW);
        Assert.NotEqual(1, checkCode);
        Assert.Contains(AuditRecords(), r => r.Entry.Kind == "check-run");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Repl_NoCensus_GreenLegacy_Executes()
    {
        // 无 sidecar → Green legacy：意图编排照常执行 add-line 并 tx-commit（行为保持）。
        SetupProfile();
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();

        int code = Repl("画一条线\n:quit\n", out string stdout, out string stderr);

        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        Assert.DoesNotContain("只读审查", stderr);
        var records = AuditRecords();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "run-rejected");
        Assert.Contains(records, r => r.Entry.Kind == "tx-commit");
        // add-line 已提交（1 条夹具 Line + 1 条新增）。
        Assert.Equal(2, DrawingSession.Load(_drawing).Summarize().Entities.Count);
    }
}
