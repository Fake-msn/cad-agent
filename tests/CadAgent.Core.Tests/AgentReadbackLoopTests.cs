using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

/// <summary>
/// 3i T2：读回迭代环——验证 ok/纠正迭代/纠正失败/不可解析/轮耗尽/预算耗尽/人工拒绝纠正/摘要 seam。
/// </summary>
public class AgentReadbackLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rb-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly StepEngine _engine;
    private readonly FakeSession _session;
    private static readonly HashSet<string> Kinds = new() { "ok-op", "bad-op" };

    public AgentReadbackLoopTests()
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
        public int SaveCount { get; private set; }
        public string ComputeHash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath)));
        public void SaveWorking() { SaveCount++; File.WriteAllText(SourcePath, "v" + SaveCount); }
        public void Reload() { }
        public byte[] CaptureState() => Array.Empty<byte>();
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
            string r = Responses.Count > 0 ? Responses.Dequeue() : "{\"verdict\":\"ok\"}";
            return Task.FromResult(new ChatResponse(r, new TokenUsage(10, 5), "m"));
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private static ProviderProfile CloudProfile(long budget = 200_000) =>
        new("p", "https://api.test/v1", "m", false, DataPolicy.CloudAllowed,
            new ProfileLimits(SessionBudgetTokens: budget));

    private AgentOrchestrator Make(ScriptedClient client, long budget = 200_000, int rounds = 2) =>
        new(client, new UsageMeter(new ProfileLimits(SessionBudgetTokens: budget)), new EgressPolicy(_dir),
            _engine, _audit, new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), "m", CloudProfile(budget), rounds);

    private const string GoodPlan = "{\"steps\":[{\"kind\":\"ok-op\",\"params\":{}}]}";
    private const string BadPlan = "{\"steps\":[{\"kind\":\"bad-op\",\"params\":{}}]}";
    private const string Ok = "{\"verdict\":\"ok\"}";

    private Task<OrchestrationResult> Run(AgentOrchestrator orch, Func<StepPlan, bool>? confirm = null,
        Func<string>? post = null) =>
        orch.RunAsync("意图", "摘要", _session, "s1", Kinds, "ok-op: 测试",
            confirm ?? (_ => true), () => true, post);

    [Fact]
    public async Task VerifyOk_Executed_OneRound_TwoLlmCalls()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(Ok);
        var result = await Run(Make(client));
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        Assert.Equal(1, result.VerificationRounds);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(30, result.TotalUsage!.PromptTokens + result.TotalUsage.CompletionTokens);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "agent-verify" && r.Entry.ParamsJson.Contains("\"ok\""));
    }

    [Fact]
    public async Task VerifyFix_Iterates_ThenOk()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(GoodPlan); // round1 fix
        client.Responses.Enqueue(Ok);       // round2 ok
        var result = await Run(Make(client));
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        Assert.Equal(2, result.VerificationRounds);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(2, _session.SaveCount); // initial + fix
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "agent-verify" && r.Entry.ParamsJson.Contains("fix"));
    }

    [Fact]
    public async Task FixExecutionFails_Escalated()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(BadPlan);
        var result = await Run(Make(client));
        Assert.Equal(OrchestrationStatus.Escalated, result.Status);
        Assert.Contains("模拟执行失败", result.Error);
    }

    [Fact]
    public async Task VerifyUnparseable_Escalated()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue("看起来没问题");
        var result = await Run(Make(client));
        Assert.Equal(OrchestrationStatus.Escalated, result.Status);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "agent-verify" && r.Entry.ParamsJson.Contains("unparseable"));
    }

    [Fact]
    public async Task RoundsExhausted_Escalated()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(GoodPlan);
        var result = await Run(Make(client, rounds: 2));
        Assert.Equal(OrchestrationStatus.Escalated, result.Status);
        Assert.Equal(2, result.VerificationRounds);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "agent-verify" && r.Entry.ParamsJson.Contains("rounds-exhausted"));
    }

    [Fact]
    public async Task BudgetExhaustedAtVerify_ExecutedWithAuditNote()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        var result = await Run(Make(client, budget: 15));
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        Assert.Equal(0, result.VerificationRounds);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "agent-verify" && r.Entry.ParamsJson.Contains("budget-skipped"));
    }

    [Fact]
    public async Task FixRejectedByHuman_RejectedByHuman()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(GoodPlan);
        bool first = true;
        var result = await Run(Make(client), confirm: _ =>
        {
            if (first) { first = false; return true; }
            return false;
        });
        Assert.Equal(OrchestrationStatus.RejectedByHuman, result.Status);
    }

    [Fact]
    public async Task PostSummaryProvider_InvokedWithPostExecuteState()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(GoodPlan);
        client.Responses.Enqueue(Ok);
        string? captured = null;
        var result = await Run(Make(client), post: () =>
        {
            captured = "POST:" + _session.SaveCount;
            return captured;
        });
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        Assert.Equal("POST:1", captured);
        Assert.Contains(client.Requests[1].Messages, m => m.Content.Contains("POST:1"));
    }

    [Fact]
    public async Task ReplanSuccess_AlsoEntersVerifyLoop()
    {
        var client = new ScriptedClient();
        client.Responses.Enqueue(BadPlan);   // initial fails
        client.Responses.Enqueue(GoodPlan);  // replan ok
        client.Responses.Enqueue(Ok);        // verify ok
        var result = await Run(Make(client));
        Assert.Equal(OrchestrationStatus.Executed, result.Status);
        Assert.Equal(1, result.VerificationRounds);
        Assert.Equal(3, client.Requests.Count);
    }
}