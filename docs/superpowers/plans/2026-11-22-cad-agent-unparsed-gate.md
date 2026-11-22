# 通知感知准入闸口（防假 Green）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 ACadSharp UnknownEntity 暴露盲区导致的假 Green：读取通知 census 进入准入闸口，原生天正图纸判 🟡（只读审查+修改操作全图级拦截+T3 指引），补齐 agent 路径缺失的准入闸检查。

**Architecture:** DrawingSession.LoadDwg 捕获 ICadReader.OnNotification 的 "read as an UnknownEntity" 逐类计数 → UnparsedCensus；Summarize 增 Unparsed 字段（与图内 Unknowns 分离）；GateDetector 三分：Green（无未支持）/Red（无可解析原生实体+全未支持）/Yellow（有原生实体+部分未支持）；Yellow/Red 修改操作在 CLI run/skills/agent + 视图编辑四消费点拒绝留痕；census 经 .cadagent sidecar 持久化（working copy 丢失 TCH 对象后二次加载仍可见）。

**Tech Stack:** .NET 8 / ACadSharp 3.8.35（ICadReader.OnNotification 事件实证可用）/ xUnit + SkippableFact（真实标本 skip-gated）

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §4.2 三级闸口 / §9 审计无旁路 / §12（d655f30 决定性修正条）

**实证基础**：四份原生天正标本 OnNotification 普查数千 TCH UnknownEntity（电施 TCH_WALL 1592 等），doc.Entities/全块表 0；中望打开报"天正代理无法完整显示"（用户观察）；GateDetector 现有逻辑 Red/Yellow 分支正确但 Unknowns 盲；agent 路径（CommandRouter.Agent）无 GateDetector 检查（缺口）。

## Global Constraints

- 分支 `feat/cad-agent-sheet-aware` 追加（head d655f30；与 PR#18/#19 独立；同一 PR 交付）。
- spec ⑥：绝不拿信息残缺图纸直接操作；🟡=只读审查+未解析区禁触（对象图无句柄/几何→全图级修改拦截，诚实降级注记）；拒绝必留痕（run-rejected 既有 kind）。
- 行为保持：无 TCH 图纸 census 空→Green 逐字不变；既有 958 测试全绿回归闸口；Yellow 旧"放行+警示"语义改为拒绝（spec 🟡 本义回归，sanctioned 变更+既有测试期望更新须保持/加强意图）。
- census sidecar 不写用户源目录（.cadagent 项目内）；working copy 既有约定不变。
- 中文注释/消息；EOF 换行；git 署名仅环境变量 Fake-msn（绝不改 config）。
- Windows GBK：dotnet 输出重定向 UTF-8 文件再读；单项目或全 sln（MSB1008）。
- TDD 每任务红→绿→提交；真实标本只读（outputs 副本）。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Create `src/CadAgent.Data/UnparsedCensus.cs` | census record + 通知解析 + sidecar store | 1 |
| Modify `src/CadAgent.Data/DrawingSession.cs` | LoadDwg 通知捕获 + Unparsed 属性 + Load/ctor census 参 + Summarize Unparsed | 1 |
| Modify `src/CadAgent.Data/DrawingSummary.cs` | Unparsed 可选字段 | 1 |
| Modify `src/CadAgent.Data/GateDetector.cs` | 三分逻辑 + Yellow guidance | 2 |
| Modify `src/CadAgent.Cli/CommandRouter.cs:116,183` + `CommandRouter.Skills.cs:112` + `CommandRouter.Agent.cs` | Yellow 拒绝 + agent 闸检查 + sidecar 读写 | 3 |
| Modify `src/CadAgent.View/ViewModels/EditViewModel.cs:186` + `MainViewModel.cs` + `MainWindow.cs` | Yellow 拒绝 + StateNote census 警示 + sidecar | 3 |
| Test 各任务 | 合成 census 单测 + 真实 skip-gated + 拒绝留痕 | 各 |
| Modify spec §12 + 门禁 + 集成 | 收尾 | 5 |

---

### Task 1: census 捕获 + session 暴露 + sidecar 持久化

**Files:**
- Create: `src/CadAgent.Data/UnparsedCensus.cs`
- Modify: `src/CadAgent.Data/DrawingSession.cs`（LoadDwg:93-97 / Load:62-80 / ctor / Summarize:148+）
- Modify: `src/CadAgent.Data/DrawingSummary.cs`（record 末尾加可选参）
- Test: `tests/CadAgent.Data.Tests/UnparsedCensusTests.cs`（新）

**Interfaces:**
- Consumes: ACadSharp `ICadReader.OnNotification`（NotificationEventArgs.Message/NotificationType）；通知文本格式实证："Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity"
- Produces:
  - `public sealed record UnparsedClassBrief(string CppClassName, int Count);`
  - `public sealed record UnparsedCensus(IReadOnlyList<UnparsedClassBrief> Classes) { public static readonly UnparsedCensus Empty; public int TotalCount; public static UnparsedCensus FromNotifications(IEnumerable<string> messages); }`
  - `public static class UnparsedCensusStore { public static UnparsedCensus Load(string projectDir, string sourcePath); public static void Save(string projectDir, string sourcePath, UnparsedCensus census); }`（sidecar `.cadagent/unparsed.json`，键=sourcePath 全路径+源文件 SHA256；源哈希变→视为陈旧返回 Empty）
  - `DrawingSession.Unparsed`（UnparsedCensus 属性）；`Load(string path, UnparsedCensus? persisted = null)`；ctor 加可选 census 参；`Summarize()` 填 `DrawingSummary.Unparsed`
  - `DrawingSummary` 末尾加 `IReadOnlyList<UnknownBrief>? Unparsed = null`

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Data;

namespace CadAgent.Data.Tests;

public class UnparsedCensusTests
{
    [Fact]
    public void FromNotifications_ParsesPerClassCounts()
    {
        var msgs = new[]
        {
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
            "Unlisted object with DXF name TCH_OPENING has been read as an UnknownEntity",
            "Entity in SortEntitiesTable 2996 not found 0", // 非 UnknownEntity 通知→忽略
        };
        var c = UnparsedCensus.FromNotifications(msgs);
        Assert.Equal(3, c.TotalCount);
        Assert.Equal(2, c.Classes.Single(x => x.CppClassName == "TCH_WALL").Count);
        Assert.Equal(1, c.Classes.Single(x => x.CppClassName == "TCH_OPENING").Count);
    }

    [Fact]
    public void FromNotifications_Empty_NoClasses()
    {
        Assert.Equal(0, UnparsedCensus.FromNotifications(Array.Empty<string>()).TotalCount);
        Assert.Equal(0, UnparsedCensus.Empty.TotalCount);
    }

    [Fact]
    public void Sidecar_Roundtrip_And_StaleSourceHash_ReturnsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "a.dwg");
        File.WriteAllText(src, "v1");
        var c = UnparsedCensus.FromNotifications(new[] { "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity" });
        UnparsedCensusStore.Save(dir, src, c);
        Assert.Equal(1, UnparsedCensusStore.Load(dir, src).TotalCount);
        File.WriteAllText(src, "v2"); // 源变→陈旧
        Assert.Equal(0, UnparsedCensusStore.Load(dir, src).TotalCount);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Summarize_IncludesUnparsedBriefs_SeparateFromGraphUnknowns()
    {
        // 合成 session（ctor census 参）：空 doc + census TCH_WALL×2 → Summarize().Unparsed 2 条 UnknownBrief(Handle="")
        // 且 Unknowns（图内）保持空；Entities 空
        var doc = new ACadSharp.CadDocument();
        var session = new DrawingSession("mem.dxf", doc, UnparsedCensus.FromNotifications(new[]
        {
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
        }));
        var s = session.Summarize();
        Assert.Empty(s.Unknowns);
        Assert.Equal(2, s.Unparsed!.Count);
        Assert.All(s.Unparsed!, u => Assert.Equal("", u.Handle));
    }

    [SkippableFact]
    public void RealNativeTArch_Load_CensusNonEmpty()
    {
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native3\2号厂房1021_t9.dwg";
        Skip.If(!File.Exists(path), "真实原生天正标本不在本机，跳过");
        // 直读源（不经 working）：用 LoadDwg 通道——经 Load 会建 working 副本于 outputs（可接受，outputs 非用户目录）
        var session = DrawingSession.Load(path);
        Assert.True(session.Unparsed.TotalCount > 1000);
        Assert.Contains(session.Unparsed.Classes, c => c.CppClassName == "TCH_WALL");
    }
}
```

（DrawingSession ctor 现签名 (string sourcePath, CadDocument doc) internal/public 读现状；加第三可选参 census。Summarize 现 Unknowns 构建读现状后 concat census briefs。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter UnparsedCensus`
Expected: 编译失败（类型不存在）

- [ ] **Step 3: 实现 UnparsedCensus.cs**

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadAgent.Data;

public sealed record UnparsedClassBrief(string CppClassName, int Count);

/// <summary>
/// 读取器通知 census：ACadSharp 3.8.35 将未列自定义类（天正 TCH_*）读为 UnknownEntity
/// 但不暴露进文档对象图（实证：通知数千/对象图 0）→ 准入闸口必须消费本 census 防假 Green。
/// </summary>
public sealed record UnparsedCensus(IReadOnlyList<UnparsedClassBrief> Classes)
{
    public static readonly UnparsedCensus Empty = new(Array.Empty<UnparsedClassBrief>());
    public int TotalCount => Classes.Sum(c => c.Count);

    private static readonly Regex UnknownEntityMsg =
        new(@"Unlisted object with DXF name (\S+) has been read as an UnknownEntity", RegexOptions.Compiled);

    public static UnparsedCensus FromNotifications(IEnumerable<string> messages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            var match = UnknownEntityMsg.Match(m);
            if (!match.Success) continue;
            string name = match.Groups[1].Value;
            counts.TryGetValue(name, out int c);
            counts[name] = c + 1;
        }
        return new UnparsedCensus(counts.OrderByDescending(kv => kv.Value)
            .Select(kv => new UnparsedClassBrief(kv.Key, kv.Value)).ToList());
    }
}

/// <summary>census sidecar（.cadagent/unparsed.json）：working copy 丢失 TCH 对象后二次加载仍可见。</summary>
public static class UnparsedCensusStore
{
    private sealed record Sidecar(string SourcePath, string SourceHash, IReadOnlyList<UnparsedClassBrief> Classes);

    private static string SidecarPath(string projectDir) =>
        Path.Combine(projectDir, ".cadagent", "unparsed.json");

    private static string HashOf(string sourcePath)
    {
        using var fs = File.OpenRead(sourcePath);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static UnparsedCensus Load(string projectDir, string sourcePath)
    {
        string p = SidecarPath(projectDir);
        if (!File.Exists(p) || !File.Exists(sourcePath)) return UnparsedCensus.Empty;
        try
        {
            var sc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(p));
            if (sc == null || sc.SourcePath != Path.GetFullPath(sourcePath) || sc.SourceHash != HashOf(sourcePath))
                return UnparsedCensus.Empty;
            return new UnparsedCensus(sc.Classes);
        }
        catch { return UnparsedCensus.Empty; } // 损坏 sidecar 降级 Empty（闸口另有图内 Unknowns 兜底）
    }

    public static void Save(string projectDir, string sourcePath, UnparsedCensus census)
    {
        string p = SidecarPath(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var sc = new Sidecar(Path.GetFullPath(sourcePath), HashOf(sourcePath), census.Classes);
        File.WriteAllText(p, JsonSerializer.Serialize(sc));
    }
}
```

- [ ] **Step 4: DrawingSession 接线**

LoadDwg 改返回 (doc, census)：

```csharp
    private static (CadDocument Doc, UnparsedCensus Census) LoadDwg(string path)
    {
        using var reader = new DwgReader(path);
        var messages = new List<string>();
        reader.OnNotification += (_, e) => messages.Add(e.Message);
        var doc = reader.Read();
        return (doc, UnparsedCensus.FromNotifications(messages));
    }
```

Load：working 分支 `return new DrawingSession(workingPath, LoadDxf(workingPath), persisted ?? UnparsedCensus.Empty);`（persisted 由调用方传 sidecar）；首载分支 `var (doc, census) = LoadDwg(path); var session = new DrawingSession(workingPath, doc, census); session.SaveWorking(); return session;`。ctor 加 `UnparsedCensus? unparsed = null` → `Unparsed = unparsed ?? UnparsedCensus.Empty`。属性 `public UnparsedCensus Unparsed { get; }`。Reload/RestoreState 不动（内存 DXF 无 TCH；census 保持原值——注释说明）。Summarize：Unknowns 构建后 concat `Unparsed.Classes.Select(c => new UnknownBrief("", c.CppClassName, c.CppClassName, ""))` 填入新字段 Unparsed（Unknowns 保持图内口径不变）。DrawingSummary record 末尾加 `IReadOnlyList<UnknownBrief>? Unparsed = null`。

- [ ] **Step 5: 运行确认绿 + Data 全量回归**

Run: `dotnet test tests/CadAgent.Data.Tests`
Expected: PASS（含真实标本 skip-gated 实跑 census>1000）

- [ ] **Step 6: 提交**

```bash
git add src/CadAgent.Data/UnparsedCensus.cs src/CadAgent.Data/DrawingSession.cs src/CadAgent.Data/DrawingSummary.cs tests/CadAgent.Data.Tests/UnparsedCensusTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: unparsed-object census from reader notifications + sidecar persistence (false-Green fix foundation)"
```

---

### Task 2: GateDetector 三分（Red=无原生实体 / Yellow=有原生+未解析）

**Files:**
- Modify: `src/CadAgent.Data/GateDetector.cs`
- Test: `tests/CadAgent.Data.Tests/GateDetectorTests.cs`（扩展；读现状夹具模式）

**Interfaces:**
- Consumes: DrawingSummary.Unparsed（T1）；UnknownBrief；ISupportMatrix/EmptySupportMatrix
- Produces: GateDetector.Inspect 三分：unsupported = (Unknowns + Unparsed) 中矩阵未支持；Green iff unsupported 空；**Red iff unsupported 全量且原生可解析实体数==0**（原生数 = Entities.Count - Unknowns.Count）；**Yellow iff unsupported 非空且原生数>0**；Yellow guidance = T3Guidance + "当前为只读审查模式：修改操作已被拦截（未解析对象无句柄/几何，禁触区无法定位，全图级拦截）。"

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Inspect_NativeEntitiesPlusUnparsed_Yellow_ReadOnlyGuidance()
{
    // summary：Entities 3 条原生 brief + Unknowns 空 + Unparsed TCH_WALL×2 → Yellow + guidance 含"只读审查"
}

[Fact]
public void Inspect_NoNativeEntities_AllUnparsed_Red()
{
    // Entities 空 + Unparsed TCH_WALL×2 → Red + T3Guidance
}

[Fact]
public void Inspect_NoUnparsed_Green_Verbatim()
{
    // Entities 3 + Unknowns 空 + Unparsed 空/null → Green（legacy 逐字）
}

[Fact]
public void Inspect_GraphUnknownsPartialSupport_Yellow_LegacyMatrixPath()
{
    // 图内 Unknowns 2（1 支持 1 不支持）+ Entities 含该 2 → 原生数=Entities-Unknowns>0 → Yellow（legacy 矩阵路径保持）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter Inspect_NativeEntitiesPlusUnparsed`
Expected: FAIL（现逻辑全未支持→Red）

- [ ] **Step 3: 实现**

```csharp
    public const string YellowReadOnlyGuidance =
        T3Guidance + " 当前为只读审查模式：修改操作已被拦截（未解析对象无句柄/几何，禁触区无法定位，全图级拦截）。";

    public static GateReport Inspect(DrawingSummary summary, ISupportMatrix matrix)
    {
        var unparsed = summary.Unparsed ?? Array.Empty<UnknownBrief>();
        var all = summary.Unknowns.Concat(unparsed).ToList();
        if (all.Count == 0)
            return new GateReport(GateLevel.Green, Array.Empty<UnknownBrief>(), "");
        var unsupported = all.Where(u => !matrix.IsSupported(u.CppClassName)).ToList();
        if (unsupported.Count == 0)
            return new GateReport(GateLevel.Green, unsupported, "");
        int nativeCount = summary.Entities.Count - summary.Unknowns.Count;
        if (unsupported.Count == all.Count && nativeCount == 0)
            return new GateReport(GateLevel.Red, unsupported, T3Guidance);
        return new GateReport(GateLevel.Yellow, unsupported, YellowReadOnlyGuidance);
    }
```

- [ ] **Step 4: 运行确认绿 + Data/Core/Cli 回归**

Run: `dotnet test tests/CadAgent.Data.Tests` + `tests/CadAgent.Cli.Tests`
Expected: PASS（既有 Yellow 期望若断言"放行"→T3 改；本任务仅闸口分类，Cli 拒绝语义 T3 改——既有 Cli Yellow 测试若存在断言 warn-proceed 会红→sanctioned 更新留 T3 一并；若本任务即红则在本任务更新期望为 Yellow 分类断言，拒绝行为 T3 落地）

- [ ] **Step 5: 提交** `feat: gate three-way classification (Red=no native entities, Yellow=partial unparsed, read-only guidance)`

---

### Task 3: 修改操作拒绝（四消费点+agent）+ sidecar 接线 + 视图警示

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs:116(intake),183(RunPlan)`、`CommandRouter.Skills.cs:112`、`CommandRouter.Agent.cs`（加闸检查+sidecar）
- Modify: `src/CadAgent.View/ViewModels/EditViewModel.cs:186`、`MainViewModel.cs`、`MainWindow.cs`（StateNote）
- Test: `tests/CadAgent.Cli.Tests/UnparsedGateCliTests.cs`（新）、`tests/CadAgent.View.Tests/EditViewModelTests.cs`（扩展）

**Interfaces:**
- Consumes: T1 census/sidecar/Load persisted 参；T2 GateReport Yellow
- Produces: CLI run/skills/agent Yellow/Red→exit 1 + run-rejected 审计（params 含 level+unparsedCount+guidance）；intake stdout 加 unparsed census 数组；视图 EditViewModel Yellow→EditResult(false,...guidance) + run-rejected 留痕；MainWindow StateNote 含"含 N 个未解析天正对象，显示不完整（只读审查）"

- [ ] **Step 1: 写失败测试**

CLI（sidecar 注入 census，无需真实文件）：
```csharp
[Fact]
public void Run_YellowUnparsed_RefusedExit1_AuditRunRejected()
{
    // 临时项目：普通 DXF 图纸（Green 基线）+ sidecar unparsed.json（TCH_WALL×2，源哈希匹配）
    // CLI run plan(add-line) → exit 1 + stdout/err 含"只读审查" + audit run-rejected params.level=Yellow
}
[Fact]
public void Agent_YellowUnparsed_RefusedBeforeLlm()
{
    // sidecar census + fake LLM factory（若被调则测试失败）→ agent exit 1 + run-rejected；LLM 零调用
}
[Fact]
public void Intake_YellowUnparsed_StdoutCensus()
{
    // intake stdout JSON 含 unparsed 数组（TCH_WALL:2）+ level=Yellow
}
[Fact]
public void Run_NoCensus_GreenLegacy_Commits()
{
    // 无 sidecar → add-line tx-commit（legacy 逐字）
}
```
View：
```csharp
[Fact]
public async Task Edit_YellowUnparsed_RefusedNoTx_StateNote()
{
    // session ctor census 注入 → EditViewModel.DrawLineAsync → Ok=false + Error 含"只读审查" + audit run-rejected + 无 tx-start
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter UnparsedGate` + View filter Edit_YellowUnparsed
Expected: FAIL（现 Yellow warn-proceed / agent 无闸）

- [ ] **Step 3: 实现拒绝+接线**

CLI 加载点统一 helper（CommandRouter.cs 加）：
```csharp
    private static DrawingSession LoadWithCensus(string drawingPath, string agentDir)
    {
        var persisted = UnparsedCensusStore.Load(agentDir, drawingPath);
        var session = DrawingSession.Load(drawingPath, persisted);
        if (session.Unparsed.TotalCount > 0)
            UnparsedCensusStore.Save(agentDir, drawingPath, session.Unparsed);
        return session;
    }
```
intake/RunPlan/Skills/Agent 的 DrawingSession.Load 改 LoadWithCensus（agent 路径加 GateDetector.Inspect：Red/Yellow→run-rejected 审计+exit 1，先于 LLM 调用）。RunPlan/Skills 现 Red 拒绝分支扩为 `report.Level != GateLevel.Green`→拒绝（Yellow 用 report.Guidance）。intake stdout 加 `unparsed = session.Unparsed.Classes`。EditViewModel:186 分支 `gate.Level == Red` 扩为 `!= Green`（Yellow guidance 拒绝+留痕）。MainViewModel Load 改 LoadWithCensus 等价（projectDir 已知）；MainWindow StateNote 追加 census 警示（Unparsed.TotalCount>0 时）。

- [ ] **Step 4: 运行确认绿 + 全量回归**

Run: Data+Core+Cli+View 全量
Expected: PASS（既有 Yellow warn-proceed 测试期望 sanctioned 更新为拒绝；无 census 路径逐字保持）

- [ ] **Step 5: 提交** `feat: refuse modifications on Yellow/Red unparsed drawings (CLI run/skills/agent + view) + census sidecar wiring + StateNote`

---

### Task 4: 真实标本闸口实战重跑 + 🟢 闭环重取

**Files:**
- Test: `tests/CadAgent.Cli.Tests/UnparsedGateCliTests.cs`（真实 skip-gated 扩展）
- Create: `outputs/tarch-gate-recombat/`（证据：intake/拒绝/verify 日志）
- Modify: `docs/verification/sheet-aware-protocol.md`（追加天正闸口实战节）

**Interfaces:**
- Consumes: T1-T3 全链；四份原生标本 outputs 副本
- Produces: 实战证据（四标本 intake=Yellow+census 计数；修改拒绝留痕；T3 指引核对；法院标本 census 分类；🟢 重取路径裁定）

- [ ] **Step 1: 真实 skip-gated 测试**

```csharp
[SkippableFact]
public void RealNativeTArch_IntakeYellow_CensusMatchesProbe()
{
    // 2号厂房1021_t9：intake level=Yellow + unparsed TCH_WALL==1594（通知普查钉住）
}
[SkippableFact]
public void RealNativeTArch_RunRefused_ReadOnlyAudit()
{
    // add-line plan → exit 1 + run-rejected level=Yellow + 图纸 working 哈希不变
}
```

- [ ] **Step 2: 手动实战循环（bash，证据落 outputs/tarch-gate-recombat/）**

四标本 intake（期望 Yellow；若某份 Entities=0→Red 记录）；电施标本 run add-line 拒绝+audit verify；法院标本 intake 分类（若 Yellow→🟢 真实重取不可得→诚实注记+合成 E2E 为 🟢 证据；若 Green→法院标本重跑 🟢 闭环 scan/map/run/verify）；T3 指引文案核对含"图形导出/T3"。

- [ ] **Step 3: 协议追加+提交** `test: real-specimen gate re-combat (Yellow classification + refusal audit) + protocol addendum`

---

### Task 5: spec §12 闭环 + 门禁连绿两次 + 集成准备

**Files:**
- Modify: spec §12（d655f30 条追加修复闭环注记：census 通道/三分/四消费点+agent/🟡 全图级拦截语义/真实标本 Yellow 实证/🟢 重取状态）
- 门禁：`dotnet test CadAgent.sln` 顺序两跑（ZWCAD 真机前后 tasklist 核查；日志 outputs/tarch-gate-recombat/gate{1,2}.log）
- 提交（docs: 前缀）；**不 push 不 PR**（集成决定保留用户）

- [ ] **Step 1: spec 注记+提交**
- [ ] **Step 2: 门禁连绿两次+精确数字记录**
- [ ] **Step 3: report+集成菜单保留用户**

---

## Self-Review 记录

- **Spec 覆盖**：§4.2 三级闸口真实生效（T2/T3）；§9 拒绝留痕（T3）；⑥ 信息残缺不操作（T3 全图级拦截）；§12 d655f30 修复闭环（T5）；agent 闸缺口补齐（T3）。
- **占位符扫描**：测试夹具"读现状镜像"指令指向具体文件；census 注入经 ctor/sidecar 双通道确定；无 TBD。
- **类型一致性**：UnparsedCensus/UnparsedClassBrief/UnparsedCensusStore（T1）→DrawingSummary.Unparsed（T1）→GateDetector（T2）→LoadWithCensus/拒绝分支（T3）→实战（T4）链一致；UnknownBrief 四参口径保持。
- **已知取舍**：Yellow 旧 warn-proceed 改拒绝=spec 🟡 本义回归（sanctioned，既有测试期望更新须保持/加强）；census 无句柄/几何→全图级拦截（诚实降级，spec 注记）；sidecar 源哈希陈旧→Empty（源换图重读通知重建）；Reload/RestoreState 保持 census（内存 DXF 无 TCH，注释说明）。
---

## 执行记录（2026-11-23，子代理驱动）

- 分支 feat/cad-agent-sheet-aware 追加，10 提交（ee38a47..f675fbe），5 任务全完成 + 最终审查修复波（R11/R12）。
- 门禁：dotnet test CadAgent.sln 连绿两次 **1017 通过 / 0 失败 / 0 跳过**（Core 208/Data 297/Cli 200/View 269/Llm 30/Zwcad 真机 13；真实标本 skip-gated 实跑）。
- 任务内修复环：T1 intake stdout 逐字+sidecar 守卫；T2 R6 nativeCount 口径；T3 R7 repl 闸+R8 export 闸；T4 R9 旧 working 旁路+R10 协议/rollback/钉住。
- 最终审查修复波：R11 用户 DXF 源 census 盲（LoadDxf 通知+正则 DXF 变体+合成 TCH DXF 夹具 Yellow 钉住）；R12 非字符串 sheetId fail-open（镜像 handle number→decimal coercion，pinning sanctioned 更新）。
- 裁决 R1-R12 摘要：R1 IVT ctor internal；R2 Yellow warn-proceed→拒绝（spec 🟡 本义）；R3 拒绝消费点镜像 parity；R4 sidecar 陈旧/损坏→Empty；R5 intake stdout null-omit+Classes 守卫；R6 nativeCount=Entities.Count（mixed→Yellow）；R7 repl 补闸+Undo/rollback 撤回豁免；R8 export 闸（衍生物不完整不交付）；R9 旧 working 三分（源重建/源缺拒绝）；R10 协议闭合注记+rollback LoadWithCensus+CLI 钉住；R11 DXF census 捕获；R12 sheetId number coercion。
- 实战：四原生天正标本全 Yellow（5985/3257/5707/3806 census）+五消费点拒绝留痕+五项目 audit verify 绿；法院标本 Yellow 1836/13 类（旧 T3/Green 判定作废）；🟢 真实重取待用户 T3 转换（合成 E2E 为证，未伪造人工确认）。
- **剩余 deferred（post-merge）**：scan/map LoadWithCensus 等价/sidecar 冗余 Save/路径大小写/Count cap/viewport math consolidate/AnchorOf 拷贝/L-sheet handler 显式拒绝/Region 瓦片校验/null SheetId 双群注释/AttDef 噪声/DoorArc 镜像/瓦片重叠/多 claim 优先级/E2E 断言强度/属性表可注入/指纹 layout.Name/legacy payload null-omit/Export-rejected 双 params/EditResult.Warning 死字段/sidecar 多源键控/Reload census 重建/OptString 负数连带/真实测试 CopySpecimen 迁移/tasklist 原始证据/spec 单点措辞。
- **证据缺口（诚实）**：真实 Red 标本缺；视觉管道生产接线留 harness；块级纠偏清单候选；纸面编辑 deferred；真实 🟢 待用户 T3 转换。
