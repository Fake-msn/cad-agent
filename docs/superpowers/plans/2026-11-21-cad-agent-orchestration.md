# CAD 智能体终端 · 编排循环核心 实现计划（计划 3b-i）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans（Side chat 内联执行）. Steps use checkbox (`- [ ]`) syntax.

**Goal:** 智能体自主作业核心闭环：数据出境管控 → 图纸文本摘要读图 → LLM 生成步骤计划 → schema+kind 校验 → 人工确认 → StepEngine 执行（既有审计/快照/闸口）→ 读回 → 失败一次重规划或升级人工；llm-call 全程审计+预算计量。全离线可测（fake ILlmClient）。

**Architecture:** Core 新增 EgressPolicy/AgentPlanParser/AgentOrchestrator（厂商无关，消费 ILlmClient/StepEngine/UsageMeter）；Data 新增 DrawingSummarizer（文本摘要，视觉留 3c/计划4）；Cli agent 命令（LlmClientFactory 注入既有）。签认/步级 undo/REPL=3b-ii。

**Tech Stack:** .NET 8、System.Text.Json、xUnit（fake ILlmClient + 真实引擎/审计/临时图纸）。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §3 边界2（LLM 主导+引擎审计执行）、§4.1②④⑤、§8.5/8.6、§9；§12 3b 承接。

## Global Constraints

- **LLM 不碰图纸旁路**：LLM 输出仅步骤计划 JSON；一切修改经 StepEngine（审计/快照/闸口既有管线）；计划必经 schema 校验+kind 白名单+人工确认。
- **数据出境（§8.5）**：项目 local-only 硬约束 > profile 策略；首次云端发送图纸数据须显式确认（持久化）；localhost 一等公民。
- **审计**：llm-call（model/usage/planId/attempt，**无图纸内容无密钥**）/agent-run/egress-blocked/egress-consent/plan-rejected；失败全部入审计。
- **预算**：每次 ChatAsync 前 EnsureBudget；超额→编排终止（Failed 状态）。
- **重规划有界**：执行失败→错误反馈 LLM 重规划一次→再确认→再执行；仍失败→Escalated（升级人工），绝不静默继续。
- Core 不引用 ACadSharp/厂商；3b-i 不改 StepEngine 内部（消费既有公共 API）；411 既有测试持续绿。
- TDD；pristine；feat: 前缀；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）；EOF 换行。
- CLI UTF-8；Windows GBK 不回显中文；源文件 UTF-8。

## 文件结构

```
src/CadAgent.Core/Llm/EgressPolicy.cs     EgressDecision/EgressPolicy
src/CadAgent.Core/Llm/AgentPlanParser.cs  ExtractPlan（JSON 提取+schema+kind 白名单）
src/CadAgent.Core/Llm/AgentOrchestrator.cs OrchestrationResult/AgentOrchestrator
src/CadAgent.Data/DrawingSummarizer.cs    文本摘要（有界截断）
src/CadAgent.Cli/CommandRouter.Agent.cs   partial：agent 命令
tests/...
```

---

### Task 1: EgressPolicy（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/EgressPolicy.cs`
- Test: `tests/CadAgent.Core.Tests/EgressPolicyTests.cs`

**Interfaces:**
- Consumes: ProviderProfile/DataPolicy（既有）
- Produces:
  - `enum EgressDecision { Allowed, BlockedLocalOnly, NeedsFirstCloudConsent }`
  - `sealed class EgressPolicy`：ctor(string projectDir)；`EgressDecision Evaluate(ProviderProfile profile, bool drawingData)`——项目 `local-only.txt` 存在→BlockedLocalOnly（硬约束覆盖一切）；profile.LocalOnly && 非 localhost→BlockedLocalOnly；drawingData && CloudAllowed && 非 localhost && 未确认→NeedsFirstCloudConsent；否则 Allowed；`void ConfirmCloudConsent()`（写 cloud-consent.json 含时间戳，原子写）；`bool ConsentGiven`；localhost 判定（Uri.Host ∈ localhost/127.0.0.1/::1）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

public class EgressPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"egress-{Guid.NewGuid():N}");
    public EgressPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static ProviderProfile P(DataPolicy policy, string endpoint = "https://api.test/v1") =>
        new("p", endpoint, "m", false, policy, new ProfileLimits());

    [Fact]
    public void ProjectLocalOnly_BlocksEverything()
    {
        File.WriteAllText(Path.Combine(_dir, "local-only.txt"), "");
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.CloudAllowed, "http://localhost:11434/v1"), true));
    }

    [Fact]
    public void LocalProfile_RemoteEndpoint_Blocked_LocalhostAllowed()
    {
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.LocalOnly), true));
        Assert.Equal(EgressDecision.Allowed, policy.Evaluate(P(DataPolicy.LocalOnly, "http://localhost:11434/v1"), true));
    }

    [Fact]
    public void CloudProfile_DrawingData_FirstConsentThenAllowed()
    {
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.NeedsFirstCloudConsent, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        policy.ConfirmCloudConsent();
        Assert.True(policy.ConsentGiven);
        Assert.Equal(EgressDecision.Allowed, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        // 新实例持久化
        Assert.True(new EgressPolicy(_dir).ConsentGiven);
    }

    [Fact]
    public void CloudProfile_NonDrawingData_NoConsentNeeded()
    {
        Assert.Equal(EgressDecision.Allowed, new EgressPolicy(_dir).Evaluate(P(DataPolicy.CloudAllowed), false));
    }

    [Fact]
    public void CloudProfile_Localhost_NoConsentNeeded()
    {
        Assert.Equal(EgressDecision.Allowed,
            new EgressPolicy(_dir).Evaluate(P(DataPolicy.CloudAllowed, "http://127.0.0.1:11434/v1"), true));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — 原子写 tmp+Move；consent JSON {consentedAt}。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: data egress policy with project hard constraint and first-cloud consent (§8.5)`

---

### Task 2: DrawingSummarizer 文本摘要（Data）

**Files:**
- Create: `src/CadAgent.Data/DrawingSummarizer.cs`
- Test: `tests/CadAgent.Data.Tests/DrawingSummarizerTests.cs`

**Interfaces:**
- Consumes: DrawingSession/Summarize、SemanticOverlay
- Produces: `static class DrawingSummarizer`：`string Summarize(DrawingSession session, SemanticOverlay? overlay, int maxChars = 8000)`——结构化文本：源文件名/版本/图层列表/实体按类型计数/文本实体内容清单（值+字高+图层，上限 50 条）/块名清单/语义标注摘要（类×来源×置信度计数+人工映射列表）；超 maxChars 截断+"…[截断]"；**确定性输出**（ordinal 排序）；无 overlay→语义段注明"无语义叠加层"

- [ ] **Step 1: 写失败测试**

```csharp
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class DrawingSummarizerTests : IDisposable
{
    private readonly string _path;
    public DrawingSummarizerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("WALL"));
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0),
            Layer = doc.Layers.First(l => l.Name == "WALL") });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "客厅", Height = 5 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "卧室", Height = 5 });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Summarize_ContainsLayersCountsTexts()
    {
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), null);
        Assert.Contains("WALL", s);
        Assert.Contains("客厅", s);
        Assert.Contains("卧室", s);
        Assert.Contains("Line", s);
        Assert.Contains("无语义叠加层", s);
    }

    [Fact]
    public void Summarize_WithOverlay_IncludesSemantics()
    {
        var overlay = new SemanticOverlay();
        overlay.ConfirmMapping(new LayerMapping("WALL", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), overlay);
        Assert.Contains("Wall", s);
        Assert.Contains("HumanConfirmed", s);
    }

    [Fact]
    public void Summarize_Truncation_Bounded()
    {
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), null, maxChars: 50);
        Assert.True(s.Length <= 60); // 50 + 截断标记
        Assert.Contains("截断", s);
    }

    [Fact]
    public void Summarize_Deterministic()
    {
        var session = DrawingSession.Load(_path);
        Assert.Equal(DrawingSummarizer.Summarize(session, null), DrawingSummarizer.Summarize(session, null));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — StringBuilder 分段；文本清单取前 50（ordinal 按句柄）；截断在 maxChars 处+标记。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: bounded deterministic drawing text summary for LLM reading (vision deferred)`

---

### Task 3: AgentPlanParser（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/AgentPlanParser.cs`
- Test: `tests/CadAgent.Core.Tests/AgentPlanParserTests.cs`

**Interfaces:**
- Produces: `static class AgentPlanParser`：`StepPlan ExtractPlan(string llmResponse, IReadOnlySet<string> allowedKinds)`——
  1. 提取 JSON：```json 围栏块优先，否则首个 `{` 到末个 `}` 子串；无→InvalidPlanException("响应不含 JSON 计划")
  2. StepPlanParser.Parse（schema 校验既有）
  3. kind 白名单：每步 kind ∈ allowedKinds，否则 InvalidPlanException 列非法 kind
  4. planId 缺省时 LLM 可能不给——StepPlanParser 要求 planId；**加性裁定：ExtractPlan 在解析前若 JSON 无 planId 则注入 "llm-{Guid:N}"**（LLM 不必编造 id）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

public class AgentPlanParserTests
{
    private static readonly HashSet<string> Kinds = new() { "add-line", "add-text" };

    [Fact]
    public void Extract_FencedJson_Works()
    {
        string resp = "好的，计划如下：\n```json\n{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}}]}\n```\n请确认。";
        var plan = AgentPlanParser.ExtractPlan(resp, Kinds);
        Assert.Single(plan.Steps);
        Assert.StartsWith("llm-", plan.PlanId); // 注入的 planId
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
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — 围栏正则 ```json\s*(.*?)```（Singleline）；回退首{末}；JsonDocument 解析→无 planId 注入→重新序列化→StepPlanParser.Parse；kind 校验。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: agent plan extraction with JSON fence parsing, planId injection, kind whitelist`

---

### Task 4: AgentOrchestrator（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/AgentOrchestrator.cs`
- Test: `tests/CadAgent.Core.Tests/AgentOrchestratorTests.cs`

**Interfaces:**
- Consumes: ILlmClient/ChatRequest/ChatMessage/TokenUsage、UsageMeter、EgressPolicy、StepEngine/StepPlan/TxResult、IAuditStore、IIdentityProvider、DrawingSummarizer 经回调注入（Core 不引用 Data——**摘要由调用方传入 string**）
- Produces:
  - `enum OrchestrationStatus { Executed, BlockedEgress, RejectedByHuman, Failed, Escalated, BudgetExceeded }`
  - `sealed record OrchestrationResult(OrchestrationStatus Status, string? TxId, StepPlan? Plan, string? Error, TokenUsage? TotalUsage)`
  - `sealed class AgentOrchestrator`：ctor(ILlmClient client, UsageMeter meter, EgressPolicy egress, StepEngine engine, IAuditStore audit, IIdentityProvider identity, string model)
  - `Task<OrchestrationResult> RunAsync(string intent, string drawingSummary, IDrawingSession session, string sessionId, IReadOnlySet<string> allowedKinds, string kindDocs, Func<StepPlan, bool> confirmPlan, Func<bool>? confirmCloudEgress = null, CancellationToken ct = default)`：
    1. egress.Evaluate(profile?——**orchestrator 不知 profile**：egress 评估由调用方预检？**裁定：ctor 加 ProviderProfile profile 参数**，Evaluate(profile, drawingData:true)；Blocked→审计 egress-blocked+返回 BlockedEgress；NeedsFirstCloudConsent→confirmCloudEgress?.Invoke() ?? false→false→审计 egress-blocked+BlockedEgress；true→egress.ConfirmCloudConsent()+审计 egress-consent
    2. 预算 EnsureBudget（超额→BudgetExceeded 结果，审计 agent-run status）
    3. ChatAsync（system: 你是 CAD 制图智能体…只输出 JSON 计划 {steps:[{kind,params}]}，可用 kind 文档 kindDocs；user: intent + drawingSummary）→llm-call 审计（model/usage/attempt=1/planId 提取后）→meter.Record
    4. ExtractPlan（失败→审计 agent-run invalid-plan+Failed 结果，不重规划——解析失败非执行失败）
    5. confirmPlan(plan)→false→审计 plan-rejected+RejectedByHuman
    6. engine.Run→Ok→审计 agent-run executed+Executed(TxId, plan, usage)
    7. 失败→错误反馈第二次 ChatAsync（attempt=2，llm-call 审计）→ExtractPlan→confirmPlan→engine.Run→Ok→Executed；仍失败→审计 agent-run escalated+Escalated
  - 审计 Source="agent"（llm-call Source="llm"）；paramsJson 无图纸内容/密钥

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

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
        Assert.Equal(15, result.TotalUsage!.PromptTokens + result.TotalUsage.CompletionTokens);
        var all = _audit.ReadAll();
        Assert.Contains(all, r => r.Entry.Kind == "llm-call");
        Assert.Contains(all, r => r.Entry.Kind == "agent-run");
        Assert.Contains(all, r => r.Entry.Kind == "egress-consent");
        Assert.All(all, r => Assert.DoesNotContain("摘要", r.Entry.ParamsJson)); // 图纸内容不入审计
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
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("模拟执行失败", client.Requests[1].Messages[^1].Content); // 错误反馈
        Assert.Equal(2, _audit.ReadAll().Count(r => r.Entry.Kind == "llm-call"));
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
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — 按契约；ctor 含 ProviderProfile；system prompt 含 kindDocs+JSON-only 指令；重规划 user 消息含上次错误；TotalUsage 累计两次调用。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: agent orchestrator — egress gate, plan validation, human confirm, bounded replan, llm-call audit`

---

### Task 5: CLI agent 命令（Cli）

**Files:**
- Create: `src/CadAgent.Cli/CommandRouter.Agent.cs`（partial）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（路由 case "agent"）
- Test: `tests/CadAgent.Cli.Tests/AgentCommandTests.cs`

**Interfaces:**
- Consumes: T1-T4 + 既有 LlmClientFactory/LlmConfigStore/DpapiKeyProtector/LoadOrReject/Identity/OpenAudit + HandlerRegistry + SemanticGate（overlay 存在时）
- Produces:
  - `agent --drawing <file> --intent <text> [--project dir] [--yes] [--name <profile>]`：
    1. active/指定 profile（无→stderr exit 1）；key ResolveKey（无→exit 1）
    2. LoadOrReject 图纸；overlay 存在→Load（损坏→审计拒绝）
    3. DrawingSummarizer.Summarize
    4. allowedKinds = HandlerRegistry.All().Select(Kind)；kindDocs = 内置参数字典（add-line {x1,y1,x2,y2,layer?} 等六处理器）
    5. confirmPlan：--yes→自动 true（审计 agent-run 注记 autoConfirmed）；否则 stdout 打印计划 JSON + stderr 提示"确认执行? [y/N]"→stdin 读行（Console.ReadLine 经注入 TextReader？CLI 测试用 --yes 路径；交互路径手动冒烟）
    6. confirmCloudEgress：--yes→true；否则 stderr 提示+stdin
    7. Orchestrator.RunAsync→stdout 结果 JSON {status, txId, error}→exit 0（Executed）/1（其他）
    8. 审计 agent-run（orchestrator 内部已审；CLI 不重复）
  - 生产 ILlmClient = LlmClientFactory?.Invoke ?? new OpenAiClient(profile, key)

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

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
            Task.FromResult(new ChatResponse(Plan, new TokenUsage(10, 5), "m"));
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
        // 图纸真的多了一条线
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
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — partial 文件；kindDocs 常量字符串（六处理器参数）；--yes 双确认自动；交互确认经 Console.ReadLine（测试不覆盖交互路径，手动冒烟注记）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: CLI agent command — full orchestration loop with confirm gates and audit`

---

### Task 6: 离线 E2E + 承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/OrchestrationE2ETests.cs`
- Modify: spec §12

**Interfaces:**
- E2E：llm add/use → agent --yes（fake plan 含 add-line+add-text）→ 图纸实体+2 → 审计全链（egress-consent/llm-call/agent-run/tx-start/step×2/tx-commit）无密钥无图纸内容 → 重规划路径（fake 首答 bad kind? 用执行失败 handler 不可——CLI 用真实 HandlerRegistry；重规划 E2E 用 Core 层已覆盖；CLI E2E 覆盖：成功/出境阻断/无效计划/无 profile）→ 二次 agent 调用同项目（consent 已持久化，不再询问）
- spec §12 追加 3b-i 完成记录：编排闭环落地（出境管控/摘要读图/计划校验/人工确认/有界重规划/llm-call 审计/预算）；3b-ii 承接（签认/步级 undo/REPL 交互会话）；视觉读图留 3c/计划4；交互确认路径手动冒烟待真实终端

- [ ] **Step 1: E2E 测试**（完整代码）
- [ ] **Step 2: 运行**
- [ ] **Step 3: 全套件**
- [ ] **Step 4: spec §12**
- [ ] **Step 5: Commit** — `test: orchestration end-to-end (offline); docs: plan-3b-i carry-overs`

---

## 自审记录

- §3 边界2 落地：LLM 仅产计划 JSON，ExtractPlan schema+kind 白名单，confirmPlan 人工闸门，engine 唯一修改入口。
- §8.5 出境：项目硬约束>profile>首次确认；localhost 豁免；consent 持久化。
- §8.6 用量：llm-call 审计 model/usage/attempt；EnsureBudget 前置；BudgetExceeded 终止。
- §9 错误：解析失败→Failed 不重规划；执行失败→一次重规划→Escalated；出境阻断/人工拒绝各有审计事件。
- 契约一致性：OrchestrationResult/Status 跨 T4-T6；ctor 含 ProviderProfile（T4 测试 Make 一致）；摘要 string 注入（Core 不引用 Data）。
- 411 既有测试保护；StepEngine 内部不改。
- 诚实边界：交互确认（stdin）测试不覆盖（--yes 路径全测）；视觉读图 deferred；签认/步级 undo=3b-ii。