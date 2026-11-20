# CAD 智能体终端 · 基础内核 实现计划（计划 1/4）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付无界面可运行的确定性内核：图纸读入/导出、三级准入闸口、哈希链审计、角色责任、快照撤回、步骤引擎、CLI 单条命令。

**Architecture:** C#/.NET 8 三项目分层：Core（厂商无关：引擎/审计/责任/快照）→ Data（ACadSharp 开源底座：读写/闸口/步骤处理器）→ Cli（命令路由）。LLM、视图、厂商 COM 适配器在后续计划接入，本计划全部确定性、可离线测试。

**Tech Stack:** .NET 8、ACadSharp（NuGet，MIT）、Microsoft.Data.Sqlite 8.x（WAL）、YamlDotNet、xUnit、System.Text.Json。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md`（本计划实现 spec §3 分层、§4.1①⑤、§4.2 闸口、§5 审计/责任/撤回、§7 CLI 单条命令、§9 错误处理的内核部分）

## Global Constraints

- 目标框架 `net8.0`；语言版本 latest；平台 Windows（身份取 Windows 账号）。
- **Core 项目不得引用任何厂商库（ZWCAD/Autodesk）与 ACadSharp**；Data 项目引用 ACadSharp；Cli 引用 Core+Data。
- 审计日志只增不改；哈希链算法 SHA256；SQLite 必须开 WAL；每项目单写入通道（锁串行化）。
- 一切操作（含后续 LLM/手动）必经审计管线——本计划的步骤引擎是唯一修改入口。
- CLI 启动时设 `Console.OutputEncoding = System.Text.Encoding.UTF8`（Windows GBK 终端防乱码）；测试断言不依赖控制台中文输出。
- 密钥/敏感信息永不进日志（本计划无密钥，但日志管线预留脱敏约定：AuditEntry.ParamsJson 由调用方负责不含密钥）。
- 每个任务结束提交一次 git；提交信息用 `feat:/test:/chore:` 前缀。
- 测试命令统一 `dotnet test`（过滤用 `--filter FullyQualifiedName~<类名>`）。
- ACadSharp API 以实际安装包为准；本计划代码按官方文档编写，若签名有出入以编译错误+包内 XML 文档修正，**不得改变接口契约与测试意图**。

## 文件结构

```
CadAgent.sln
src/CadAgent.Core/
  Identity.cs              OperatorIdentity / Role / IIdentityProvider / WindowsIdentityProvider
  RoleConfig.cs            YAML 角色配置加载 + RoleChecker + RoleViolationException
  Audit.cs                 AuditEntry / AuditRecord / HashChain
  SqliteAuditStore.cs      IAuditStore / SqliteAuditStore（WAL+单写入）
  SnapshotStore.cs         快照摄取/恢复/查找
  StepEngine.cs            StepPlan / StepSpec / StepPlanParser / StepResult / IStepHandler / IDrawingSession / TxResult / StepEngine
  SupportMatrix.cs         ISupportMatrix / EmptySupportMatrix
src/CadAgent.Data/
  DrawingSession.cs        ACadSharp 封装：Load/SaveWorking/Reload/ComputeHash/Summarize
  DrawingSummary.cs        EntityBrief / UnknownBrief / DrawingSummary DTO
  GateDetector.cs          GateLevel / GateReport / GateDetector
  Handlers/LineHandlers.cs add-line / add-text
  Handlers/EditHandlers.cs delete-entity / move-entity / change-layer
src/CadAgent.Cli/
  Program.cs               入口（UTF-8 + 路由）
  CommandRouter.cs         intake / run / rollback / audit verify / roles show
tests/CadAgent.Core.Tests/   各 Core 组件测试
tests/CadAgent.Data.Tests/   读写/闸口/处理器测试 + DxfFixture 工具
tests/CadAgent.Cli.Tests/    路由端到端测试
```

---

### Task 1: 环境准备与解决方案骨架

**Files:**
- Create: `CadAgent.sln`、`src/CadAgent.Core/CadAgent.Core.csproj`、`src/CadAgent.Data/CadAgent.Data.csproj`、`src/CadAgent.Cli/CadAgent.Cli.csproj`、三个测试项目、`.gitignore`
- Test: `tests/CadAgent.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: 可编译的解决方案；后续所有任务的项目结构

- [ ] **Step 1: 安装 .NET 8 SDK（系统级安装，执行前必须经用户同意）**

本机已有 .NET 8 运行时（8.0.33）但无 SDK。Run:
```bash
winget install Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
```
安装后新开 shell 验证：`dotnet --list-sdks` 应出现 8.0.x。若 winget 失败，改用官方 dotnet-install 脚本（https://dot.net/v1/dotnet-install.ps1，`-Channel 8.0`）。**不得静默安装；用户拒绝则停止并报告。**

- [ ] **Step 2: git 初始化**

```bash
git init
```
创建 `.gitignore`：
```
bin/
obj/
*.user
.cadagent/
TestResults/
```

- [ ] **Step 3: 创建项目与引用**

```bash
dotnet new sln -n CadAgent
dotnet new classlib -n CadAgent.Core -o src/CadAgent.Core -f net8.0
dotnet new classlib -n CadAgent.Data -o src/CadAgent.Data -f net8.0
dotnet new console -n CadAgent.Cli -o src/CadAgent.Cli -f net8.0
dotnet new xunit -n CadAgent.Core.Tests -o tests/CadAgent.Core.Tests -f net8.0
dotnet new xunit -n CadAgent.Data.Tests -o tests/CadAgent.Data.Tests -f net8.0
dotnet new xunit -n CadAgent.Cli.Tests -o tests/CadAgent.Cli.Tests -f net8.0
dotnet sln add src/CadAgent.Core src/CadAgent.Data src/CadAgent.Cli tests/CadAgent.Core.Tests tests/CadAgent.Data.Tests tests/CadAgent.Cli.Tests
dotnet add src/CadAgent.Data reference src/CadAgent.Core
dotnet add src/CadAgent.Cli reference src/CadAgent.Core src/CadAgent.Data
dotnet add tests/CadAgent.Core.Tests reference src/CadAgent.Core
dotnet add tests/CadAgent.Data.Tests reference src/CadAgent.Data src/CadAgent.Core
dotnet add tests/CadAgent.Cli.Tests reference src/CadAgent.Cli src/CadAgent.Data src/CadAgent.Core
dotnet add src/CadAgent.Data package ACadSharp
dotnet add src/CadAgent.Core package Microsoft.Data.Sqlite --version 8.0.11
dotnet add src/CadAgent.Core package YamlDotNet --version 16.2.1
```
删除模板生成的 `Class1.cs`。记录 ACadSharp 实际解析版本到本文件末尾"版本记录"。

- [ ] **Step 4: 写冒烟测试**

`tests/CadAgent.Core.Tests/SmokeTests.cs`：
```csharp
namespace CadAgent.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Solution_Builds_And_Tests_Run()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: 运行验证**

Run: `dotnet test`
Expected: 全部 PASS（3 个测试项目各 1 条模板/冒烟测试；删除各项目模板 UnitTest1.cs 后仅剩冒烟测试）。

- [ ] **Step 6: Commit**

```bash
git add .gitignore CadAgent.sln src tests
git commit -m "chore: scaffold CadAgent solution (.NET 8, Core/Data/Cli + xUnit)"
```

---

### Task 2: DrawingSession 与 DXF 读写

**Files:**
- Create: `src/CadAgent.Data/DrawingSession.cs`、`src/CadAgent.Data/DrawingSummary.cs`、`tests/CadAgent.Data.Tests/DxfFixture.cs`
- Test: `tests/CadAgent.Data.Tests/DrawingSessionTests.cs`

**Interfaces:**
- Consumes: Core 的 `IDrawingSession`（本任务先在 Core 定义，见 Step 3）
- Produces:
  - `CadAgent.Core.IDrawingSession`：`string SourcePath { get; }`、`string ComputeHash()`、`void SaveWorking()`、`void Reload()`
  - `CadAgent.Data.DrawingSession : IDrawingSession`：`static DrawingSession Load(string path)`、`ACadSharp.CadDocument Doc { get; }`、`DrawingSummary Summarize()`
  - `CadAgent.Data.DrawingSummary(string SourcePath, string Version, IReadOnlyList<string> Layers, IReadOnlyList<EntityBrief> Entities, IReadOnlyList<UnknownBrief> Unknowns)`
  - `EntityBrief(string Handle, string Kind, string Layer)`；`UnknownBrief(string Handle, string ObjectName, string CppClassName, string ApplicationName)`
  - `DxfFixture.CreateTempDxf(Action<CadDocument> populate)` → 返回临时文件路径（测试用）

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Data.Tests/DxfFixture.cs`：
```csharp
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Math;

namespace CadAgent.Data.Tests;

public static class DxfFixture
{
    public static string CreateTempDxf(Action<CadDocument> populate)
    {
        var doc = new CadDocument();
        populate(doc);
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-test-{Guid.NewGuid():N}.dxf");
        using var fs = File.Create(path);
        DxfWriter.Write(fs, doc);
        return path;
    }

    public static void AddSampleLine(CadDocument doc)
    {
        var line = new Line { StartPoint = new XY(0, 0), EndPoint = new XY(100, 100) };
        doc.Entities.Add(line);
    }
}
```

`tests/CadAgent.Data.Tests/DrawingSessionTests.cs`：
```csharp
using ACadSharp.Entities;
using ACadSharp.Math;

namespace CadAgent.Data.Tests;

public class DrawingSessionTests : IDisposable
{
    private readonly string _path;
    public DrawingSessionTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_ReadsEntitiesAndLayers()
    {
        var session = DrawingSession.Load(_path);
        var summary = session.Summarize();
        Assert.Single(summary.Entities);
        Assert.Equal("Line", summary.Entities[0].Kind);
        Assert.Contains("0", summary.Layers);
        Assert.Empty(summary.Unknowns);
    }

    [Fact]
    public void ComputeHash_IsStableForSameContent()
    {
        var s1 = DrawingSession.Load(_path);
        var s2 = DrawingSession.Load(_path);
        Assert.Equal(s1.ComputeHash(), s2.ComputeHash());
    }

    [Fact]
    public void SaveWorking_And_Reload_PersistChanges()
    {
        var session = DrawingSession.Load(_path);
        session.Doc.Entities.Add(new Line { StartPoint = new XY(1, 2), EndPoint = new XY(3, 4) });
        session.SaveWorking();
        session.Reload();
        Assert.Equal(2, session.Summarize().Entities.Count);
    }

    [Fact]
    public void Load_UnsupportedExtension_Throws()
    {
        Assert.Throws<NotSupportedException>(() => DrawingSession.Load("foo.pdf"));
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~DrawingSessionTests`
Expected: 编译失败（DrawingSession 不存在）。

- [ ] **Step 3: 定义 Core 接口**

`src/CadAgent.Core/StepEngine.cs`（本任务先放接口，Task 8 补全引擎）：
```csharp
namespace CadAgent.Core;

public interface IDrawingSession
{
    string SourcePath { get; }
    string ComputeHash();
    void SaveWorking();
    void Reload();
}
```

- [ ] **Step 4: 实现 DrawingSession 与 DTO**

`src/CadAgent.Data/DrawingSummary.cs`：
```csharp
namespace CadAgent.Data;

public sealed record EntityBrief(string Handle, string Kind, string Layer);
public sealed record UnknownBrief(string Handle, string ObjectName, string CppClassName, string ApplicationName);
public sealed record DrawingSummary(
    string SourcePath,
    string Version,
    IReadOnlyList<string> Layers,
    IReadOnlyList<EntityBrief> Entities,
    IReadOnlyList<UnknownBrief> Unknowns);
```

`src/CadAgent.Data/DrawingSession.cs`：
```csharp
using System.Security.Cryptography;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;

namespace CadAgent.Data;

public sealed class DrawingSession : IDrawingSession
{
    public CadDocument Doc { get; private set; }
    public string SourcePath { get; }

    private DrawingSession(string path, CadDocument doc)
    {
        SourcePath = path;
        Doc = doc;
    }

    public static DrawingSession Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        CadDocument doc = ext switch
        {
            ".dxf" => LoadDxf(path),
            ".dwg" => DwgReader.Read(path),
            _ => throw new NotSupportedException($"不支持的图纸格式: {ext}")
        };
        return new DrawingSession(path, doc);
    }

    private static CadDocument LoadDxf(string path)
    {
        using var reader = new DxfReader(path);
        return reader.Read();
    }

    public string ComputeHash()
    {
        using var ms = new MemoryStream();
        DxfWriter.Write(ms, Doc);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    public void SaveWorking()
    {
        using var fs = File.Create(SourcePath);
        DxfWriter.Write(fs, Doc);
    }

    public void Reload() => Doc = LoadDxf(SourcePath);

    public DrawingSummary Summarize()
    {
        var layers = Doc.Layers.Select(l => l.Name).ToList();
        var entities = new List<EntityBrief>();
        var unknowns = new List<UnknownBrief>();
        foreach (var e in Doc.Entities)
        {
            string handle = e.Handle.ToString();
            string layer = e.Layer?.Name ?? "0";
            if (e is UnknownEntity u)
                unknowns.Add(new UnknownBrief(handle, u.ObjectName ?? "",
                    u.DxfClass?.CppClassName ?? "", u.DxfClass?.ApplicationName ?? ""));
            else if (e is ProxyEntity p)
                unknowns.Add(new UnknownBrief(handle, p.ObjectName ?? "",
                    p.DxfClass?.CppClassName ?? "", p.DxfClass?.ApplicationName ?? ""));
            else
                entities.Add(new EntityBrief(handle, e.GetType().Name, layer));
        }
        return new DrawingSummary(SourcePath, Doc.Version.ToString(), layers, entities, unknowns);
    }
}
```
注意：`DxfReader` 实例用后释放（`using var reader = new DxfReader(path); Doc = reader.Read();`）；ACadSharp 集合接口以实际包为准微调，测试意图不变。

- [ ] **Step 5: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~DrawingSessionTests`
Expected: 4 条 PASS。

- [ ] **Step 6: Commit**

```bash
git add src/CadAgent.Core/StepEngine.cs src/CadAgent.Data tests/CadAgent.Data.Tests
git commit -m "feat: DrawingSession with ACadSharp load/save/hash/summary"
```

---

### Task 3: 三级准入闸口

**Files:**
- Create: `src/CadAgent.Core/SupportMatrix.cs`、`src/CadAgent.Data/GateDetector.cs`
- Test: `tests/CadAgent.Data.Tests/GateDetectorTests.cs`

**Interfaces:**
- Consumes: `DrawingSummary`、`UnknownBrief`（Task 2）
- Produces:
  - `CadAgent.Core.ISupportMatrix`：`bool IsSupported(string cppClassName)`
  - `CadAgent.Core.EmptySupportMatrix : ISupportMatrix`（恒 false）
  - `CadAgent.Data.GateLevel`：`Green / Yellow / Red`
  - `CadAgent.Data.GateReport(GateLevel Level, IReadOnlyList<UnknownBrief> Unsupported, string Guidance)`
  - `static GateReport GateDetector.Inspect(DrawingSummary summary, ISupportMatrix matrix)`

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Data.Tests/GateDetectorTests.cs`：
```csharp
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class GateDetectorTests
{
    private static DrawingSummary Summary(params UnknownBrief[] unknowns) =>
        new("test.dxf", "AC1027", new[] { "0" },
            new[] { new EntityBrief("1", "Line", "0") }, unknowns);

    private sealed class FakeMatrix : ISupportMatrix
    {
        private readonly HashSet<string> _ok;
        public FakeMatrix(params string[] ok) => _ok = new HashSet<string>(ok);
        public bool IsSupported(string cppClassName) => _ok.Contains(cppClassName);
    }

    [Fact]
    public void PlainDrawing_IsGreen()
    {
        var report = GateDetector.Inspect(Summary(), new EmptySupportMatrix());
        Assert.Equal(GateLevel.Green, report.Level);
        Assert.Empty(report.Unsupported);
    }

    [Fact]
    public void AllUnsupportedUnknowns_IsRed_WithT3Guidance()
    {
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_WALL", "TCH_WALL", "Tangent")),
            new EmptySupportMatrix());
        Assert.Equal(GateLevel.Red, report.Level);
        Assert.Contains("T3", report.Guidance);
    }

    [Fact]
    public void PartiallySupported_IsYellow()
    {
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_TEXT", "TCH_TEXT", "Tangent"),
                    new UnknownBrief("3", "TCH_WALL", "TCH_WALL", "Tangent")),
            new FakeMatrix("TCH_TEXT"));
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Single(report.Unsupported);
        Assert.Equal("TCH_WALL", report.Unsupported[0].CppClassName);
    }

    [Fact]
    public void AllSupported_IsGreen()
    {
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_TEXT", "TCH_TEXT", "Tangent")),
            new FakeMatrix("TCH_TEXT"));
        Assert.Equal(GateLevel.Green, report.Level);
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~GateDetectorTests`
Expected: 编译失败（GateDetector 不存在）。

- [ ] **Step 3: 实现**

`src/CadAgent.Core/SupportMatrix.cs`：
```csharp
namespace CadAgent.Core;

public interface ISupportMatrix
{
    bool IsSupported(string cppClassName);
}

public sealed class EmptySupportMatrix : ISupportMatrix
{
    public bool IsSupported(string cppClassName) => false;
}
```

`src/CadAgent.Data/GateDetector.cs`：
```csharp
using CadAgent.Core;

namespace CadAgent.Data;

public enum GateLevel { Green, Yellow, Red }

public sealed record GateReport(
    GateLevel Level,
    IReadOnlyList<UnknownBrief> Unsupported,
    string Guidance);

public static class GateDetector
{
    public const string T3Guidance =
        "图纸含未完整解析的自定义对象（如天正 TCH_*）。请在天正中执行【文件布图→图形导出】，" +
        "保存类型选择天正T3文件(*.t3)后重新导入；或等待解析支持矩阵点亮对应对象类。";

    public static GateReport Inspect(DrawingSummary summary, ISupportMatrix matrix)
    {
        if (summary.Unknowns.Count == 0)
            return new GateReport(GateLevel.Green, Array.Empty<UnknownBrief>(), "");

        var unsupported = summary.Unknowns
            .Where(u => !matrix.IsSupported(u.CppClassName))
            .ToList();

        if (unsupported.Count == 0)
            return new GateReport(GateLevel.Green, unsupported, "");
        if (unsupported.Count == summary.Unknowns.Count)
            return new GateReport(GateLevel.Red, unsupported, T3Guidance);
        return new GateReport(GateLevel.Yellow, unsupported, T3Guidance);
    }
}
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~GateDetectorTests`
Expected: 4 条 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/SupportMatrix.cs src/CadAgent.Data/GateDetector.cs tests/CadAgent.Data.Tests/GateDetectorTests.cs
git commit -m "feat: three-level intake gate keyed on information completeness"
```

---

### Task 4: 审计记录与哈希链

**Files:**
- Create: `src/CadAgent.Core/Audit.cs`
- Test: `tests/CadAgent.Core.Tests/HashChainTests.cs`

**Interfaces:**
- Consumes: `Role`（Task 6 定义；本任务先用 string 角色字段避免循环依赖——AuditEntry.Role 为 string）
- Produces:
  - `AuditEntry(DateTimeOffset Ts, string Operator, string Role, string Session, string Source, string Kind, string Target, string ParamsJson, string BeforeHash, string AfterHash, string SnapshotRef, string PlanRef)`
  - `AuditRecord(long Seq, AuditEntry Entry, string PrevHash, string Hash)`
  - `static class HashChain`：`string Canonical(AuditEntry e, long seq, string prevHash)`、`string Compute(AuditEntry e, long seq, string prevHash)`、`string GenesisHash`（64 个 '0'）

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Core.Tests/HashChainTests.cs`：
```csharp
namespace CadAgent.Core.Tests;

public class HashChainTests
{
    private static AuditEntry Entry(string kind) =>
        new(DateTimeOffset.UnixEpoch, "TEST\\user", "Draw", "s1", "plan",
            kind, "handle-1", "{}", "before", "after", "", "p1");

    [Fact]
    public void SameInput_SameHash()
    {
        var h1 = HashChain.Compute(Entry("intake"), 1, HashChain.GenesisHash);
        var h2 = HashChain.Compute(Entry("intake"), 1, HashChain.GenesisHash);
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);
    }

    [Fact]
    public void TamperedField_ChangesHash()
    {
        var original = Entry("intake");
        var tampered = original with { Operator = "TEST\\attacker" };
        Assert.NotEqual(
            HashChain.Compute(original, 1, HashChain.GenesisHash),
            HashChain.Compute(tampered, 1, HashChain.GenesisHash));
    }

    [Fact]
    public void PrevHash_LinksChain()
    {
        var h1 = HashChain.Compute(Entry("a"), 1, HashChain.GenesisHash);
        var h2a = HashChain.Compute(Entry("b"), 2, h1);
        var h2b = HashChain.Compute(Entry("b"), 2, HashChain.GenesisHash);
        Assert.NotEqual(h2a, h2b);
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~HashChainTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Core/Audit.cs`：
```csharp
using System.Security.Cryptography;
using System.Text;

namespace CadAgent.Core;

public sealed record AuditEntry(
    DateTimeOffset Ts,
    string Operator,
    string Role,
    string Session,
    string Source,
    string Kind,
    string Target,
    string ParamsJson,
    string BeforeHash,
    string AfterHash,
    string SnapshotRef,
    string PlanRef);

public sealed record AuditRecord(long Seq, AuditEntry Entry, string PrevHash, string Hash);

public static class HashChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Canonical(AuditEntry e, long seq, string prevHash) =>
        string.Join("|", seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            e.Ts.ToString("O"), Esc(e.Operator), Esc(e.Role), Esc(e.Session),
            Esc(e.Source), Esc(e.Kind), Esc(e.Target), Esc(e.ParamsJson), Esc(e.BeforeHash), Esc(e.AfterHash),
            Esc(e.SnapshotRef), Esc(e.PlanRef), prevHash);

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("|", "\\p");

    public static string Compute(AuditEntry e, long seq, string prevHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(e, seq, prevHash))));
}
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~HashChainTests`
Expected: 3 条 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/Audit.cs tests/CadAgent.Core.Tests/HashChainTests.cs
git commit -m "feat: audit entry model and SHA256 hash chain"
```

---

### Task 5: SQLite 审计存储（WAL + 单写入 + 验链）

**Files:**
- Create: `src/CadAgent.Core/SqliteAuditStore.cs`
- Test: `tests/CadAgent.Core.Tests/SqliteAuditStoreTests.cs`

**Interfaces:**
- Consumes: `AuditEntry / AuditRecord / HashChain`（Task 4）
- Produces:
  - `IAuditStore`：`AuditRecord Append(AuditEntry e)`、`IReadOnlyList<AuditRecord> ReadAll()`、`bool VerifyChain(out string error)`
  - `SqliteAuditStore : IAuditStore, IDisposable`：ctor `string dbPath`（自动建库建表开 WAL）

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Core.Tests/SqliteAuditStoreTests.cs`：
```csharp
namespace CadAgent.Core.Tests;

public class SqliteAuditStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cadagent-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditStore NewStore() => new(_dbPath);
    public void Dispose() { if (File.Exists(_dbPath)) File.Delete(_dbPath); }

    private static AuditEntry Entry(string kind) =>
        new(DateTimeOffset.UtcNow, "TEST\\user", "Draw", "s1", "plan",
            kind, "t", "{}", "b", "a", "", "p");

    [Fact]
    public void Append_AssignsSeqAndLinksChain()
    {
        using var store = NewStore();
        var r1 = store.Append(Entry("intake"));
        var r2 = store.Append(Entry("step"));
        Assert.Equal(1, r1.Seq);
        Assert.Equal(2, r2.Seq);
        Assert.Equal(HashChain.GenesisHash, r1.PrevHash);
        Assert.Equal(r1.Hash, r2.PrevHash);
    }

    [Fact]
    public void VerifyChain_OkOnUntamperedLog()
    {
        using var store = NewStore();
        store.Append(Entry("a"));
        store.Append(Entry("b"));
        Assert.True(store.VerifyChain(out string error));
        Assert.Equal("", error);
    }

    [Fact]
    public void VerifyChain_DetectsTampering()
    {
        using (var store = NewStore())
        {
            store.Append(Entry("a"));
            store.Append(Entry("b"));
        }
        // 绕过应用层直接改库，模拟篡改
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audit SET operator = 'TEST\\attacker' WHERE seq = 1";
            cmd.ExecuteNonQuery();
        }
        using var verify = NewStore();
        Assert.False(verify.VerifyChain(out string error));
        Assert.Contains("seq 1", error);
    }

    [Fact]
    public void ConcurrentAppends_AllPersistedAndChainValid()
    {
        using var store = NewStore();
        Parallel.For(0, 50, i => store.Append(Entry($"k{i}")));
        var all = store.ReadAll();
        Assert.Equal(50, all.Count);
        Assert.Equal(Enumerable.Range(1, 50), all.Select(r => r.Seq));
        Assert.True(store.VerifyChain(out _));
    }

    [Fact]
    public void ReopenStore_PreservesChain()
    {
        using (var store = NewStore()) store.Append(Entry("a"));
        using var reopened = NewStore();
        var r2 = reopened.Append(Entry("b"));
        Assert.Equal(2, r2.Seq);
        Assert.True(reopened.VerifyChain(out _));
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~SqliteAuditStoreTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Core/SqliteAuditStore.cs`：
```csharp
using Microsoft.Data.Sqlite;

namespace CadAgent.Core;

public interface IAuditStore
{
    AuditRecord Append(AuditEntry e);
    IReadOnlyList<AuditRecord> ReadAll();
    bool VerifyChain(out string error);
}

public sealed class SqliteAuditStore : IAuditStore, IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public SqliteAuditStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS audit(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL, operator TEXT NOT NULL, role TEXT NOT NULL,
                session TEXT NOT NULL, source TEXT NOT NULL, kind TEXT NOT NULL,
                target TEXT NOT NULL, params_json TEXT NOT NULL,
                before_hash TEXT NOT NULL, after_hash TEXT NOT NULL,
                snapshot_ref TEXT NOT NULL, plan_ref TEXT NOT NULL,
                prev_hash TEXT NOT NULL, hash TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public AuditRecord Append(AuditEntry e)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            long seq;
            string prevHash;
            using (var head = conn.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = "SELECT seq, hash FROM audit ORDER BY seq DESC LIMIT 1";
                using var r = head.ExecuteReader();
                if (r.Read()) { seq = r.GetInt64(0) + 1; prevHash = r.GetString(1); }
                else { seq = 1; prevHash = HashChain.GenesisHash; }
            }
            string hash = HashChain.Compute(e, seq, prevHash);
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO audit(seq, ts, operator, role, session, source, kind, target,
                        params_json, before_hash, after_hash, snapshot_ref, plan_ref, prev_hash, hash)
                    VALUES($seq,$ts,$op,$role,$session,$source,$kind,$target,
                        $params,$before,$after,$snap,$plan,$prev,$hash)
                    """;
                ins.Parameters.AddWithValue("$seq", seq);
                ins.Parameters.AddWithValue("$ts", e.Ts.ToString("O"));
                ins.Parameters.AddWithValue("$op", e.Operator);
                ins.Parameters.AddWithValue("$role", e.Role);
                ins.Parameters.AddWithValue("$session", e.Session);
                ins.Parameters.AddWithValue("$source", e.Source);
                ins.Parameters.AddWithValue("$kind", e.Kind);
                ins.Parameters.AddWithValue("$target", e.Target);
                ins.Parameters.AddWithValue("$params", e.ParamsJson);
                ins.Parameters.AddWithValue("$before", e.BeforeHash);
                ins.Parameters.AddWithValue("$after", e.AfterHash);
                ins.Parameters.AddWithValue("$snap", e.SnapshotRef);
                ins.Parameters.AddWithValue("$plan", e.PlanRef);
                ins.Parameters.AddWithValue("$prev", prevHash);
                ins.Parameters.AddWithValue("$hash", hash);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            return new AuditRecord(seq, e, prevHash, hash);
        }
    }

    public IReadOnlyList<AuditRecord> ReadAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM audit ORDER BY seq";
        using var r = cmd.ExecuteReader();
        var list = new List<AuditRecord>();
        while (r.Read()) list.Add(ReadRecord(r));
        return list;
    }

    private static AuditRecord ReadRecord(SqliteDataReader r)
    {
        var e = new AuditEntry(
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ts"))),
            r.GetString(r.GetOrdinal("operator")),
            r.GetString(r.GetOrdinal("role")),
            r.GetString(r.GetOrdinal("session")),
            r.GetString(r.GetOrdinal("source")),
            r.GetString(r.GetOrdinal("kind")),
            r.GetString(r.GetOrdinal("target")),
            r.GetString(r.GetOrdinal("params_json")),
            r.GetString(r.GetOrdinal("before_hash")),
            r.GetString(r.GetOrdinal("after_hash")),
            r.GetString(r.GetOrdinal("snapshot_ref")),
            r.GetString(r.GetOrdinal("plan_ref")));
        return new AuditRecord(r.GetInt64(r.GetOrdinal("seq")), e,
            r.GetString(r.GetOrdinal("prev_hash")), r.GetString(r.GetOrdinal("hash")));
    }

    public bool VerifyChain(out string error)
    {
        string prev = HashChain.GenesisHash;
        foreach (var rec in ReadAll())
        {
            if (rec.PrevHash != prev)
            { error = $"seq {rec.Seq}: prev_hash 断链"; return false; }
            string expect = HashChain.Compute(rec.Entry, rec.Seq, rec.PrevHash);
            if (rec.Hash != expect)
            { error = $"seq {rec.Seq}: 哈希不匹配（内容被篡改）"; return false; }
            prev = rec.Hash;
        }
        error = "";
        return true;
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~SqliteAuditStoreTests`
Expected: 5 条 PASS（含 50 并发追加链完整）。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/SqliteAuditStore.cs tests/CadAgent.Core.Tests/SqliteAuditStoreTests.cs
git commit -m "feat: SQLite WAL audit store with single-writer channel and chain verification"
```

---

### Task 6: 身份与角色责任

**Files:**
- Create: `src/CadAgent.Core/Identity.cs`、`src/CadAgent.Core/RoleConfig.cs`
- Test: `tests/CadAgent.Core.Tests/RoleTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `enum Role { Draw, Review, Approve }`
  - `sealed record OperatorIdentity(string Account, Role Role)`
  - `interface IIdentityProvider { OperatorIdentity Current { get; } }`
  - `WindowsIdentityProvider : IIdentityProvider`：ctor `RoleConfig config`；Current 取 `WindowsIdentity.GetCurrent().Name` + 配置角色（未配置账号默认 Draw）
  - `RoleConfig`：`static RoleConfig Load(string yamlPath)`、`static RoleConfig Parse(string yaml)`、`Role RoleFor(string account)`
  - `RoleChecker`：`void Ensure(OperatorIdentity op, Role required)`（不满足抛 `RoleViolationException`）
  - YAML 格式：`roles: { "DOMAIN\\user": Review, "OTHER\\user": Approve }`

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Core.Tests/RoleTests.cs`：
```csharp
namespace CadAgent.Core.Tests;

public class RoleTests
{
    private const string Yaml = """
        roles:
          "TEST\\drawer": Draw
          "TEST\\reviewer": Review
          "TEST\\approver": Approve
        """;

    [Fact]
    public void Parse_MapsAccountsToRoles()
    {
        var config = RoleConfig.Parse(Yaml);
        Assert.Equal(Role.Review, config.RoleFor("TEST\\reviewer"));
        Assert.Equal(Role.Approve, config.RoleFor("TEST\\approver"));
    }

    [Fact]
    public void UnknownAccount_DefaultsToDraw()
    {
        var config = RoleConfig.Parse(Yaml);
        Assert.Equal(Role.Draw, config.RoleFor("TEST\\stranger"));
    }

    [Fact]
    public void Ensure_PassesForSufficientRole()
    {
        var checker = new RoleChecker();
        checker.Ensure(new OperatorIdentity("TEST\\reviewer", Role.Review), Role.Review);
    }

    [Fact]
    public void Ensure_ThrowsForInsufficientRole()
    {
        var checker = new RoleChecker();
        var ex = Assert.Throws<RoleViolationException>(() =>
            checker.Ensure(new OperatorIdentity("TEST\\drawer", Role.Draw), Role.Review));
        Assert.Contains("Review", ex.Message);
    }

    [Fact]
    public void WindowsIdentityProvider_UsesCurrentAccount()
    {
        var provider = new WindowsIdentityProvider(RoleConfig.Parse(Yaml));
        var current = provider.Current;
        Assert.False(string.IsNullOrEmpty(current.Account));
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~RoleTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Core/Identity.cs`：
```csharp
using System.Security.Principal;

namespace CadAgent.Core;

public enum Role { Draw, Review, Approve }

public sealed record OperatorIdentity(string Account, Role Role);

public interface IIdentityProvider
{
    OperatorIdentity Current { get; }
}

public sealed class WindowsIdentityProvider : IIdentityProvider
{
    private readonly RoleConfig _config;
    public WindowsIdentityProvider(RoleConfig config) => _config = config;

    public OperatorIdentity Current
    {
        get
        {
            string account = WindowsIdentity.GetCurrent().Name;
            return new OperatorIdentity(account, _config.RoleFor(account));
        }
    }
}
```

`src/CadAgent.Core/RoleConfig.cs`：
```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CadAgent.Core;

public sealed class RoleViolationException : Exception
{
    public RoleViolationException(string message) : base(message) { }
}

public sealed class RoleConfig
{
    private readonly Dictionary<string, Role> _byAccount;
    private RoleConfig(Dictionary<string, Role> byAccount) => _byAccount = byAccount;

    public static RoleConfig Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var raw = deserializer.Deserialize<RoleFile>(yaml) ?? new RoleFile();
        var map = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        if (raw.Roles != null)
            foreach (var (account, role) in raw.Roles)
                map[account] = Enum.Parse<Role>(role, ignoreCase: true);
        return new RoleConfig(map);
    }

    public static RoleConfig Load(string yamlPath) =>
        File.Exists(yamlPath) ? Parse(File.ReadAllText(yamlPath)) : Parse("roles: {}");

    public Role RoleFor(string account) =>
        _byAccount.TryGetValue(account, out var role) ? role : Role.Draw;

    private sealed class RoleFile
    {
        public Dictionary<string, string>? Roles { get; set; }
    }
}

public sealed class RoleChecker
{
    public void Ensure(OperatorIdentity op, Role required)
    {
        if (op.Role < required)
            throw new RoleViolationException(
                $"账号 {op.Account} 角色 {op.Role} 不满足所需角色 {required}");
    }
}
```
注意：`Role` 枚举顺序 Draw=0 < Review=1 < Approve=2，`<` 比较即权限不足；职责分离的"绘制者不能自审"在审查签认流程（计划 3）用 `Ensure(op, Role.Review)` 强制。

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~RoleTests`
Expected: 5 条 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/Identity.cs src/CadAgent.Core/RoleConfig.cs tests/CadAgent.Core.Tests/RoleTests.cs
git commit -m "feat: Windows identity + YAML role config + role enforcement"
```

---

### Task 7: 快照与事务撤回

**Files:**
- Create: `src/CadAgent.Core/SnapshotStore.cs`
- Test: `tests/CadAgent.Core.Tests/SnapshotStoreTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `SnapshotStore`：ctor `string rootDir`；`string Take(string sourcePath, string txId)`（复制到 `{root}/{txId}/{filename}`，返回快照路径）；`string Find(string txId)`（返回该事务快照路径，不存在抛 `FileNotFoundException`）；`void Restore(string txId, string targetPath)`

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Core.Tests/SnapshotStoreTests.cs`：
```csharp
namespace CadAgent.Core.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cadagent-snap-{Guid.NewGuid():N}");
    private readonly string _source;
    public SnapshotStoreTests()
    {
        _source = Path.Combine(Path.GetTempPath(), $"cadagent-src-{Guid.NewGuid():N}.dxf");
        File.WriteAllText(_source, "original");
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_source)) File.Delete(_source);
    }

    [Fact]
    public void Take_CopiesFile_UnderTxDir()
    {
        var store = new SnapshotStore(_root);
        string snap = store.Take(_source, "tx1");
        Assert.True(File.Exists(snap));
        Assert.Equal("original", File.ReadAllText(snap));
        Assert.Contains("tx1", snap);
    }

    [Fact]
    public void Restore_BringsBackOriginalContent()
    {
        var store = new SnapshotStore(_root);
        store.Take(_source, "tx1");
        File.WriteAllText(_source, "modified");
        store.Restore("tx1", _source);
        Assert.Equal("original", File.ReadAllText(_source));
    }

    [Fact]
    public void Find_UnknownTx_Throws()
    {
        var store = new SnapshotStore(_root);
        Assert.Throws<FileNotFoundException>(() => store.Find("nope"));
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~SnapshotStoreTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Core/SnapshotStore.cs`：
```csharp
namespace CadAgent.Core;

public sealed class SnapshotStore
{
    private readonly string _root;
    public SnapshotStore(string rootDir) => _root = rootDir;

    public string Take(string sourcePath, string txId)
    {
        string dir = Path.Combine(_root, txId);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    public string Find(string txId)
    {
        string dir = Path.Combine(_root, txId);
        if (!Directory.Exists(dir))
            throw new FileNotFoundException($"事务 {txId} 无快照", dir);
        var files = Directory.GetFiles(dir);
        if (files.Length == 0)
            throw new FileNotFoundException($"事务 {txId} 快照目录为空", dir);
        return files[0];
    }

    public void Restore(string txId, string targetPath) =>
        File.Copy(Find(txId), targetPath, overwrite: true);
}
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~SnapshotStoreTests`
Expected: 3 条 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/SnapshotStore.cs tests/CadAgent.Core.Tests/SnapshotStoreTests.cs
git commit -m "feat: transaction snapshot store with restore"
```

---

### Task 8: 步骤引擎与计划解析

**Files:**
- Modify: `src/CadAgent.Core/StepEngine.cs`（补全 Task 2 留下的接口文件）
- Test: `tests/CadAgent.Core.Tests/StepEngineTests.cs`

**Interfaces:**
- Consumes: `IDrawingSession`（Task 2）、`IAuditStore`（Task 5）、`IIdentityProvider/RoleChecker`（Task 6）、`SnapshotStore`（Task 7）
- Produces:
  - `sealed record StepSpec(string Kind, System.Text.Json.JsonElement Params)`
  - `sealed record StepPlan(string PlanId, IReadOnlyList<StepSpec> Steps)`
  - `static class StepPlanParser`：`StepPlan Parse(string json)`（非法 JSON/缺字段抛 `InvalidPlanException`）
  - `sealed record StepResult(bool Ok, string Error, string TargetHandle)`（成功 Error=""）
  - `interface IStepHandler { string Kind { get; } StepResult Apply(IDrawingSession session, System.Text.Json.JsonElement parameters); }`
  - `sealed record TxResult(string TxId, bool Ok, int StepsDone, string Error)`
  - `StepEngine`：ctor `(IAuditStore audit, SnapshotStore snapshots, IIdentityProvider identity, RoleChecker roles, IEnumerable<IStepHandler> handlers)`；`TxResult Run(StepPlan plan, IDrawingSession session, string sessionId)`；`void Rollback(string txId, IDrawingSession session, string sessionId)`
  - 计划 JSON：`{"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":10,"y2":10}}]}`

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Core.Tests/StepEngineTests.cs`：
```csharp
using System.Text.Json;

namespace CadAgent.Core.Tests;

public class StepEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cadagent-eng-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly FakeSession _session;
    private readonly StepEngine _engine;

    public StepEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        _snapshots = new SnapshotStore(Path.Combine(_dir, "snapshots"));
        _session = new FakeSession(Path.Combine(_dir, "drawing.dxf"));
        var identity = new FixedIdentity(new OperatorIdentity("TEST\\drawer", Role.Draw));
        _engine = new StepEngine(_audit, _snapshots, identity, new RoleChecker(),
            new IStepHandler[] { new OkHandler("add-line"), new FailHandler("bad-op") });
    }

    public void Dispose()
    {
        _audit.Dispose();
        Directory.Delete(_dir, true);
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private sealed class OkHandler : IStepHandler
    {
        public OkHandler(string kind) => Kind = kind;
        public string Kind { get; }
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private sealed class FailHandler : IStepHandler
    {
        public FailHandler(string kind) => Kind = kind;
        public string Kind { get; }
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(false, "模拟失败", "");
    }

    private sealed class FakeSession : IDrawingSession
    {
        public FakeSession(string path) { SourcePath = path; File.WriteAllText(path, "v0"); }
        public string SourcePath { get; }
        public int SaveCount { get; private set; }
        public string ComputeHash() => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath)));
        public void SaveWorking() { SaveCount++; File.WriteAllText(SourcePath, "v" + SaveCount); }
        public void Reload() { }
    }

    private static StepPlan Plan(params string[] kinds) =>
        new("p1", kinds.Select(k => new StepSpec(k, JsonDocument.Parse("{}").RootElement)).ToList());

    [Fact]
    public void Parse_ValidPlan_Ok()
    {
        var plan = StepPlanParser.Parse("""
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0}}]}
            """);
        Assert.Equal("p1", plan.PlanId);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void Parse_MissingSteps_Throws()
    {
        Assert.Throws<InvalidPlanException>(() => StepPlanParser.Parse("""{"planId":"p1"}"""));
    }

    [Fact]
    public void Run_AllStepsOk_AuditsEachStep_AndSaves()
    {
        var result = _engine.Run(Plan("add-line", "add-line"), _session, "s1");
        Assert.True(result.Ok);
        Assert.Equal(2, result.StepsDone);
        Assert.Equal(1, _session.SaveCount);
        var log = _audit.ReadAll();
        Assert.Equal(4, log.Count); // tx-start + 2 步 + tx-commit
        Assert.All(log, r => Assert.Equal("TEST\\drawer", r.Entry.Operator));
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_FailingStep_HaltsAndAuditsFailure()
    {
        var result = _engine.Run(Plan("add-line", "bad-op", "add-line"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Equal(1, result.StepsDone);
        Assert.Contains("模拟失败", result.Error);
        var log = _audit.ReadAll();
        Assert.Equal("step-failed", log[^1].Entry.Kind);
        Assert.Equal(0, _session.SaveCount); // 失败事务不落盘
    }

    [Fact]
    public void Run_UnknownKind_Fails()
    {
        var result = _engine.Run(Plan("no-such-op"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Contains("no-such-op", result.Error);
    }

    [Fact]
    public void Run_HigherRoleAllowed_StampsActualRole()
    {
        // 角色是最低权限语义：Review >= Draw，可执行；职责分离（绘制者不能自审）在签认流程强制
        var reviewer = new FixedIdentity(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var engine = new StepEngine(_audit, _snapshots, reviewer, new RoleChecker(),
            new IStepHandler[] { new OkHandler("add-line") });
        var result = engine.Run(Plan("add-line"), _session, "s1");
        Assert.True(result.Ok);
        Assert.Equal("Review", _audit.ReadAll()[^1].Entry.Role);
    }

    [Fact]
    public void Rollback_RestoresSnapshot_AndAudits()
    {
        var result = _engine.Run(Plan("add-line"), _session, "s1");
        File.WriteAllText(_session.SourcePath, "corrupted");
        _engine.Rollback(result.TxId, _session, "s1");
        Assert.Equal("v0", File.ReadAllText(_session.SourcePath));
        Assert.Equal("rollback", _audit.ReadAll()[^1].Entry.Kind);
        Assert.True(_audit.VerifyChain(out _));
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~StepEngineTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Core/StepEngine.cs`（整文件替换 Task 2 的接口占位）：
```csharp
using System.Text.Json;

namespace CadAgent.Core;

public interface IDrawingSession
{
    string SourcePath { get; }
    string ComputeHash();
    void SaveWorking();
    void Reload();
}

public sealed record StepSpec(string Kind, JsonElement Params);
public sealed record StepPlan(string PlanId, IReadOnlyList<StepSpec> Steps);
public sealed record StepResult(bool Ok, string Error, string TargetHandle);
public sealed record TxResult(string TxId, bool Ok, int StepsDone, string Error);

public sealed class InvalidPlanException : Exception
{
    public InvalidPlanException(string message) : base(message) { }
}

public static class StepPlanParser
{
    public static StepPlan Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidPlanException($"计划 JSON 非法: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("planId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new InvalidPlanException("计划缺少 planId");
            if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidPlanException("计划缺少 steps 数组");
            var steps = new List<StepSpec>();
            foreach (var s in stepsEl.EnumerateArray())
            {
                if (!s.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
                    throw new InvalidPlanException("步骤缺少 kind");
                var paramsEl = s.TryGetProperty("params", out var p) ? p.Clone() : JsonDocument.Parse("{}").RootElement;
                steps.Add(new StepSpec(kindEl.GetString()!, paramsEl));
            }
            return new StepPlan(idEl.GetString()!, steps);
        }
    }
}

public interface IStepHandler
{
    string Kind { get; }
    StepResult Apply(IDrawingSession session, JsonElement parameters);
}

public sealed class StepEngine
{
    private readonly IAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly IIdentityProvider _identity;
    private readonly RoleChecker _roles;
    private readonly Dictionary<string, IStepHandler> _handlers;

    public StepEngine(IAuditStore audit, SnapshotStore snapshots, IIdentityProvider identity,
        RoleChecker roles, IEnumerable<IStepHandler> handlers)
    {
        _audit = audit;
        _snapshots = snapshots;
        _identity = identity;
        _roles = roles;
        _handlers = handlers.ToDictionary(h => h.Kind);
    }

    public TxResult Run(StepPlan plan, IDrawingSession session, string sessionId)
    {
        var op = _identity.Current;
        _roles.Ensure(op, Role.Draw);
        string txId = Guid.NewGuid().ToString("N");
        string snapshot = _snapshots.Take(session.SourcePath, txId);
        string beforeHash = session.ComputeHash();
        Append(op, sessionId, "tx-start", session.SourcePath, plan.PlanId, beforeHash, beforeHash, snapshot, plan.PlanId);

        int done = 0;
        foreach (var step in plan.Steps)
        {
            if (!_handlers.TryGetValue(step.Kind, out var handler))
            {
                string err = $"未注册的步骤类型: {step.Kind}";
                Append(op, sessionId, "step-failed", step.Kind, err, beforeHash, session.ComputeHash(), snapshot, plan.PlanId);
                return new TxResult(txId, false, done, err);
            }
            StepResult result;
            try { result = handler.Apply(session, step.Params); }
            catch (Exception ex) { result = new StepResult(false, ex.Message, ""); }

            string afterHash = session.ComputeHash();
            if (!result.Ok)
            {
                Append(op, sessionId, "step-failed", result.TargetHandle,
                    JsonSerializer.Serialize(new { step.Kind, result.Error }), beforeHash, afterHash, snapshot, plan.PlanId);
                return new TxResult(txId, false, done, result.Error);
            }
            Append(op, sessionId, "step", result.TargetHandle,
                JsonSerializer.Serialize(new { step.Kind, Params = step.Params }), beforeHash, afterHash, snapshot, plan.PlanId);
            beforeHash = afterHash;
            done++;
        }
        session.SaveWorking();
        Append(op, sessionId, "tx-commit", session.SourcePath, plan.PlanId, beforeHash, session.ComputeHash(), snapshot, plan.PlanId);
        return new TxResult(txId, true, done, "");
    }

    public void Rollback(string txId, IDrawingSession session, string sessionId)
    {
        var op = _identity.Current;
        _roles.Ensure(op, Role.Draw);
        string before = session.ComputeHash();
        _snapshots.Restore(txId, session.SourcePath);
        session.Reload();
        Append(op, sessionId, "rollback", session.SourcePath, txId, before, session.ComputeHash(), txId, "");
    }

    private void Append(OperatorIdentity op, string session, string kind, string target,
        string paramsJson, string before, string after, string snapshot, string plan) =>
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "plan", kind, target, paramsJson, before, after, snapshot, plan));
}
```
注意：`tx-start/tx-commit/step-failed` 的 ParamsJson 字段复用为错误/计划信息（字段语义在审计查询时按 Kind 区分）；失败事务不调 SaveWorking，图纸文件保持事务前状态，内存模型由调用方 Reload 复原（CLI 在失败时 Reload）。

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~StepEngineTests`
Expected: 7 条 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/StepEngine.cs tests/CadAgent.Core.Tests/StepEngineTests.cs
git commit -m "feat: deterministic step engine with plan parsing, audit stamping, halt-on-failure, rollback"
```

---

### Task 9: 图纸步骤处理器（Data 层）

**Files:**
- Create: `src/CadAgent.Data/Handlers/LineHandlers.cs`、`src/CadAgent.Data/Handlers/EditHandlers.cs`
- Test: `tests/CadAgent.Data.Tests/HandlerTests.cs`

**Interfaces:**
- Consumes: `IStepHandler/StepResult/IDrawingSession`（Task 8）、`DrawingSession`（Task 2）
- Produces: 五个 `IStepHandler` 实现，Kind 与参数契约：
  - `AddLineHandler` `"add-line"` `{x1,y1,x2,y2,layer?}` → 新实体句柄
  - `AddTextHandler` `"add-text"` `{x,y,text,height,layer?}` → 新实体句柄
  - `DeleteEntityHandler` `"delete-entity"` `{handle}` → 被删句柄
  - `MoveEntityHandler` `"move-entity"` `{handle,dx,dy}` → 句柄（支持 Line/TextEntity，其余返回失败）
  - `ChangeLayerHandler` `"change-layer"` `{handle,layer}` → 句柄（图层不存在则创建）
  - `static class HandlerRegistry`：`IReadOnlyList<IStepHandler> All()`

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Data.Tests/HandlerTests.cs`：
```csharp
using System.Text.Json;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class HandlerTests : IDisposable
{
    private readonly string _path;
    private readonly DrawingSession _session;
    public HandlerTests()
    {
        _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _session = DrawingSession.Load(_path);
    }
    public void Dispose() => File.Delete(_path);

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;
    private ulong FirstHandle => ulong.Parse(_session.Summarize().Entities[0].Handle);

    [Fact]
    public void AddLine_AddsEntity()
    {
        var r = new AddLineHandler().Apply(_session, P("""{"x1":0,"y1":0,"x2":5,"y2":5}"""));
        Assert.True(r.Ok);
        Assert.Equal(2, _session.Summarize().Entities.Count);
    }

    [Fact]
    public void AddText_AddsEntityWithContent()
    {
        var r = new AddTextHandler().Apply(_session, P("""{"x":1,"y":2,"text":"测试","height":3.5}"""));
        Assert.True(r.Ok);
        Assert.Contains(_session.Summarize().Entities, e => e.Kind == "TextEntity");
    }

    [Fact]
    public void DeleteEntity_RemovesByHandle()
    {
        var r = new DeleteEntityHandler().Apply(_session, P($"{{\"handle\":{FirstHandle}}}"));
        Assert.True(r.Ok);
        Assert.Empty(_session.Summarize().Entities);
    }

    [Fact]
    public void DeleteEntity_UnknownHandle_Fails()
    {
        var r = new DeleteEntityHandler().Apply(_session, P("""{"handle":999999}"""));
        Assert.False(r.Ok);
    }

    [Fact]
    public void MoveEntity_ShiftsLine()
    {
        var r = new MoveEntityHandler().Apply(_session, P($"{{\"handle\":{FirstHandle},\"dx\":10,\"dy\":20}}"));
        Assert.True(r.Ok);
        var line = (ACadSharp.Entities.Line)_session.Doc.Entities.First();
        Assert.Equal(10, line.StartPoint.X, 3);
        Assert.Equal(20, line.StartPoint.Y, 3);
    }

    [Fact]
    public void ChangeLayer_CreatesLayerAndAssigns()
    {
        var r = new ChangeLayerHandler().Apply(_session, P($"{{\"handle\":{FirstHandle},\"layer\":\"E-LITE\"}}"));
        Assert.True(r.Ok);
        Assert.Contains("E-LITE", _session.Summarize().Layers);
        Assert.Equal("E-LITE", _session.Summarize().Entities[0].Layer);
    }

    [Fact]
    public void Registry_ContainsAllFiveKinds()
    {
        var kinds = HandlerRegistry.All().Select(h => h.Kind).ToHashSet();
        Assert.Equal(new[] { "add-line", "add-text", "delete-entity", "move-entity", "change-layer" }.ToHashSet(), kinds);
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~HandlerTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Data/Handlers/LineHandlers.cs`：
```csharp
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Math;
using ACadSharp.Tables;
using CadAgent.Core;

namespace CadAgent.Data.Handlers;

internal static class HandlerHelpers
{
    public static DrawingSession AsDrawing(IDrawingSession session) =>
        session as DrawingSession ?? throw new InvalidOperationException("处理器需要 DrawingSession");

    public static double Req(JsonElement p, string name) => p.GetProperty(name).GetDouble();
    public static string? OptString(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static Layer ResolveLayer(DrawingSession s, string? name)
    {
        if (string.IsNullOrEmpty(name)) return s.Doc.Layers.First(l => l.Name == "0");
        var existing = s.Doc.Layers.FirstOrDefault(l => l.Name == name);
        if (existing != null) return existing;
        var layer = new Layer(name);
        s.Doc.Layers.Add(layer);
        return layer;
    }

    public static Entity? Find(DrawingSession s, JsonElement p)
    {
        ulong handle = p.GetProperty("handle").GetUInt64();
        return s.Doc.Entities.FirstOrDefault(e => e.Handle == handle);
    }
}

public sealed class AddLineHandler : IStepHandler
{
    public string Kind => "add-line";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var line = new Line
        {
            StartPoint = new XY(HandlerHelpers.Req(p, "x1"), HandlerHelpers.Req(p, "y1")),
            EndPoint = new XY(HandlerHelpers.Req(p, "x2"), HandlerHelpers.Req(p, "y2")),
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        s.Doc.Entities.Add(line);
        return new StepResult(true, "", line.Handle.ToString());
    }
}

public sealed class AddTextHandler : IStepHandler
{
    public string Kind => "add-text";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var text = new TextEntity
        {
            InsertPoint = new XY(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y")),
            Value = p.GetProperty("text").GetString() ?? "",
            Height = HandlerHelpers.Req(p, "height"),
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        s.Doc.Entities.Add(text);
        return new StepResult(true, "", text.Handle.ToString());
    }
}
```

`src/CadAgent.Data/Handlers/EditHandlers.cs`：
```csharp
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Math;
using CadAgent.Core;

namespace CadAgent.Data.Handlers;

public sealed class DeleteEntityHandler : IStepHandler
{
    public string Kind => "delete-entity";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var entity = HandlerHelpers.Find(s, p);
        if (entity == null) return new StepResult(false, "句柄不存在", "");
        string handle = entity.Handle.ToString();
        s.Doc.Entities.Remove(entity);
        return new StepResult(true, "", handle);
    }
}

public sealed class MoveEntityHandler : IStepHandler
{
    public string Kind => "move-entity";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var entity = HandlerHelpers.Find(s, p);
        if (entity == null) return new StepResult(false, "句柄不存在", "");
        double dx = HandlerHelpers.Req(p, "dx"), dy = HandlerHelpers.Req(p, "dy");
        switch (entity)
        {
            case Line line:
                line.StartPoint = new XY(line.StartPoint.X + dx, line.StartPoint.Y + dy);
                line.EndPoint = new XY(line.EndPoint.X + dx, line.EndPoint.Y + dy);
                break;
            case TextEntity text:
                text.InsertPoint = new XY(text.InsertPoint.X + dx, text.InsertPoint.Y + dy);
                break;
            default:
                return new StepResult(false, $"move 暂不支持实体类型 {entity.GetType().Name}", "");
        }
        return new StepResult(true, "", entity.Handle.ToString());
    }
}

public sealed class ChangeLayerHandler : IStepHandler
{
    public string Kind => "change-layer";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var entity = HandlerHelpers.Find(s, p);
        if (entity == null) return new StepResult(false, "句柄不存在", "");
        entity.Layer = HandlerHelpers.ResolveLayer(s, p.GetProperty("layer").GetString());
        return new StepResult(true, "", entity.Handle.ToString());
    }
}

public static class HandlerRegistry
{
    public static IReadOnlyList<IStepHandler> All() => new IStepHandler[]
    {
        new AddLineHandler(), new AddTextHandler(),
        new DeleteEntityHandler(), new MoveEntityHandler(), new ChangeLayerHandler()
    };
}
```
ACadSharp 集合/属性签名以实际包为准微调（如 `Layers` 索引器、`Entities.Remove`），测试意图不变。

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test --filter FullyQualifiedName~HandlerTests`
Expected: 7 条 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Data/Handlers tests/CadAgent.Data.Tests/HandlerTests.cs
git commit -m "feat: five drawing step handlers (add-line/add-text/delete/move/change-layer)"
```

---

### Task 10: CLI 命令路由与端到端

**Files:**
- Create: `src/CadAgent.Cli/CommandRouter.cs`
- Modify: `src/CadAgent.Cli/Program.cs`
- Test: `tests/CadAgent.Cli.Tests/CommandRouterTests.cs`

**Interfaces:**
- Consumes: 全部前序任务
- Produces:
  - `static class CommandRouter`：`int Run(string[] args, TextWriter stdout, TextWriter stderr)`
  - 命令与退出码：
    - `intake <file> [--project <dir>]` → 闸口报告 JSON；退出码 0=Green / 2=Yellow / 3=Red / 1=错误
    - `run <plan.json> --drawing <file> [--project <dir>] [--session <id>]` → TxResult JSON；0=成功 / 1=失败
    - `rollback <txId> --drawing <file> [--project <dir>]` → 0/1
    - `audit verify [--project <dir>]` → 0=链完整 / 1=断链
    - `roles show [--project <dir>]` → 当前账号+角色；0
  - 项目目录约定：`<dir>/.cadagent/audit.db`、`<dir>/.cadagent/snapshots/`、`<dir>/.cadagent/roles.yaml`（dir 默认当前目录）

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Cli.Tests/CommandRouterTests.cs`：
```csharp
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class CommandRouterTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"cadagent-cli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public CommandRouterTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void Intake_PlainDxf_Green_AndAudited()
    {
        var (code, stdout, _) = Run("intake", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("Green", stdout);
        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
    }

    [Fact]
    public void Run_Plan_AddsLine_AndCommits()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        var session = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(2, session.Summarize().Entities.Count);
    }

    [Fact]
    public void Run_BadPlan_KindFails_ExitOne()
    {
        string planPath = Path.Combine(_project, "bad.json");
        File.WriteAllText(planPath, """{"planId":"p1","steps":[{"kind":"nope","params":{}}]}""");
        var (code, _, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(1, code);
    }

    [Fact]
    public void Rollback_RestoresDrawing()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"delete-entity","params":{"handle":HANDLE}}]}
            """.Replace("HANDLE", CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities[0].Handle));
        var (_, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        string txId = System.Text.Json.JsonDocument.Parse(runOut).RootElement.GetProperty("txId").GetString()!;
        var (code, _, _) = Run("rollback", txId, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities);
    }

    [Fact]
    public void AuditVerify_TamperedDb_ExitOne()
    {
        Run("intake", _drawing);
        string db = Path.Combine(_project, ".cadagent", "audit.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audit SET operator='x' WHERE seq=1";
            cmd.ExecuteNonQuery();
        }
        var (code, _, stderr) = Run("audit", "verify");
        Assert.Equal(1, code);
        Assert.Contains("seq 1", stderr);
    }

    [Fact]
    public void RolesShow_PrintsCurrentIdentity()
    {
        var (code, stdout, _) = Run("roles", "show");
        Assert.Equal(0, code);
        Assert.Contains("Draw", stdout); // 未配置账号默认 Draw
    }

    [Fact]
    public void UnknownCommand_ExitOne_WithUsage()
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "frobnicate" }, outW, errW);
        Assert.Equal(1, code);
        Assert.Contains("intake", errW.ToString());
    }
}
```
注意：Cli.Tests 需引用 Data.Tests 项目以复用 DxfFixture（`dotnet add tests/CadAgent.Cli.Tests reference tests/CadAgent.Data.Tests`），或将 DxfFixture 移入共享测试工具项目——采用前者，最少改动。

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test --filter FullyQualifiedName~CommandRouterTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/CadAgent.Cli/CommandRouter.cs`：
```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;

namespace CadAgent.Cli;

public static class CommandRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0) return Usage(stderr);
            string project = GetOption(args, "--project") ?? Directory.GetCurrentDirectory();
            string agentDir = Path.Combine(project, ".cadagent");
            Directory.CreateDirectory(agentDir);
            string session = GetOption(args, "--session") ?? Guid.NewGuid().ToString("N");
            var positional = args.Where(a => !a.StartsWith("--")).ToArray();
            // 去掉选项值（--project/--session/--drawing 后跟的值）
            positional = StripOptionValues(args);

            switch (positional[0])
            {
                case "intake": return Intake(positional[1], agentDir, session, stdout);
                case "run": return RunPlan(positional[1], RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "rollback": return Rollback(positional[1], RequireOption(args, "--drawing"), agentDir, session, stdout);
                case "audit": return AuditVerify(agentDir, stdout, stderr);
                case "roles": return RolesShow(agentDir, stdout);
                default: return Usage(stderr);
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string[] StripOptionValues(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) { i++; continue; }
            list.Add(args[i]);
        }
        return list.ToArray();
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string RequireOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException($"缺少必需选项 {name}");

    private static SqliteAuditStore OpenAudit(string agentDir) =>
        new(Path.Combine(agentDir, "audit.db"));

    private static IIdentityProvider Identity(string agentDir) =>
        new WindowsIdentityProvider(RoleConfig.Load(Path.Combine(agentDir, "roles.yaml")));

    private static int Intake(string file, string agentDir, string session, TextWriter stdout)
    {
        var drawing = DrawingSession.Load(file);
        var summary = drawing.Summarize();
        var report = GateDetector.Inspect(summary, new EmptySupportMatrix());
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "intake", "intake", file,
            JsonSerializer.Serialize(new { summary.Version, Entities = summary.Entities.Count, Unknowns = summary.Unknowns.Count }),
            drawing.ComputeHash(), drawing.ComputeHash(), "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            level = report.Level.ToString(),
            unsupported = report.Unsupported,
            guidance = report.Guidance,
            summary
        }, JsonOpts));
        return report.Level switch { GateLevel.Green => 0, GateLevel.Yellow => 2, _ => 3 };
    }

    private static StepEngine Engine(string agentDir, IAuditStore audit) =>
        new(audit, new SnapshotStore(Path.Combine(agentDir, "snapshots")),
            Identity(agentDir), new RoleChecker(), HandlerRegistry.All());

    private static int RunPlan(string planPath, string drawingPath, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        var plan = StepPlanParser.Parse(File.ReadAllText(planPath));
        var drawing = DrawingSession.Load(drawingPath);
        using var audit = OpenAudit(agentDir);
        var result = Engine(agentDir, audit).Run(plan, drawing, session);
        if (!result.Ok) drawing.Reload();
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            txId = result.TxId, ok = result.Ok, stepsDone = result.StepsDone, error = result.Error
        }, JsonOpts));
        return result.Ok ? 0 : 1;
    }

    private static int Rollback(string txId, string drawingPath, string agentDir, string session, TextWriter stdout)
    {
        var drawing = DrawingSession.Load(drawingPath);
        using var audit = OpenAudit(agentDir);
        Engine(agentDir, audit).Rollback(txId, drawing, session);
        stdout.WriteLine($"已撤回事务 {txId}");
        return 0;
    }

    private static int AuditVerify(string agentDir, TextWriter stdout, TextWriter stderr)
    {
        using var audit = OpenAudit(agentDir);
        if (audit.VerifyChain(out string error))
        {
            stdout.WriteLine($"审计链完整，共 {audit.ReadAll().Count} 条记录");
            return 0;
        }
        stderr.WriteLine(error);
        return 1;
    }

    private static int RolesShow(string agentDir, TextWriter stdout)
    {
        var op = Identity(agentDir).Current;
        stdout.WriteLine($"{op.Account}\t{op.Role}");
        return 0;
    }

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("用法: cadagent <intake|run|rollback|audit verify|roles show> [参数] [--project <dir>]");
        return 1;
    }
}
```

`src/CadAgent.Cli/Program.cs`（整文件替换）：
```csharp
using System.Text;
using CadAgent.Cli;

Console.OutputEncoding = Encoding.UTF8;
return CommandRouter.Run(args, Console.Out, Console.Error);
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test`
Expected: 全部测试 PASS（含 7 条 CLI 端到端）。

- [ ] **Step 5: 手工冒烟（真机）**

```bash
dotnet run --project src/CadAgent.Cli -- intake <任一真实.dxf> --project ./demo
dotnet run --project src/CadAgent.Cli -- audit verify --project ./demo
```
Expected: intake 输出 Green 报告；verify 输出链完整。demo 目录测试后删除。

- [ ] **Step 6: Commit**

```bash
git add src/CadAgent.Cli tests/CadAgent.Cli.Tests
git commit -m "feat: CLI router with intake/run/rollback/audit-verify/roles commands"
```

---

## 版本记录

（Task 1 执行时填写，2026-11-20）
- ACadSharp 实际解析版本：3.8.35
- .NET SDK 实际安装版本：8.0.428（winget Microsoft.DotNet.SDK.8）

## 后续计划（本计划不含）

- 计划 2：语义重建层 + 图层纠偏 + 规则引擎（效力分级/来源引用/裁定沉淀）+ skill YAML + 问题清单
- 计划 3：LLM profile/DPAPI/模型发现/数据出境 + 编排 + 审查签认 + **步级 undo 栈（会话内细粒度回退，spec §5.4，随会话管理落地）** + 中望 COM 适配器 + 保真导出 + 审计包 + 厂商支持矩阵
- 计划 4：Avalonia 视图 + 基础手动编辑 + 审计回放 + 全部人工交互界面