# CAD 智能体终端 · LLM 基础设施 实现计划（计划 3a）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** LLM 接入基础设施：provider profile 模型、DPAPI 密钥安全存储、OpenAI 兼容 HTTP 客户端、/v1/models 自动发现、预设与 Ollama 探测、连通探针、CLI llm 命令族、用量计量与套餐预算——全部可离线测试（mock HTTP），不依赖真实 API key。

**Architecture:** Core 定义 profile 模型/ILlmClient 接口/用量模型（厂商无关）；新项目 CadAgent.Llm 实现 HTTP 客户端+DPAPI+发现（Windows 目标）；Cli llm 命令族。编排（读图/计划生成/确认循环）在 3b。

**Tech Stack:** .NET 8、System.Security.Cryptography.ProtectedData 8.0.0（DPAPI，Windows）、System.Net.Http、System.Text.Json、xUnit（Mock HttpMessageHandler）。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §8（8.1-8.6）；§9 错误处理；§5 审计（llm-call 事件接线在 3b，本计划用量模型先行）。

## Global Constraints

- **密钥硬规则**：API key 不落明文配置——DPAPI（CurrentUser scope）加密存储；配置 JSON 只存引用；环境变量 CADAGENT_LLM_API_KEY 注入优先；密钥永不进日志/审计/错误报告/配置导出（脱敏测试钉死）。
- Core 不引用 ACadSharp/厂商/DPAPI 包；CadAgent.Llm 引用 Core+ProtectedData；Cli 引用 Core+Data+Llm。
- HTTP 测试用 Mock HttpMessageHandler（无真实网络）；Ollama 探测抽象 IEndpointProbe 可 mock。
- profile 名唯一（OrdinalIgnoreCase）；endpoint 必须 http(s)；http 非 localhost 警示（不阻止——本地模型场景）；dataPolicy∈{LocalOnly, CloudAllowed}。
- 用量：token 数从 API 响应 usage 解析；session 预算帽超额→LlmBudgetExceededException（编排层 3b 消费）；套餐（总额度/重置周期）模型先行。
- 一切 CLI llm 操作可审计（llm-profile-added/removed/used 事件；test 探针结果 llm-probe；**密钥值不入审计**）。
- 342 既有测试持续绿；新文件 EOF 换行字节核验；Directory.Build.props 平台属性已覆盖新项目。
- TDD；pristine；feat:/fix: 前缀；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）。
- CLI UTF-8；Windows GBK 不回显中文；源文件 UTF-8。

## 文件结构

```
CadAgent.sln                  + src/CadAgent.Llm + tests/CadAgent.Llm.Tests
src/CadAgent.Core/
  Llm/ProviderProfile.cs      ProviderProfile/DataPolicy/ProfileLimits/TokenPlan + 校验
  Llm/LlmConfigStore.cs       profile JSON 持久化（无密钥）+ active profile
  Llm/ILlmClient.cs           ILlmClient/ChatRequest/ChatResponse/TokenUsage/LlmException 族
  Llm/UsageMeter.cs           会话用量累计+预算检查
src/CadAgent.Llm/
  DpapiKeyProtector.cs        IKeyProtector/DPAPI 实现+环境变量注入
  OpenAiClient.cs             OpenAI 兼容 chat/completions + models 发现
  EndpointProbe.cs            IEndpointProbe/Ollama 探测+预设
  LlmProbe.cs                 连通+补全+视觉探针
src/CadAgent.Cli/
  CommandRouter.Llm.cs        partial：llm add/list/test/use/remove
tests/CadAgent.Llm.Tests/     mock HTTP 测试
tests/CadAgent.Cli.Tests/     llm 命令测试
```

---

### Task 1: 解决方案扩展 + profile 模型与配置存储（Core）

**Files:**
- Modify: `CadAgent.sln`、`src/CadAgent.Cli/CadAgent.Cli.csproj`（引用 Llm）
- Create: `src/CadAgent.Llm/CadAgent.Llm.csproj`、`tests/CadAgent.Llm.Tests/CadAgent.Llm.Tests.csproj`、`src/CadAgent.Core/Llm/ProviderProfile.cs`、`src/CadAgent.Core/Llm/LlmConfigStore.cs`
- Test: `tests/CadAgent.Core.Tests/LlmProfileTests.cs`

**Interfaces:**
- Produces:
  - `enum DataPolicy { LocalOnly, CloudAllowed }`
  - `sealed record TokenPlan(long TotalTokens, int ResetDays)`（可选）
  - `sealed record ProfileLimits(int TimeoutSeconds = 60, int MaxTokensPerCall = 4096, long SessionBudgetTokens = 200_000, TokenPlan? Plan = null)`
  - `sealed record ProviderProfile(string Name, string Endpoint, string Model, bool VisionCapable, DataPolicy DataPolicy, ProfileLimits Limits)`
  - `static class ProfileValidator`：`void Validate(ProviderProfile p)`→InvalidProfileException（name 非空/endpoint http(s) Uri/Model 非空）
  - `sealed class LlmConfigStore`：ctor(string configPath)；`IReadOnlyList<ProviderProfile> Profiles`；`string? ActiveProfileName`；`void Upsert(ProviderProfile p)`（同名替换）；`void Remove(string name)`；`void SetActive(string name)`（不存在→InvalidProfileException）；`void Save()`（JSON 缩进，**无密钥字段**）；`static LlmConfigStore Load(string path)`（缺失→空）
  - `sealed class InvalidProfileException : Exception`

- [ ] **Step 1: 项目骨架**

```bash
dotnet new classlib -n CadAgent.Llm -o src/CadAgent.Llm -f net8.0
dotnet new xunit -n CadAgent.Llm.Tests -o tests/CadAgent.Llm.Tests -f net8.0
dotnet sln add src/CadAgent.Llm tests/CadAgent.Llm.Tests
dotnet add src/CadAgent.Llm reference src/CadAgent.Core
dotnet add src/CadAgent.Llm package System.Security.Cryptography.ProtectedData --version 8.0.0
dotnet add tests/CadAgent.Llm.Tests reference src/CadAgent.Llm src/CadAgent.Core
dotnet add src/CadAgent.Cli reference src/CadAgent.Llm
```
删除 Class1.cs/UnitTest1.cs。

- [ ] **Step 2: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class LlmProfileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"llmcfg-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static ProviderProfile P(string name = "云端主力", string endpoint = "https://api.example.com/v1") =>
        new(name, endpoint, "model-x", true, DataPolicy.CloudAllowed, new ProfileLimits());

    [Fact]
    public void Validate_BadEndpoint_Throws()
    {
        Assert.Throws<InvalidProfileException>(() => ProfileValidator.Validate(P(endpoint: "ftp://x")));
        Assert.Throws<InvalidProfileException>(() => ProfileValidator.Validate(P(endpoint: "not a uri")));
    }

    [Fact]
    public void Validate_EmptyName_Throws()
    {
        Assert.Throws<InvalidProfileException>(() => ProfileValidator.Validate(P(name: "")));
    }

    [Fact]
    public void Store_UpsertReplace_SetActive_Remove_RoundTrip()
    {
        var store = LlmConfigStore.Load(_path);
        store.Upsert(P());
        store.Upsert(P("本地保密", "http://localhost:11434/v1"));
        store.SetActive("本地保密");
        store.Save();
        var loaded = LlmConfigStore.Load(_path);
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Equal("本地保密", loaded.ActiveProfileName);
        loaded.Remove("云端主力");
        Assert.Single(loaded.Profiles);
    }

    [Fact]
    public void SetActive_Unknown_Throws()
    {
        var store = LlmConfigStore.Load(_path);
        Assert.Throws<InvalidProfileException>(() => store.SetActive("nope"));
    }

    [Fact]
    public void Save_ContainsNoSecretFields()
    {
        var store = LlmConfigStore.Load(_path);
        store.Upsert(P());
        store.Save();
        string json = File.ReadAllText(_path);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: RED** — 编译失败。
- [ ] **Step 4: 实现** — records + validator + JSON store（System.Text.Json，camelCase，枚举字符串）。
- [ ] **Step 5: GREEN + 全套件**。
- [ ] **Step 6: Commit** — `feat: LLM provider profile model + secret-free config store; CadAgent.Llm project scaffold`

---

### Task 2: DPAPI 密钥保护器（Llm 项目）

**Files:**
- Create: `src/CadAgent.Llm/DpapiKeyProtector.cs`
- Test: `tests/CadAgent.Llm.Tests/KeyProtectorTests.cs`

**Interfaces:**
- Produces:
  - `interface IKeyProtector { void Store(string profileName, string apiKey); string? Retrieve(string profileName); void Delete(string profileName); }`
  - `sealed class DpapiKeyProtector : IKeyProtector`：ctor(string storeDir)——每 profile 一个文件 `{storeDir}/{name-sanitized}.key`（ProtectedData.Protect CurrentUser）；文件名 sanitize（Path.GetInvalidFileNameChars→'_'，防穿越：拒绝含分隔符/.. 的 profileName→ArgumentException）；Retrieve 缺失→null；**环境变量 CADAGENT_LLM_API_KEY 优先**：`static string? ResolveKey(IKeyProtector protector, string profileName)`——env 非空→env；否则 Retrieve。
  - 密钥值永不 ToString/日志（类无 override ToString 泄漏；测试钉死存储文件非明文）。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Llm;

namespace CadAgent.Llm.Tests;

public class KeyProtectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}");
    public KeyProtectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void StoreRetrieve_RoundTrip_NotPlaintext()
    {
        var p = new DpapiKeyProtector(_dir);
        p.Store("prof", "sk-secret-12345");
        Assert.Equal("sk-secret-12345", p.Retrieve("prof"));
        var files = Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.DoesNotContain("sk-secret-12345", File.ReadAllText(files[0]));
    }

    [Fact]
    public void Retrieve_Missing_Null()
    {
        Assert.Null(new DpapiKeyProtector(_dir).Retrieve("nope"));
    }

    [Fact]
    public void Delete_Removes()
    {
        var p = new DpapiKeyProtector(_dir);
        p.Store("prof", "k");
        p.Delete("prof");
        Assert.Null(p.Retrieve("prof"));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("C:\\x")]
    public void Store_PathTraversal_Rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new DpapiKeyProtector(_dir).Store(name, "k"));
    }

    [Fact]
    public void ResolveKey_EnvOverridesStore()
    {
        var p = new DpapiKeyProtector(_dir);
        p.Store("prof", "stored");
        try
        {
            Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", "env-key");
            Assert.Equal("env-key", DpapiKeyProtector.ResolveKey(p, "prof"));
        }
        finally { Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", null); }
        Assert.Equal("stored", DpapiKeyProtector.ResolveKey(p, "prof"));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — ProtectedData.Protect/Unprotect（UTF8 bytes, CurrentUser, optionalEntropy=null）；sanitize+ traversal 拒绝（同 SnapshotStore 模式）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: DPAPI key protector with env override and traversal guard`

---

### Task 3: ILlmClient + OpenAI 兼容 HTTP 客户端（Core 接口 + Llm 实现）

**Files:**
- Create: `src/CadAgent.Core/Llm/ILlmClient.cs`、`src/CadAgent.Llm/OpenAiClient.cs`
- Test: `tests/CadAgent.Llm.Tests/OpenAiClientTests.cs`

**Interfaces:**
- Produces:
  - Core：`sealed record ChatMessage(string Role, string Content)`；`sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, int MaxTokens, double Temperature = 0.2)`；`sealed record TokenUsage(int PromptTokens, int CompletionTokens)`；`sealed record ChatResponse(string Content, TokenUsage Usage, string Model)`
  - `interface ILlmClient { Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default); Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default); }`
  - `class LlmException : Exception`；`class LlmAuthException : LlmException`（401/403）；`class LlmBudgetExceededException : LlmException`
  - `sealed class OpenAiClient : ILlmClient`：ctor(ProviderProfile profile, string apiKey, HttpMessageHandler? handler = null)——POST {endpoint}/chat/completions（Bearer；body {model, messages, max_tokens, temperature}）；解析 choices[0].message.content + usage；401/403→LlmAuthException（**消息不含 key**）；超时→LlmException；GET {endpoint}/models→data[].id 列表；endpoint 尾斜杠归一。
- [ ] **Step 1: 写失败测试**（Mock HttpMessageHandler 返回固定 JSON；断言请求体/Bearer 头/响应解析/401 异常消息不含 key/模型列表）

```csharp
using System.Net;
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Llm.Tests;

public class OpenAiClientTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string Body = "";
        public HttpStatusCode Code = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Code) { Content = new StringContent(ResponseJson) };
        }
        public string ResponseJson = """
            {"choices":[{"message":{"content":"你好"}}],"usage":{"prompt_tokens":10,"completion_tokens":5},"model":"model-x"}
            """;
    }

    private static ProviderProfile P() => new("t", "https://api.test/v1/", "model-x", false, DataPolicy.CloudAllowed, new ProfileLimits());

    [Fact]
    public async Task Chat_ParsesContentAndUsage_SendsBearer()
    {
        var h = new MockHandler();
        var client = new OpenAiClient(P(), "sk-key", h);
        var resp = await client.ChatAsync(new ChatRequest(new[] { new ChatMessage("user", "hi") }, 100));
        Assert.Equal("你好", resp.Content);
        Assert.Equal(10, resp.Usage.PromptTokens);
        Assert.Equal(5, resp.Usage.CompletionTokens);
        Assert.Equal("Bearer", h.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-key", h.Last.Headers.Authorization.Parameter);
        Assert.Contains("\"max_tokens\":100", h.Body);
        Assert.EndsWith("/chat/completions", h.Last.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Chat_401_ThrowsAuth_NoKeyInMessage()
    {
        var h = new MockHandler { Code = HttpStatusCode.Unauthorized, ResponseJson = "{\"error\":\"bad key\"}" };
        var client = new OpenAiClient(P(), "sk-supersecret", h);
        var ex = await Assert.ThrowsAsync<LlmAuthException>(() => client.ChatAsync(new ChatRequest(new[] { new ChatMessage("user", "hi") }, 10)));
        Assert.DoesNotContain("sk-supersecret", ex.Message);
    }

    [Fact]
    public async Task ListModels_ParsesIds()
    {
        var h = new MockHandler { ResponseJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""" };
        var client = new OpenAiClient(P(), "k", h);
        var models = await client.ListModelsAsync();
        Assert.Equal(new[] { "m1", "m2" }, models);
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: ILlmClient contract + OpenAI-compatible HTTP client (mock-tested, key-safe errors)`

---

### Task 4: 模型发现 + 预设 + Ollama 探测（Llm）

**Files:**
- Create: `src/CadAgent.Llm/EndpointProbe.cs`
- Test: `tests/CadAgent.Llm.Tests/DiscoveryTests.cs`

**Interfaces:**
- Produces:
  - `interface IEndpointProbe { Task<bool> IsReachableAsync(string baseUrl, CancellationToken ct = default); }`
  - `sealed class HttpEndpointProbe : IEndpointProbe`（GET {base}/models 或 /api/tags，2xx/401 均视为可达——401 说明服务在；超时→false）
  - `sealed record LlmPreset(string Name, string Endpoint, string SuggestedModel, bool Local)`；`static class Presets`：All（OpenAI https://api.openai.com/v1、DashScope https://dashscope.aliyuncs.com/compatible-mode/v1、Ollama http://localhost:11434/v1）
  - `static class Discovery`：`Task<IReadOnlyList<string>> DiscoverModelsAsync(ILlmClient client)`（ListModelsAsync 包装，异常→空列表+不抛）；`Task<LlmPreset?> DetectLocalOllamaAsync(IEndpointProbe probe)`（probe localhost:11434→Ollama preset，否则 null）；视觉能力判定 `static bool GuessVisionCapable(string modelId)`（含 vision/vl/gpt-4o/claude/qwen-vl 等子串→true，保守 heuristic，注释注明最终靠探针）

- [ ] **Step 1: 写失败测试**（mock probe/client；预设存在；Ollama 探测正反；vision heuristic）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: model discovery, provider presets, Ollama auto-detect, vision heuristic`

---

### Task 5: 连通探针（Llm）

**Files:**
- Create: `src/CadAgent.Llm/LlmProbe.cs`
- Test: `tests/CadAgent.Llm.Tests/ProbeTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ProbeResult(bool Connected, bool CompletionOk, bool? VisionOk, string? Error, TokenUsage? Usage)`
  - `sealed class LlmProbe`：ctor(ILlmClient client)；`Task<ProbeResult> RunAsync(bool testVision, CancellationToken ct)`——小补全（"回复 OK"，maxTokens 16）；testVision 时发 1×1 PNG data-url 图片消息（content 数组形式——OpenAI vision 格式；client 需支持？第一 slice：ChatMessage.Content 为字符串，vision 探针发特殊构造请求——**裁定：ChatRequest 加性可选 ImageUrl 字段**，OpenAiClient 构造 content 数组）；异常→ProbeResult(false,...,Error 脱敏)；Usage 记录。
  - Core ChatRequest 加性：`string? ImageUrl = null`（默认 null 不破坏既有）。

- [ ] **Step 1: 写失败测试**（mock：补全成功/失败/视觉请求体含 image_url）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: connectivity + completion + vision probe with redacted errors`

---

### Task 6: 用量计量与预算（Core）

**Files:**
- Create: `src/CadAgent.Core/Llm/UsageMeter.cs`
- Test: `tests/CadAgent.Core.Tests/UsageMeterTests.cs`

**Interfaces:**
- Produces:
  - `sealed class UsageMeter`：ctor(ProfileLimits limits)；`long SessionTokens { get; }`；`void Record(TokenUsage usage)`（累计 prompt+completion）；`void EnsureBudget()`（SessionTokens≥SessionBudgetTokens→LlmBudgetExceededException）；`TokenPlan? Plan`；`long PlanRemaining(DateTimeOffset now)`（Plan null→long.MaxValue；重置周期：planStart 起算 ResetDays 窗口——第一 slice：Plan 窗口从首次 Record 起算，`PlanWindowStart` 记录；跨窗重置累计）
  - 审计接线（llm-call 事件）在 3b——本任务纯模型。

- [ ] **Step 1: 写失败测试**（累计/超额异常/套餐窗口重置）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: usage metering with session budget and token-plan window`

---

### Task 7: CLI llm 命令族（Cli）

**Files:**
- Create: `src/CadAgent.Cli/CommandRouter.Llm.cs`（partial）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（路由 case "llm"）
- Test: `tests/CadAgent.Cli.Tests/LlmCommandTests.cs`

**Interfaces:**
- Produces:
  - `llm add --name <n> --endpoint <url> --model <m> [--vision] [--data-policy local|cloud] [--key <k>] [--project dir]`：校验→存 profile（config .cadagent/llm-profiles.json）+key（DPAPI .cadagent/keys/；--key 缺省→ResolveKey env/已存）→审计 llm-profile-added（**paramsJson 不含 key**）→exit 0；校验失败 exit 1
  - `llm list [--project dir]`：profiles+active+dataPolicy JSON（无 key）→exit 0
  - `llm use --name <n> [--project dir]`：SetActive+审计 llm-profile-used→exit 0/1
  - `llm remove --name <n> [--project dir]`：Remove+Delete key+审计 llm-profile-removed→exit 0
  - `llm test [--name <n>] [--vision] [--project dir]`：active/指定 profile→ResolveKey（无 key→stderr exit 1）→LlmProbe.RunAsync→stdout ProbeResult JSON+审计 llm-probe（usage/结果，无 key）→exit 0（Connected&&CompletionOk）/1
  - 未知子命令→exit 1；http 非 localhost endpoint→stderr 警示（不阻止）
  - **测试用 mock**：CLI test 命令需可注入 ILlmClient——裁定：CommandRouter 加性 internal static Func<...>? LlmClientFactory（测试设 mock 工厂，生产默认 OpenAiClient）；InternalsVisibleTo Cli.Tests 已有先例。

- [ ] **Step 1: 写失败测试**（add/list/use/remove 全链+审计无 key 断言+test mock 工厂+无 key exit 1）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: CLI llm add/list/use/remove/test with DPAPI keys and key-free audit`

---

### Task 8: 端到端与承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/LlmE2ETests.cs`
- Modify: spec §12

**Interfaces:**
- E2E（mock HTTP 工厂）：llm add（key 经 --key）→list（无 key 泄漏）→use→test（mock 补全 OK+usage）→审计链绿（llm-profile-added/used/probe 事件，paramsJson 无 key）→remove（key 文件删除）→配置文件全文扫描无 key 明文→env override 路径（CADAGENT_LLM_API_KEY）test 用 env key。
- spec §12 追加 3a 完成记录：profile/DPAPI/发现/预设/探针/CLI/用量预算落地；vision 渲染 deferred（3b 文本摘要读图，视觉留 3c COM 导出/计划4）；编排/确认循环/签认/步级undo/llm-call 审计=3b；COM/保真/审计包/外部锚/厂商矩阵=3c；DPAPI Windows-only（跨平台留后续）；Ollama 探测 heuristic。

- [ ] **Step 1: E2E 测试**（完整代码）
- [ ] **Step 2: 运行**
- [ ] **Step 3: 全套件**
- [ ] **Step 4: spec §12**
- [ ] **Step 5: Commit** — `test: LLM infrastructure end-to-end (mock HTTP); docs: plan-3a carry-overs`

---

## 自审记录

- 密钥安全贯穿：DPAPI 存储/env 优先/配置无密钥/审计无密钥/错误消息无密钥/导出无密钥——各有测试钉死。
- ChatRequest.ImageUrl 加性（T5）不破坏 T3 既有构造；OpenAiClient content 数组分支。
- CLI test 可注入（LlmClientFactory internal）——mock 全链离线。
- 342 既有测试保护；新项目受 Directory.Build.props 平台属性覆盖。
- 诚实边界：vision heuristic 保守（最终靠探针）；DPAPI Windows-only；套餐窗口第一 slice 从首次 Record 起算；llm-call 审计/数据出境确认执行在 3b。