# CAD 智能体终端 · 硬前置与 LLM 配置鉴权 实现计划（计划 3a）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task (Side chat 内联执行，不用子代理). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 关闭计划 3 硬前置（Insert 哈希投影/Lighting 语义类/闸口上下文重载/嵌套 params 契约/handle 契约/跨进程串行化），交付 §8 LLM 配置鉴权全套（profile/DPAPI 密钥/模型发现/预设/Ollama 探测/预算/数据出境/CLI llm 命令族）——全部离线可测。

**Architecture:** Core 新增 Llm 子域（profile/keystore/discovery/budget/policy，厂商无关）；Data 哈希投影扩展+handle 契约；Core 闸口上下文接口加性；Cli llm 命令族。真实 LLM HTTP 调用仅在 `llm test` 显式命令发生，测试全用 fake HTTP。渲染+编排=3b，COM=3c，布灯=3d。

**Tech Stack:** .NET 8、System.Security.Cryptography.ProtectedData（DPAPI，NuGet）、System.Net.Http、System.Text.Json、YamlDotNet、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §8.1-8.6、§12 计划3硬前置与遗留。

## Global Constraints

- Core 不引用 ACadSharp/厂商库；DPAPI 仅 Windows（程序集已 SupportedOSPlatform("windows")）。
- **密钥硬规则（§8.2）**：API Key 不落明文配置——DPAPI CurrentUser 加密，配置存引用；环境变量 CADAGENT_LLM_API_KEY 注入优先；密钥永不进审计/日志/错误报告/配置导出（Redactor 统一脱敏）；http:// 端点警示（localhost 除外）。
- 读图任务强制视觉能力（caps.vision）——不满足的 profile 不可选（3b 编排消费，本计划落地 caps 字段与校验）。
- 数据出境（§8.5）：profile dataPolicy∈{LocalOnly, CloudAllowed}；项目级 LocalOnly 硬约束覆盖 profile；首次云端发送图纸数据须显式确认（确认状态持久化，3b 消费）。
- 预算（§8.4）：总额度/重置周期/会话上限；用量计量；接近耗尽预警（80%）；超额→停用决定（降级策略留 3b）。
- 审计：llm 配置操作入审计（llm-profile-added/llm-profile-used/llm-test）；**paramsJson 不含密钥**（Redactor 守卫测试）。
- 闸口上下文重载加性——既有 ISemanticGate 行为不变；342 既有测试持续绿。
- builtin 治理哈希不变（本计划不改 builtin YAML）；SemanticClass 加性 Lighting（aliases 解析自动支持）。
- TDD；pristine；feat:/fix: 前缀；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）；EOF 换行字节核验。
- CLI UTF-8；Windows GBK 不回显中文；源文件 UTF-8。
- 预设端点常量：DashScope https://dashscope.aliyuncs.com/compatible-mode/v1 ；OpenAI https://api.openai.com/v1 ；Ollama http://localhost:11434/v1（探测端口 11434）。

## 文件结构

```
src/CadAgent.Core/
  Llm/LlmProfile.cs        LlmProfile/Caps/Limits/DataPolicy/ProfileStore
  Llm/KeyStore.cs          DPAPI 保护/解保护 + 环境变量注入 + KeyRef
  Llm/Redactor.cs          脱敏（密钥模式替换）
  Llm/ModelDiscovery.cs    IHttp 抽象 + /v1/models 发现 + 预设 + Ollama 探测
  Llm/BudgetMeter.cs       套餐/用量/预警/超额
  Llm/EgressPolicy.cs      出境决策 + 首次确认持久化
  SemanticGate.cs          加性 ISemanticContextGate
  Skills/StepTemplate.cs   嵌套 params 递归替换
src/CadAgent.Data/
  DrawingSession.cs        ComputeHash Insert 投影扩展
  Checks/...               不动
  Handlers/HandlerHelpers.cs  Find 接受字符串/数字 handle
  Semantic/SemanticGate.cs 上下文重载实现（实体级）
src/CadAgent.Cli/
  CommandRouter.Llm.cs     partial：llm add/list/test/use
tests/...
```

---

### Task 1: ComputeHash Insert 投影扩展（Data）

**Files:**
- Modify: `src/CadAgent.Data/DrawingSession.cs`（ComputeHash 投影 switch）
- Test: `tests/CadAgent.Data.Tests/InsertHashProjectionTests.cs`

**Interfaces:**
- Consumes: ACadSharp Insert（InsertPoint XYZ, XScale/YScale/ZScale double, Rotation double radians, BlockRecord.Name）
- Produces: ComputeHash 对 Insert 追加 `|I|{BlockName}|{InsertPoint XYZ R}|{XScale R}|{YScale R}|{ZScale R}|{Rotation R}`——Insert 移动/缩放/旋转/换块→哈希变化

- [ ] **Step 1: 写失败测试**

```csharp
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class InsertHashProjectionTests : IDisposable
{
    private readonly string _path;
    public InsertHashProjectionTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var br = new BlockRecord("LUM");
        br.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(10, 0) });
        doc.BlockRecords.Add(br);
        doc.Entities.Add(new Insert(br) { InsertPoint = (XYZ)new XY(100, 100) });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void InsertMove_ChangesHash()
    {
        var s = DrawingSession.Load(_path);
        string before = s.ComputeHash();
        var ins = (Insert)s.Doc.Entities.First(e => e is Insert);
        ins.InsertPoint = (XYZ)new XY(200, 100);
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void InsertScaleRotation_ChangesHash()
    {
        var s = DrawingSession.Load(_path);
        string before = s.ComputeHash();
        var ins = (Insert)s.Doc.Entities.First(e => e is Insert);
        ins.XScale = 2.0;
        Assert.NotEqual(before, s.ComputeHash());
        string mid = s.ComputeHash();
        ins.Rotation = Math.PI / 4;
        Assert.NotEqual(mid, s.ComputeHash());
    }

    [Fact]
    public void InsertHash_StableAcrossSaveReload()
    {
        var s = DrawingSession.Load(_path);
        string before = s.ComputeHash();
        s.SaveWorking();
        s.Reload();
        Assert.Equal(before, s.ComputeHash());
    }
}
```
（BlockRecord API 以 XML 文档核实：Entities 集合/构造；既有 InsertBlockTests 有夹具先例可参照。）

- [ ] **Step 2: RED** — `dotnet test --filter InsertHashProjection`（Move 测试当前哈希不变→红）。
- [ ] **Step 3: 实现** — ComputeHash switch 加 `case Insert ins:` 追加投影段（R 格式 InvariantCulture，沿用既有投影风格）。
- [ ] **Step 4: GREEN** + 全套件（治理哈希不受影响——builtin YAML 未动；既有哈希测试值可能变化？既有测试断言哈希相等/不等关系而非字面值——核查无字面哈希断言后放行）。
- [ ] **Step 5: Commit** — `feat: ComputeHash Insert projection (point/scale/rotation/block) — plan-3 prerequisite`

---

### Task 2: SemanticClass.Lighting + 闸口上下文重载（Core/Data）

**Files:**
- Modify: `src/CadAgent.Core/SemanticModel.cs`（enum 加 Lighting）、`src/CadAgent.Core/SemanticGate.cs`（ISemanticContextGate）、`src/CadAgent.Core/StepEngine.cs`（上下文闸口分支）、`src/CadAgent.Data/Semantic/SemanticGate.cs`（实现）
- Test: `tests/CadAgent.Core.Tests/ContextGateTests.cs`、`tests/CadAgent.Data.Tests/EntityLevelGateTests.cs`

**Interfaces:**
- Produces:
  - `SemanticClass.Lighting`（enum 末尾加性）
  - `interface ISemanticContextGate : ISemanticGate { GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle, SemanticOverlay overlay); }`
  - Data.SemanticGate 实现 CheckContext：targetHandle 非空→**实体级**：该句柄须有每个所需类的标注且 Confidence≥minConfidence（禁触区精确到实体）；targetHandle null→退化为操作级（既有 Check 语义）
  - StepEngine：闸口 is ISemanticContextGate→从 step.Params 取 "handle"（字符串或数字→string，无则 null）调 CheckContext；否则既有 Check。**既有 ISemanticGate 路径行为不变**
  - 拒绝审计 kind 沿用 semantic-gate-refused（paramsJson 加 handle 字段）

- [ ] **Step 1: 写失败测试**

Core（fake context gate + engine 分支）：
```csharp
namespace CadAgent.Core.Tests;

public class ContextGateTests
{
    private sealed class FakeSession : IDrawingSession
    {
        public string SourcePath => "f.dxf";
        public string ComputeHash() => "h";
        public void SaveWorking() { }
        public void Reload() { }
    }
    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }
    private sealed class AwareHandler : IStepHandler, ISemanticAware
    {
        public string Kind => "need-lighting";
        public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Lighting };
        public StepResult Apply(IDrawingSession s, System.Text.Json.JsonElement p) => new(true, "", "h1");
    }
    private sealed class ContextGate : ISemanticContextGate
    {
        public string? LastHandle;
        public GateDecision Check(IReadOnlyList<SemanticClass> required) => new(true, "");
        public GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle, SemanticOverlay overlay)
        {
            LastHandle = targetHandle;
            return targetHandle == "h-ok" ? new GateDecision(true, "") : new GateDecision(false, $"实体 {targetHandle} 缺少语义");
        }
    }
    private static StepPlan Plan(string handleJson) =>
        new("p1", new[] { new StepSpec("need-lighting", System.Text.Json.JsonDocument.Parse(handleJson).RootElement) });

    [Fact]
    public void Engine_PassesHandle_ToContextGate()
    {
        var gate = new ContextGate();
        var engine = new RuleEngineHost(gate); // 见下：直接用 StepEngine
        var result = engine.Run(Plan("""{"handle":"h-ok"}"""), new FakeSession(), "s1");
        Assert.True(result.Ok);
        Assert.Equal("h-ok", gate.LastHandle);
    }

    [Fact]
    public void Engine_ContextRefusal_AuditedWithHandle()
    {
        var gate = new ContextGate();
        var engine = new RuleEngineHost(gate);
        var result = engine.Run(Plan("""{"handle":"h-bad"}"""), new FakeSession(), "s1");
        Assert.False(result.Ok);
        Assert.Contains("h-bad", result.Error);
    }

    [Fact]
    public void Engine_NoHandle_PassesNull()
    {
        var gate = new ContextGate();
        var engine = new RuleEngineHost(gate);
        var result = engine.Run(Plan("{}"), new FakeSession(), "s1");
        Assert.False(result.Ok); // null → 拒绝（fake）
        Assert.Null(gate.LastHandle);
    }

    // 辅助：构造带审计/快照/身份的 StepEngine（临时目录，IDisposable 清理）
    private sealed class RuleEngineHost : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ctx-{Guid.NewGuid():N}");
        public StepEngine Engine { get; }
        public RuleEngineHost(ISemanticGate gate)
        {
            Directory.CreateDirectory(_dir);
            var audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
            Engine = new StepEngine(audit, new SnapshotStore(Path.Combine(_dir, "s")),
                new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
                new IStepHandler[] { new AwareHandler() }, gate);
        }
        public TxResult Run(StepPlan p, IDrawingSession s, string sess) => Engine.Run(p, s, sess);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_dir, true);
        }
    }
}
```
（测试用 RuleEngineHost 简化构造；Dispose 经 using 或测试内调用——实现者补 using 模式。数字 handle `{"handle":72}` → "72" 传递补一条测试。）

Data（实体级）：
```csharp
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class EntityLevelGateTests
{
    [Fact]
    public void ContextGate_EntityAnnotationRequired()
    {
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(overlay);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, "h1", overlay).Allowed);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "h2", overlay).Allowed); // 无标注实体
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null, overlay).Allowed);  // 操作级退化
        Assert.False(gate.CheckContext(new[] { SemanticClass.Wall }, null, overlay).Allowed);     // 操作级缺类
    }

    [Fact]
    public void LightingClass_Parseable()
    {
        Assert.Equal(SemanticClass.Lighting, Enum.Parse<SemanticClass>("Lighting", true));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — enum 加值；接口；StepEngine 分支（handle 提取：ValueKind Number→GetUInt64.ToString，String→GetString，其他→null）；Data CheckContext。
- [ ] **Step 4: GREEN + 全套件**（既有闸口测试不变——非上下文闸口走原路径）。
- [ ] **Step 5: Commit** — `feat: SemanticClass.Lighting + entity-level context gate (additive) — plan-3 prerequisite`

---

### Task 3: 嵌套 params 模板契约（Core）

**Files:**
- Modify: `src/CadAgent.Core/Skills/StepTemplate.cs`（TemplateInstantiator 递归替换）
- Test: `tests/CadAgent.Core.Tests/NestedParamsTests.cs`

**Interfaces:**
- Produces: 模板 params 值为 Dictionary<string,object>（嵌套 map）或 List<object>（数组）时**递归替换**其中精确 "{{name}}" 字符串；占位校验同既有（未知占位拒绝，含嵌套内）；数值保持；\z 精确整串规则不变。契约文档注释：嵌套结构经 Instantiate 后为 JSON 对象/数组（StepPlan params 自由形式，处理器消费）。

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class NestedParamsTests
{
    private static StepTemplate T(Dictionary<string, object> stepParams) =>
        new("t", "", new[] { "x" }, new[] { new TemplateStep("add-line", stepParams) });

    [Fact]
    public void NestedMap_PlaceholderSubstituted()
    {
        var t = T(new Dictionary<string, object>
        {
            ["x1"] = "{{x}}",
            ["nested"] = new Dictionary<string, object> { ["inner"] = "{{x}}", ["keep"] = 5 }
        });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 7 });
        var p = plan.Steps[0].Params;
        Assert.Equal(7, p.GetProperty("x1").GetInt32());
        Assert.Equal(7, p.GetProperty("nested").GetProperty("inner").GetInt32());
        Assert.Equal(5, p.GetProperty("nested").GetProperty("keep").GetInt32());
    }

    [Fact]
    public void NestedList_PlaceholderSubstituted()
    {
        var t = T(new Dictionary<string, object> { ["pts"] = new List<object> { "{{x}}", 0, "{{x}}" } });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 3 });
        var arr = plan.Steps[0].Params.GetProperty("pts");
        Assert.Equal(3, arr[0].GetInt32());
        Assert.Equal(3, arr[2].GetInt32());
    }

    [Fact]
    public void NestedUnknownPlaceholder_Rejected()
    {
        var t = T(new Dictionary<string, object>
        {
            ["nested"] = new Dictionary<string, object> { ["inner"] = "{{bogus}}" }
        });
        Assert.Throws<InvalidSkillException>(() =>
            TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 1, ["bogus"] = 2 }));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — 替换函数递归：string→占位匹配；Dictionary→新字典递归值；List→新列表递归元素；其他原样。未知占位校验同样递归。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: nested params recursive placeholder substitution contract — plan-3 prerequisite`

---

### Task 4: handle 契约统一（Data）

**Files:**
- Modify: `src/CadAgent.Data/Handlers/HandlerHelpers.cs`（Find）
- Test: `tests/CadAgent.Data.Tests/HandleContractTests.cs`

**Interfaces:**
- Produces: Find 接受 params["handle"] 为 JSON 数字（GetUInt64）或字符串（ulong.Parse InvariantCulture）；非法字符串→StepResult 失败路径（Find 返回 null→"句柄不存在"）；Summarize 字符串句柄可直接回灌计划（LLM 编写层契约统一）。

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class HandleContractTests : IDisposable
{
    private readonly string _path;
    public HandleContractTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Delete_StringHandle_Works()
    {
        var s = DrawingSession.Load(_path);
        string h = s.Summarize().Entities[0].Handle; // 字符串
        var r = new DeleteEntityHandler().Apply(s, JsonDocument.Parse($"{{\"handle\":\"{h}\"}}").RootElement);
        Assert.True(r.Ok);
        Assert.Empty(s.Summarize().Entities);
    }

    [Fact]
    public void Delete_NumericHandle_StillWorks()
    {
        var s = DrawingSession.Load(_path);
        string h = s.Summarize().Entities[0].Handle;
        var r = new DeleteEntityHandler().Apply(s, JsonDocument.Parse($"{{\"handle\":{h}}}").RootElement);
        Assert.True(r.Ok);
    }

    [Fact]
    public void Delete_InvalidStringHandle_FailsGracefully()
    {
        var s = DrawingSession.Load(_path);
        var r = new DeleteEntityHandler().Apply(s, JsonDocument.Parse("""{"handle":"not-a-number"}""").RootElement);
        Assert.False(r.Ok);
    }
}
```

- [ ] **Step 2: RED**（字符串 handle 当前抛 InvalidOperationException→引擎捕获为失败，但 Delete_InvalidString 期望优雅失败结果——当前 Find 抛异常被引擎 catch 也是失败结果；区分：handler 内 Find 抛 vs 返回 null。测试断言 r.Ok=false 两者皆满足——RED 点在 StringHandle_Works：当前 GetUInt64 对字符串抛→r.Ok=false→红）。
- [ ] **Step 3: 实现** — Find：ValueKind Number→GetUInt64；String→ulong.TryParse 失败返回 null；其他→null。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: handle contract accepts string or numeric JSON — plan-3 prerequisite`

---

### Task 5: 跨进程审计串行化（Core）

**Files:**
- Modify: `src/CadAgent.Core/SqliteAuditStore.cs`（Append 事务 BEGIN IMMEDIATE + busy 重试）
- Test: `tests/CadAgent.Core.Tests/CrossInstanceAuditTests.cs`

**Interfaces:**
- Produces: Append 用 `BEGIN IMMEDIATE`（写锁提前获取）；SQLITE_BUSY→重试（最多 5 次，间隔 50ms×attempt）；跨实例并发追加链完整（既有每实例锁保留——双保险）。

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class CrossInstanceAuditTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"xinst-{Guid.NewGuid():N}.db");
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
    }

    private static AuditEntry E(string k) =>
        new(DateTimeOffset.UtcNow, "T\\u", "Draw", "s", "test", k, "t", "{}", "b", "a", "", "p");

    [Fact]
    public void TwoInstances_ConcurrentAppend_ChainValid()
    {
        using var a = new SqliteAuditStore(_db);
        using var b = new SqliteAuditStore(_db);
        Parallel.For(0, 40, i =>
        {
            var store = i % 2 == 0 ? a : b;
            store.Append(E($"k{i}"));
        });
        Assert.Equal(40, a.ReadAll().Count);
        Assert.True(a.VerifyChain(out _));
        Assert.True(b.VerifyChain(out _));
    }
}
```
（当前代码跨实例并发可能 BUSY 抛异常或 PK 冲突——测试红；IMMEDIATE+重试后绿。若当前恰好绿（锁竞争窗口小），加压力：Parallel.For 0..200 + 两实例各自循环追加以放大窗口；实现者确保测试在修复前可复现红——用 barrier 同步两实例首追加。）

- [ ] **Step 2: RED**（复现竞争失败）。
- [ ] **Step 3: 实现** — conn.BeginTransaction(deferred:false)→BEGIN IMMEDIATE（Microsoft.Data.Sqlite：`conn.BeginTransaction(System.Data.IsolationLevel.Serializable)` 产生 BEGIN IMMEDIATE？核实：SqliteConnection BeginTransaction(IsolationLevel.Serializable)→BEGIN IMMEDIATE；Deferred→BEGIN。用 Serializable）；busy 重试 catch SqliteException ErrorCode=5(BUSY)/6(LOCKED)。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: cross-process audit serialization BEGIN IMMEDIATE + busy retry — plan-3 prerequisite`

---

### Task 6: LLM profile 模型与 DPAPI 密钥库（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/LlmProfile.cs`、`KeyStore.cs`、`Redactor.cs`
- Modify: `src/CadAgent.Core/CadAgent.Core.csproj`（加 System.Security.Cryptography.ProtectedData 8.0.0）
- Test: `tests/CadAgent.Core.Tests/LlmProfileStoreTests.cs`、`KeyStoreTests.cs`、`RedactorTests.cs`

**Interfaces:**
- Produces:
  - `enum DataPolicy { LocalOnly, CloudAllowed }`
  - `sealed record LlmCaps(bool Vision)`
  - `sealed record LlmLimits(int TimeoutSeconds = 60, int MaxTokensPerCall = 4096, long SessionTokenBudget = 200000, long? PlanTotalQuota = null, int? PlanResetDays = null)`
  - `sealed record LlmProfile(string Name, string Endpoint, string Model, string AuthScheme, LlmCaps Caps, LlmLimits Limits, DataPolicy DataPolicy, string? KeyRef)`（KeyRef=密钥库引用名，非密钥）
  - `sealed class ProfileStore`：ctor(string configDir)；`Save(LlmProfile)`（profiles.json，原子写）；`Load(name)`；`List()`；`SetDefault(name)`/`Default`（default.txt）；**配置 JSON 不含密钥**（KeyRef 仅引用）
  - `static class KeyStore`：`string Protect(string keyName, string secret)`→DPAPI CurrentUser 加密存 {configDir}/keys/{keyName}.bin，返回 keyName；`string? Unprotect(string configDir, string keyName)`；`string? Resolve(string configDir, string? keyRef)`——**环境变量 CADAGENT_LLM_API_KEY 优先**（有则直接返回，不解密）；无引用→null（本地无 key profile）
  - `static class Redactor`：`string Scrub(string text, IEnumerable<string> secrets)`——每个 secret 非空→替换 "***"；`string ScrubProfile(LlmProfile p)` 不需要（profile 无密钥）；审计 paramsJson 守卫：含 key 模式（sk- 前缀等）也替换（防御性正则 `sk-[A-Za-z0-9]{8,}`→"***"）

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class KeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}");
    public KeyStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ProtectUnprotect_RoundTrip()
    {
        KeyStore.Protect(_dir, "p1", "sk-secret-12345678");
        Assert.Equal("sk-secret-12345678", KeyStore.Unprotect(_dir, "p1"));
        // 密文文件不含明文
        string cipher = File.ReadAllText(Path.Combine(_dir, "keys", "p1.bin"));
        Assert.DoesNotContain("sk-secret", cipher);
    }

    [Fact]
    public void Resolve_EnvVarOverrides()
    {
        KeyStore.Protect(_dir, "p1", "stored-key");
        Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", "env-key");
        try { Assert.Equal("env-key", KeyStore.Resolve(_dir, "p1")); }
        finally { Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", null); }
    }

    [Fact]
    public void Resolve_NoRef_Null()
    {
        Assert.Null(KeyStore.Resolve(_dir, null));
    }
}

public class RedactorTests
{
    [Fact]
    public void Scrub_RemovesSecrets()
    {
        Assert.Equal("key=***", Redactor.Scrub("key=sk-abcdefgh12345678", new[] { "sk-abcdefgh12345678" }));
    }

    [Fact]
    public void Scrub_DefensivePattern()
    {
        Assert.DoesNotContain("sk-", Redactor.Scrub("oops sk-abcdefgh12345678 leaked", Array.Empty<string>()));
    }
}

public class LlmProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"prof-{Guid.NewGuid():N}");
    public LlmProfileStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void SaveLoad_List_Default()
    {
        var store = new ProfileStore(_dir);
        var p = new LlmProfile("云端主力", "https://api.openai.com/v1", "gpt-4o", "bearer",
            new LlmCaps(true), new LlmLimits(), DataPolicy.CloudAllowed, "p1");
        store.Save(p);
        store.SetDefault("云端主力");
        Assert.Equal("云端主力", store.Default);
        Assert.Single(store.List());
        Assert.Equal("p1", store.Load("云端主力")!.KeyRef);
        // 配置文件不含密钥材料
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(_dir, "profiles.json")));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)；原子写沿用 tmp+Move 模式；Redactor 正则+字面替换。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: LLM profile store + DPAPI key vault + redactor (§8.1/8.2)`

---

### Task 7: 模型发现/预设/Ollama 探测（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/ModelDiscovery.cs`
- Test: `tests/CadAgent.Core.Tests/ModelDiscoveryTests.cs`

**Interfaces:**
- Produces:
  - `interface ILlmHttp { Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct); }`（可 fake；生产 HttpClient 包装）
  - `sealed record Preset(string Name, string Endpoint, bool RequiresKey, string? DefaultModel)`
  - `static class Presets`：All = [DashScope(endpoint 常量, RequiresKey true, DefaultModel "qwen-vl-max"), OpenAI(..., "gpt-4o"), Ollama(http://localhost:11434/v1, RequiresKey false, DefaultModel null)]
  - `sealed class ModelDiscovery(ILlmHttp http)`：`Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, string? apiKey, CancellationToken)`——GET {endpoint}/models（endpoint 已含 /v1；Bearer 头若有 key）；解析 data[].id；非 200→LlmDiscoveryException（状态码+脱敏 body）
  - `Task<bool> ProbeOllamaAsync(CancellationToken)`——GET http://localhost:11434/v1/models 短超时（2s）；成功→true
  - `static bool IsLocalhost(string endpoint)`——Uri.Host ∈ {localhost, 127.0.0.1, ::1}
  - `static bool IsInsecureHttp(string endpoint)`——scheme http && !IsLocalhost

- [ ] **Step 1: 写失败测试**

```csharp
using System.Net;
using System.Text;

namespace CadAgent.Core.Tests;

public class ModelDiscoveryTests
{
    private sealed class FakeHttp : ILlmHttp
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public HttpRequestMessage? Last;
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        { Last = req; return Task.FromResult(Handler(req)); }
    }

    [Fact]
    public async Task ListModels_ParsesIds_AndBearer()
    {
        var http = new FakeHttp
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"m1"},{"id":"m2"}]}""", Encoding.UTF8, "application/json") }
        };
        var d = new ModelDiscovery(http);
        var models = await d.ListModelsAsync("https://api.test/v1", "sk-abcdefgh12345678", default);
        Assert.Equal(new[] { "m1", "m2" }, models);
        Assert.Equal("Bearer", http.Last!.Headers.Authorization!.Scheme);
        Assert.EndsWith("/v1/models", http.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListModels_Error_ThrowsSanitized()
    {
        var http = new FakeHttp
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("bad key sk-abcdefgh12345678", Encoding.UTF8) }
        };
        var ex = await Assert.ThrowsAsync<LlmDiscoveryException>(() =>
            new ModelDiscovery(http).ListModelsAsync("https://api.test/v1", "sk-abcdefgh12345678", default));
        Assert.DoesNotContain("sk-", ex.Message); // 脱敏
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public void Presets_Three_LocalhostDetection()
    {
        Assert.Equal(3, Presets.All.Count);
        Assert.True(ModelDiscovery.IsLocalhost("http://localhost:11434/v1"));
        Assert.True(ModelDiscovery.IsLocalhost("http://127.0.0.1:11434/v1"));
        Assert.False(ModelDiscovery.IsLocalhost("https://api.openai.com/v1"));
        Assert.True(ModelDiscovery.IsInsecureHttp("http://remote.test/v1"));
        Assert.False(ModelDiscovery.IsInsecureHttp("http://localhost:11434/v1"));
    }

    [Fact]
    public async Task ProbeOllama_FalseWhenUnreachable()
    {
        var http = new FakeHttp { Handler = _ => throw new HttpRequestException("refused") };
        Assert.False(await new ModelDiscovery(http).ProbeOllamaAsync(default));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — HttpClient 包装生产 ILlmHttp（DefaultHttp）；异常消息经 Redactor.Scrub；Probe 短超时 CancellationTokenSource。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: model discovery + presets + Ollama probe with sanitized errors (§8.3)`

---

### Task 8: 预算计量与数据出境策略（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/BudgetMeter.cs`、`EgressPolicy.cs`
- Test: `tests/CadAgent.Core.Tests/BudgetEgressTests.cs`

**Interfaces:**
- Produces:
  - `sealed class BudgetMeter`：ctor(LlmLimits limits, string statePath)；`RecordUsage(long tokens)`（持久化累计 usage.json 原子写）；`long UsedInPeriod`（PlanResetDays 周期重置——按首次使用日+周期计算）；`bool WarningReached`（≥80% PlanTotalQuota 或 SessionTokenBudget）；`bool Exceeded`（>quota 或 session 累计>SessionTokenBudget）；`ResetSession()`；无 PlanTotalQuota→仅会话帽
  - `enum EgressDecision { Allowed, BlockedLocalOnly, NeedsFirstCloudConsent }`
  - `sealed class EgressPolicy`：ctor(string projectDir)；`EgressDecision Evaluate(LlmProfile profile, bool drawingData)`——项目 local-only.txt 存在→BlockedLocalOnly（硬约束）；profile.LocalOnly→非 localhost 端点 BlockedLocalOnly；drawingData && CloudAllowed && 非 localhost && 未确认→NeedsFirstCloudConsent；否则 Allowed；`ConfirmCloudConsent()`（写 cloud-consent.json 含时间戳）；`bool ConsentGiven`
  - localhost 判定复用 ModelDiscovery.IsLocalhost

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class BudgetEgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bud-{Guid.NewGuid():N}");
    public BudgetEgressTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static LlmProfile P(DataPolicy policy, string endpoint = "https://api.test/v1") =>
        new("p", endpoint, "m", "bearer", new LlmCaps(true),
            new LlmLimits(SessionTokenBudget: 1000, PlanTotalQuota: 10000, PlanResetDays: 30), policy, null);

    [Fact]
    public void Budget_SessionCap_AndWarning()
    {
        var m = new BudgetMeter(new LlmLimits(SessionTokenBudget: 1000, PlanTotalQuota: 10000), Path.Combine(_dir, "u.json"));
        m.RecordUsage(700);
        Assert.False(m.WarningReached); // 700/1000=70% <80%
        m.RecordUsage(150);
        Assert.True(m.WarningReached);  // 850/1000=85%
        Assert.False(m.Exceeded);
        m.RecordUsage(200);
        Assert.True(m.Exceeded);        // 1050>1000
        m.ResetSession();
        Assert.False(m.Exceeded);
    }

    [Fact]
    public void Budget_PersistsAcrossInstances()
    {
        string path = Path.Combine(_dir, "u2.json");
        var m1 = new BudgetMeter(new LlmLimits(SessionTokenBudget: 1000), path);
        m1.RecordUsage(500);
        var m2 = new BudgetMeter(new LlmLimits(SessionTokenBudget: 1000), path);
        Assert.True(m2.WarningReached == false);
        m2.RecordUsage(400);
        Assert.True(m2.WarningReached); // 900/1000
    }

    [Fact]
    public void Egress_ProjectLocalOnly_BlocksCloud()
    {
        File.WriteAllText(Path.Combine(_dir, "local-only.txt"), "");
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
    }

    [Fact]
    public void Egress_FirstCloudSend_NeedsConsent_ThenAllowed()
    {
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.NeedsFirstCloudConsent, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        policy.ConfirmCloudConsent();
        Assert.Equal(EgressDecision.Allowed, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        // 非图纸数据不需确认
        var policy2 = new EgressPolicy(Path.Combine(_dir, "p2"));
        Directory.CreateDirectory(Path.Combine(_dir, "p2"));
        Assert.Equal(EgressDecision.Allowed, policy2.Evaluate(P(DataPolicy.CloudAllowed), false));
    }

    [Fact]
    public void Egress_LocalProfile_RemoteEndpoint_Blocked()
    {
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.LocalOnly), true));
        Assert.Equal(EgressDecision.Allowed, policy.Evaluate(P(DataPolicy.LocalOnly, "http://localhost:11434/v1"), true));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — 周期重置：usage.json 存 periodStart+used；now>periodStart+ResetDays→重置；原子写。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: token budget metering + data egress policy with first-cloud consent (§8.4/8.5)`

---

### Task 9: CLI llm 命令族（Cli）

**Files:**
- Create: `src/CadAgent.Cli/CommandRouter.Llm.cs`（partial）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（路由 switch 加 llm 分支）
- Test: `tests/CadAgent.Cli.Tests/LlmCommandTests.cs`

**Interfaces:**
- Consumes: T6-T8 全部
- Produces:
  - `llm add --name <n> (--preset dashscope|openai|ollama | --endpoint <url>) [--key <k>] [--model <m>] [--vision] [--local-only] [--project dir]`：preset→端点常量；key→KeyStore.Protect（KeyRef=name）；--model 缺省→preset.DefaultModel 或发现列表首个；discovery 经 ILlmHttp（CLI 用生产 DefaultHttp；**测试经环境变量 CADAGENT_LLM_HTTP_FAKE？不——CLI 测试用离线模式：--offline 旗标跳过 discovery**，模型须 --model 显式）；http 非 localhost→stderr 警示；Save profile；审计 llm-profile-added（paramsJson={name, endpoint, model, vision, dataPolicy}——**无 key**）；exit 0
  - `llm list [--project dir]`：profiles JSON（name/endpoint/model/caps/dataPolicy/default 标记，**无 KeyRef 也无密钥**）→exit 0
  - `llm test --name <n> [--project dir]`：Resolve key→ListModelsAsync 实弹（生产 HTTP）→成功 stdout 模型数+视觉探针？**视觉探针第一 slice：仅连通+模型列表；视觉实弹留 3b**；失败→脱敏 stderr+exit 1；审计 llm-test（{name, ok, modelCount}）
  - `llm use --name <n> [--project dir]`：SetDefault+审计 llm-profile-used→exit 0
  - `llm detect-local [--project dir]`：ProbeOllamaAsync→探到→自动创建"本地保密"profile（Ollama 端点, LocalOnly, vision=false 注记, 无 key）+审计；未探到→stdout 提示 exit 0
  - 配置目录：{project}/.cadagent/llm/；未知 llm 子命令→exit 1
  - **密钥经 --key 命令行传入仅用于 Protect 立即加密——stdout/审计/错误永不回显**（Redactor 守卫）

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Cli.Tests;

public class LlmCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"llmcli-{Guid.NewGuid():N}");
    public LlmCommandTests() => Directory.CreateDirectory(_project);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void AddOffline_Preset_ProfileSaved_NoKeyInOutputOrAudit()
    {
        var (code, stdout, stderr) = Run("llm", "add", "--name", "测试云", "--preset", "openai",
            "--key", "sk-abcdefgh12345678", "--model", "gpt-4o", "--vision", "--offline");
        Assert.Equal(0, code);
        Assert.DoesNotContain("sk-", stdout + stderr);
        var (lc, listOut, _) = Run("llm", "list");
        Assert.Equal(0, lc);
        Assert.Contains("测试云", listOut);
        Assert.DoesNotContain("sk-", listOut);
        // 审计无密钥
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-", r.Entry.ParamsJson));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "llm-profile-added");
        // DPAPI 密文存在
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "llm", "keys", "测试云.bin")));
    }

    [Fact]
    public void AddInsecureHttp_Warns()
    {
        var (_, _, stderr) = Run("llm", "add", "--name", "远端http", "--endpoint", "http://remote.test/v1",
            "--model", "m", "--offline");
        Assert.Contains("http", stderr); // 警示
    }

    [Fact]
    public void Use_SetsDefault_Audited()
    {
        Run("llm", "add", "--name", "p1", "--preset", "ollama", "--model", "llava", "--offline");
        var (code, _, _) = Run("llm", "use", "--name", "p1");
        Assert.Equal(0, code);
        var (_, listOut, _) = Run("llm", "list");
        Assert.Contains("default", listOut.ToLower());
    }

    [Fact]
    public void LocalOnly_Flag_ProfilePolicy()
    {
        Run("llm", "add", "--name", "保密", "--preset", "ollama", "--model", "llava", "--local-only", "--offline");
        var store = new CadAgent.Core.Llm.ProfileStore(Path.Combine(_project, ".cadagent", "llm"));
        Assert.Equal(CadAgent.Core.Llm.DataPolicy.LocalOnly, store.Load("保密")!.DataPolicy);
    }

    [Fact]
    public void UnknownSubcommand_ExitOne()
    {
        var (code, _, _) = Run("llm", "bogus");
        Assert.Equal(1, code);
    }
}
```
（llm test 实弹命令离线测试不覆盖真实网络——测试仅验证缺 profile→exit 1 路径；实弹冒烟留用户配置后手动。）

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — partial 文件；--offline 跳过 discovery（模型须显式）；审计经 OpenAudit/Identity 既有模式；Redactor 守卫所有输出。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: CLI llm add/list/test/use/detect-local with DPAPI + audit + redaction (§8.3)`

---

### Task 10: 离线 E2E 与承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/LlmConfigE2ETests.cs`
- Modify: spec §12

**Interfaces:**
- E2E：add openai preset（offline, key, vision）→list（无密钥）→use→add ollama local-only→项目 local-only.txt→EgressPolicy 评估云端 profile 被阻→ConfirmCloudConsent 后仍被项目硬约束阻→预算计量会话帽触发→审计链绿（llm-profile-added/used 无密钥）→KeyStore env 覆盖生效
- spec §12 追加 3a 完成记录：硬前置六项关闭（Insert 投影/Lighting/上下文闸口实体级/嵌套 params/handle 契约/跨进程 IMMEDIATE）；§8 配置鉴权落地（DPAPI/发现/预设/探测/预算/出境/CLI）；3b 承接（渲染+编排消费 caps.vision/EgressPolicy/BudgetMeter/ILlmHttp；视觉实弹探针；llm test 真实冒烟待用户 key）；3c/3d 承接不变

- [ ] **Step 1: E2E 测试**（完整代码）
- [ ] **Step 2: 运行**
- [ ] **Step 3: 全套件**
- [ ] **Step 4: spec §12**
- [ ] **Step 5: Commit** — `test: LLM config offline end-to-end; docs: plan-3a completion and 3b handoff`

---

## 自审记录

- §8 覆盖：8.1 profile（T6）/8.2 DPAPI+脱敏+env+http 警示（T6/T9）/8.3 发现+预设+探测+CLI（T7/T9；首启向导按钮=视图 plan4，CLI 等价 detect-local）/8.4 预算（T8）/8.5 出境+首次确认+项目硬约束（T8）/8.6 用量审计（T9 审计 kind；token 用量记录在 3b 编排消费 BudgetMeter.RecordUsage）。
- 硬前置六项：T1 Insert 投影/T2 Lighting+上下文闸口/T3 嵌套 params/T4 handle/T5 跨进程；审计外部锚=3c（审计包）。
- 契约一致性：LlmProfile/KeyStore/ProfileStore/ModelDiscovery/BudgetMeter/EgressPolicy 跨 T6-T10 签名一致；ISemanticContextGate 加性不破坏既有闸口测试。
- 治理哈希：builtin YAML 不动；SemanticClass 加值不影响 YAML 内容哈希。
- 离线可测：所有 HTTP 经 ILlmHttp fake；llm test 实弹仅显式命令；DPAPI 真机 Windows 可测。
- 诚实边界：视觉实弹探针/首启向导图形按钮/订阅 OAuth/降级策略=3b/plan4/后续；--offline 旗标为测试与无网环境设计。