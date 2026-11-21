using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

/// <summary>
/// 3b-i T4：编排闭环——出境闸/预算前置/计划校验/人工确认/有界重规划/llm-call 审计。
/// </summary>
public class AgentOrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orch-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly StepEngine _engine;
    private readonly FakeSession _session;
    private static readonly HashSet<string> Kinds = new() { "ok-op", "bad-op" };

    public AgentOrchestratorTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
        _session = new FakeSession(Path.Combine(_dir, "d.dxf"));
        _engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(_dir, "s")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new OkHandler(), new BadHandler() });
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FakeSession : IDrawingSession
    {
        public FakeSession(string path) { SourcePath = path; File.WriteAllText(path, "v0"); }
        public string SourcePath { get; }
        public string ComputeHash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath)));
        public void SaveWorking() => File.WriteAllText(SourcePath, "v1");
        public void Reload() { }
        public byte[] CaptureState() => System.Array.Empty<byte>();
        public void RestoreState(byte[] state) { }
    }
    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }
    private sealed class OkHandler : IStepHandler
    {
        public string Kind => "ok-op";
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }
    private sealed class BadHandler : IStepHandler
    {
        public string Kind => "bad-op";
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(false, "模拟执行失败", "");
    }
    private sealed class ScriptedClient : ILlmClient
    {
        public Queue<string> Responses { get; } = new();
        public List<ChatRequest> Requests { get; } = new();
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            string r = Responses.Count > 0 ? Responses.Dequeue() : "{\"steps\":[]}";
            return Task.FromResult(new ChatResponse(r, new TokenUsage(10, 5), "m"));
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private static ProviderProfile CloudProfile() =>
        new("p", "https://api.test/v1", "m", false, DataPolicy.CloudAllowed, new ProfileLimits());

    private AgentOrchestrator Make(ScriptedClient client, EgressPolicy? egress = null) =>
        new(client, new UsageMeter(new ProfileLimits()), egress ?? new EgressPolicy(_dir),
            _engine, _audit, new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), "m", CloudProfile());

    private const string GoodPlan = "{\"steps\":[{\"kind\":\"ok-op\",\"params\":{}}]}";
    private const string BadPlan = "{\"steps\":[{\"kind\":\"bad-op\",\"params\":{}}]}";

    [Fact]
    public async Task HappyPath_Executed_AuditedLlmCall()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        var orch = Make(client);
        var result = await orch.RunAsync("画一条线", "摘要", _session, "s1", Kinds, "ok-op: 测试",
            confirmPlan: _ => true, confirmCloudEgress: () => true);
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        Assert.NotNull(result.TxId);
        // 3i 契约变更（sanctioned）：Executed 含读回验证调用（计划15+验证15）
        Assert.Equal(30, result.TotalUsage!.PromptTokens + result.TotalUsage.CompletionTokens);
        Assert.Equal(1, result.VerificationRounds);
        var all = _audit.ReadAll();
        Assert.Contains(all, r => r.Entry.Kind == "llm-call");
        Assert.Contains(all, r => r.Entry.Kind == "agent-run");
        Assert.Contains(all, r => r.Entry.Kind == "egress-consent");
        Assert.All(all, r => Assert.DoesNotContain("摘要", r.Entry.ParamsJson));
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public async Task EgressBlocked_NoLlmCall()
    {
        File.WriteAllText(Path.Combine(_dir, "local-only.txt"), "");
        var client = new ScriptedClient();
        var result = await Make(client).RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => true);
        Assert.Equal(OrchestrationStatus.BlockedEgress, result.Status);
        Assert.Empty(client.Requests);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "egress-blocked");
    }

    [Fact]
    public async Task EgressConsentDenied_Blocked()
    {
        var client = new ScriptedClient();
        var result = await Make(client).RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => true, confirmCloudEgress: () => false);
        Assert.Equal(OrchestrationStatus.BlockedEgress, result.Status);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task HumanRejectsPlan_NoExecution()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        var result = await Make(client).RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => false, confirmCloudEgress: () => true);
        Assert.Equal(OrchestrationStatus.RejectedByHuman, result.Status);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "plan-rejected");
        Assert.DoesNotContain(_audit.ReadAll(), r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public async Task ExecutionFails_ReplanOnce_ThenExecuted()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(BadPlan);
        client.Responses.Enqueue(GoodPlan);
        var result = await Make(client).RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => true, confirmCloudEgress: () => true);
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        // 3i 契约变更（sanctioned）：重规划成功后亦进读回验证环（3 次 LLM 调用）
        Assert.Equal(3, client.Requests.Count);
        Assert.Contains("模拟执行失败", client.Requests[1].Messages[^1].Content);
        Assert.Equal(3, _audit.ReadAll().Count(r => r.Entry.Kind == "llm-call"));
        Assert.Equal(1, result.VerificationRounds);
    }

    [Fact]
    public async Task ExecutionFailsTwice_Escalated()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(BadPlan);
        client.Responses.Enqueue(BadPlan);
        var result = await Make(client).RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => true, confirmCloudEgress: () => true);
        Assert.Equal(OrchestrationStatus.Escalated, result.Status);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "agent-run" && r.Entry.ParamsJson.Contains("escalated"));
    }

    [Fact]
    public async Task InvalidPlanJson_Failed_NoReplan()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue("抱歉我不知道");
        var result = await Make(client).RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => true, confirmCloudEgress: () => true);
        Assert.Equal(OrchestrationStatus.Failed, result.Status);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task BudgetExceeded_Terminates()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        var meter = new UsageMeter(new ProfileLimits(SessionBudgetTokens: 5));
        meter.Record(new TokenUsage(5, 0)); // 预计量触顶——EnsureBudget 前置检查
        var orch = new AgentOrchestrator(client, meter, new EgressPolicy(_dir), _engine, _audit,
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), "m", CloudProfile());
        var result = await orch.RunAsync("意图", "摘要", _session, "s1", Kinds, "",
            confirmPlan: _ => true, confirmCloudEgress: () => true);
        Assert.Equal(OrchestrationStatus.BudgetExceeded, result.Status);
        Assert.Empty(client.Requests);
    }
}