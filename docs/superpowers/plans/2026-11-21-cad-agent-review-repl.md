# CAD 智能体终端 · 签认与步级撤回与 REPL 实现计划（计划 3b-ii）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans（Side chat 内联执行）.

**Goal:** 审查签认闭环（review 角色+职责分离+驳回回滚+防重复签认）；步级 undo（会话内模型快照栈，审计留痕）；REPL 交互会话（持久图纸会话+意图编排+:undo/:rollback/:status/:quit）。

**Architecture:** IDrawingSession 加性 CaptureState/RestoreState；Core ModelUndoStack + StepEngine 加性 undo 集成；Cli review 命令族 + repl 命令（IdentityOverride 测试注入）。

**Tech Stack:** .NET 8、ACadSharp 3.8.35、xUnit。

**Spec:** spec §4.1⑥（审查签认）、§5.3（职责分离）、§5.4（步级 undo）、§7（REPL）；§12 3b-ii 承接。

## Global Constraints

- 职责分离：签认人 ≠ 事务执行人（audit SnapshotRef 含 txId 溯源）；review 角色强制（RoleChecker.Ensure Review）；重复签认拒绝。
- 驳回→engine.Rollback（既有快照回滚+审计）；批准→审计 review-approved（不改图纸）。
- 步级 undo：会话内（引擎实例持有栈）；每步执行前 CaptureState 入栈；undo→RestoreState+审计 step-undone；空栈→false 不审计；跨会话仍靠事务快照（既有）。
- IDrawingSession 加性（CaptureState/RestoreState）——既有实现/测试不破坏；FakeSession 测试夹具需补实现。
- REPL：持久 DrawingSession；意图→orchestrator（stdin 确认）；:undo/:rollback/:status/:quit；EOF 退出；REPL 内 orchestrator 每意图新 tx。
- 审计：review-approved/review-rejected/step-undone/repl-session 事件；438 既有测试持续绿。
- TDD；feat: 前缀；git 署名环境变量；EOF 换行；Core 不引用 ACadSharp。

---

### Task 1: IDrawingSession 状态捕获/恢复（Core/Data）

**Files:**
- Modify: `src/CadAgent.Core/StepEngine.cs`（IDrawingSession 加性两方法）、`src/CadAgent.Data/DrawingSession.cs`（实现）
- Modify: 既有 FakeSession 夹具（Core.Tests 的 StepEngineTests/ContextGateTests/AgentOrchestratorTests/SemanticGateTests 等——补 CaptureState/RestoreState 最小实现：内存字节数组）
- Test: `tests/CadAgent.Data.Tests/SessionStateTests.cs`

**Interfaces:**
- `IDrawingSession` 加性：`byte[] CaptureState(); void RestoreState(byte[] state);`
- DrawingSession：CaptureState = DXF 序列化到 MemoryStream 返回字节；RestoreState = DxfReader 从 MemoryStream 读回 Doc

- [ ] **Step 1: 写失败测试**

```csharp
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class SessionStateTests : IDisposable
{
    private readonly string _path;
    public SessionStateTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void CaptureRestore_RoundTrip_UndoEntityAdd()
    {
        var s = DrawingSession.Load(_path);
        byte[] state = s.CaptureState();
        s.Doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(5, 5) });
        Assert.Equal(2, s.Summarize().Entities.Count);
        s.RestoreState(state);
        Assert.Single(s.Summarize().Entities);
    }

    [Fact]
    public void CaptureState_DoesNotMutateFile()
    {
        var s = DrawingSession.Load(_path);
        byte[] before = File.ReadAllBytes(_path);
        s.CaptureState();
        Assert.Equal(before, File.ReadAllBytes(_path));
    }
}
```

- [ ] **Step 2: RED**（编译失败：接口缺方法）。
- [ ] **Step 3: 实现** + 补全部 FakeSession 夹具（`private byte[] _state = ...; CaptureState()=>_state; RestoreState(b)=>_state=b;` 最小）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: IDrawingSession state capture/restore for step-level undo (additive)`

---

### Task 2: ModelUndoStack + StepEngine undo 集成（Core）

**Files:**
- Create: `src/CadAgent.Core/ModelUndoStack.cs`
- Modify: `src/CadAgent.Core/StepEngine.cs`（ctor 加性可选 ModelUndoStack? undoStack = null；Run 每步前 push；UndoLastStep 方法）
- Test: `tests/CadAgent.Core.Tests/StepUndoTests.cs`

**Interfaces:**
- `sealed class ModelUndoStack`：`void Push(byte[] state)`；`byte[]? Pop()`；`int Count`
- StepEngine ctor 尾部加性 `ModelUndoStack? undoStack = null`；Run 每步 Apply 前 `_undoStack?.Push(session.CaptureState())`
- `bool UndoLastStep(IDrawingSession session, string sessionId)`：栈空→false；Pop→session.RestoreState→审计 step-undone（Source="plan", target=session.SourcePath）→true

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;

namespace CadAgent.Core.Tests;

public class StepUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"undo-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly ModelUndoStack _stack = new();
    private readonly StepEngine _engine;
    private readonly FakeSession _session;

    public StepUndoTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
        _session = new FakeSession(Path.Combine(_dir, "d.dxf"));
        _engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(_dir, "s")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AddHandler() }, null, _stack);
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FakeSession : IDrawingSession
    {
        private byte[] _state;
        public FakeSession(string path) { SourcePath = path; _state = new byte[] { 0 }; File.WriteAllBytes(path, _state); }
        public string SourcePath { get; }
        public string ComputeHash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_state));
        public void SaveWorking() => File.WriteAllBytes(SourcePath, _state);
        public void Reload() => _state = File.ReadAllBytes(SourcePath);
        public byte[] CaptureState() => _state;
        public void RestoreState(byte[] state) { _state = state; File.WriteAllBytes(SourcePath, state); }
    }
    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }
    private sealed class AddHandler : IStepHandler
    {
        public string Kind => "mutate";
        public StepResult Apply(IDrawingSession s, JsonElement p)
        {
            s.RestoreState(new byte[] { 1, 2, 3 }); // 模拟修改
            return new StepResult(true, "", "h1");
        }
    }

    private static StepPlan Plan(int n) =>
        new("p1", Enumerable.Range(0, n).Select(_ => new StepSpec("mutate", JsonDocument.Parse("{}").RootElement)).ToList());

    [Fact]
    public void Run_PushesPerStep_UndoRestoresPrevious()
    {
        var result = _engine.Run(Plan(2), _session, "s1");
        Assert.True(result.Ok);
        Assert.Equal(2, _stack.Count);
        Assert.True(_engine.UndoLastStep(_session, "s1"));
        Assert.Equal(1, _stack.Count);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "step-undone");
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Undo_EmptyStack_False_NoAudit()
    {
        Assert.False(_engine.UndoLastStep(_session, "s1"));
        Assert.DoesNotContain(_audit.ReadAll(), r => r.Entry.Kind == "step-undone");
    }

    [Fact]
    public void NoStack_RunUnchanged()
    {
        var engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(_dir, "s2")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AddHandler() });
        Assert.True(engine.Run(Plan(1), _session, "s2").Ok);
        Assert.False(engine.UndoLastStep(_session, "s2"));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: step-level undo via in-session model snapshot stack (audited)`

---

### Task 3: review 签认命令族（Cli）

**Files:**
- Create: `src/CadAgent.Cli/CommandRouter.Review.cs`（partial）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（路由 case "review"；internal static IIdentityProvider? IdentityOverride 测试注入）
- Test: `tests/CadAgent.Cli.Tests/ReviewCommandTests.cs`

**Interfaces:**
- `review signoff --drawing <file> --tx <txId> (--approve | --reject) [--comment <text>] [--project dir]`：
  1. IdentityOverride ?? Identity(agentDir)；RoleChecker.Ensure(op, Role.Review)（不足→exit 1）
  2. 溯源：audit.ReadAll 找 SnapshotRef 含 txId 的首条记录→executor 账号；无→stderr"事务不存在"+exit 1
  3. 职责分离：op.Account == executor（OrdinalIgnoreCase）→stderr"绘制者不能自审"+exit 1
  4. 防重复：已有 review-approved/review-rejected 含 txId→exit 1
  5. --approve→审计 review-approved {txId, comment}→exit 0
  6. --reject→审计 review-rejected {txId, comment}→engine.Rollback(txId, drawing, session)（既有 rollback 审计）→drawing.Reload→exit 0
  7. --approve 与 --reject 互斥/必填其一→exit 1
- IdentityOverride：internal static（测试设 reviewer 身份；Dispose 清理）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class ReviewCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public ReviewCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.IdentityOverride = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private string RunTx()
    {
        string plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(plan, """{"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":9,"y2":9}}]}""");
        var (code, stdout, _) = Run("run", plan, "--drawing", _drawing);
        Assert.Equal(0, code);
        return System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("txId").GetString()!;
    }

    private static CadAgent.Core.IIdentityProvider Reviewer() =>
        new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
    private sealed class FixedId : CadAgent.Core.IIdentityProvider
    {
        public FixedId(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    [Fact]
    public void Approve_ReviewerRole_Audited()
    {
        string tx = RunTx();
        CommandRouter.IdentityOverride = Reviewer();
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve", "--comment", "合格");
        Assert.Equal(0, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "review-approved" && r.Entry.ParamsJson.Contains(tx));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void SelfReview_Rejected()
    {
        string tx = RunTx();
        // 默认身份=执行人（Windows 当前账号），角色需 Review——设同账号 Review 角色
        var current = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity(current, Role.Review));
        var (code, _, stderr) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        Assert.Equal(1, code);
        Assert.Contains("自审", stderr);
    }

    [Fact]
    public void Reject_RollsBackDrawing()
    {
        string tx = RunTx();
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities.Count);
        CommandRouter.IdentityOverride = Reviewer();
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--reject", "--comment", "重画");
        Assert.Equal(0, code);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "review-rejected");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "rollback");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void DuplicateSignoff_Rejected()
    {
        string tx = RunTx();
        CommandRouter.IdentityOverride = Reviewer();
        Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        Assert.Equal(1, code);
    }

    [Fact]
    public void DrawRole_CannotSignoff()
    {
        string tx = RunTx();
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\drawer", Role.Draw));
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        Assert.Equal(1, code);
    }

    [Fact]
    public void UnknownTx_ExitOne()
    {
        CommandRouter.IdentityOverride = Reviewer();
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", "nonexistent", "--approve");
        Assert.Equal(1, code);
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**（IdentityOverride 加性；Identity() helper 改为 `IdentityOverride ?? Identity(agentDir)` 模式——注意既有 Identity(agentDir) 调用点不改，review/repl 用 override-aware helper）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: review signoff with role enforcement, separation of duties, reject-rollback`

---

### Task 4: REPL 交互会话（Cli）

**Files:**
- Create: `src/CadAgent.Cli/CommandRouter.Repl.cs`（partial）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（路由 case "repl"）
- Test: `tests/CadAgent.Cli.Tests/ReplTests.cs`

**Interfaces:**
- `repl --drawing <file> [--project dir] [--name <profile>]`：持久 DrawingSession + ModelUndoStack + StepEngine（undoStack）+ audit；循环读 stdin（internal ReplLoop(args, TextReader stdin, TextWriter stdout, TextWriter stderr) 测试注入）：
  - `:quit` / EOF→退出 0
  - `:undo`→engine.UndoLastStep→stdout 结果
  - `:rollback <txId>`→engine.Rollback+Reload
  - `:status`→实体数/栈深/活动 profile
  - 其他行→intent→orchestrator.RunAsync（confirmPlan=stdin y/n；--yes 旗标自动）→stdout 结果 JSON；图纸会话保持（不重新 Load）
  - repl-session 审计（开始/结束）
- 测试：StringReader 多行脚本（intent→y→:undo→:status→:quit）+ fake LLM factory

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

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
            Task.FromResult(new ChatResponse(
                "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":7,\"y2\":7}}]}",
                new TokenUsage(5, 3), "m"));
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
        int code = Repl("画一条线\ny\n:status\n:undo\n:status\n:quit\n", out string stdout);
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        // undo 后实体数回到 1（status 输出含计数）
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
        int code = Repl("画线\nn\n:quit\n", out string stdout);
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
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — ReplLoop internal static；:status 输出 `entities={n} undo={depth} profile={name}`；意图确认读 stdin 行。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: REPL interactive session with persistent drawing, undo, and orchestration`

---

### Task 5: E2E + 承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/ReviewReplE2ETests.cs`
- Modify: spec §12

**Interfaces:**
- E2E：repl 画线→:quit→review signoff --reject（reviewer override）→图纸回滚→审计全链（repl-session/llm-call/tx/review-rejected/rollback）→再 repl 画线→approve→防重复→职责分离（同账号拒绝）
- spec §12 追加 3b-ii 完成记录：签认/步级undo/REPL 落地；3b 全部完成→3c 就绪（中望 COM+保真导出+审计包外部锚+厂商矩阵）；步级 undo 会话内（跨会话事务快照兜底既有）；REPL 视觉反馈留计划4

- [ ] **Step 1: E2E 测试**
- [ ] **Step 2: 运行 + 全套件**
- [ ] **Step 3: spec §12**
- [ ] **Step 4: Commit** — `test: review+repl end-to-end; docs: plan-3b-ii completion, 3c readiness`

---

## 自审记录

- 职责分离溯源：SnapshotRef 含 txId（SnapshotStore.Take 路径 {root}/{txId}/{file}）→audit 查询可行。
- IDrawingSession 加性两方法——全部 FakeSession 夹具需补（T1 列出）；既有 438 测试保护。
- StepEngine ctor 再加性可选参（undoStack）——既有调用零改动。
- IdentityOverride 仅 review/repl 消费；既有命令身份路径不变。
- REPL 测试经 StringReader 全离线；交互确认 y/n 路径被测试覆盖（优于 3b-i 的 stdin 手动冒烟）。