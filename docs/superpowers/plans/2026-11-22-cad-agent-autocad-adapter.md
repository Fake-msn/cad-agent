# AutoCAD 适配器切片 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付 AutoCAD COM 适配器（ICadRuntime 全接口），把 PR#17 的 CUIX 竞态守卫/退出等待参数化为厂商无关机制，CLI 可选运行时，真机验证以条件测试+工作环境协议诚实 deferred。

**Architecture:** Core.Cad 新增 VendorProfile（厂商差异数据）+ VendorGuard（生命周期守卫，PR#17 机制泛化）+ CuixLocator（CUIX 定位）；ZwcadRuntime 行为保持重构委托三者；新 CadAgent.Adapters.Acad 项目镜像 ZwcadRuntime 结构实现 AcadRuntime（dynamic COM，ProgID AutoCAD.Application）；CLI 经 CADAGENT_RUNTIME 环境变量选择运行时（默认 zwcad）。本机无 AutoCAD（孤儿 ProgID 注册，CLSID 无 LocalServer32）→ 真机测试 [SkippableFact] 条件跳过，矩阵保持 CompatibleUnverified 直至工作环境真机证据。

**Tech Stack:** .NET 8 / C# dynamic COM（不编译期引用厂商 interop）/ xUnit 2.5.3 + Xunit.SkippableFact 1.4.13 / ACadSharp 3.8.35（读回闸口）

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §3（适配器一等公民、Core 厂商无关）、§11（厂商支持矩阵，Verified 须真机证据）、§12（AutoCAD 参数化承接约束）

## Global Constraints

- 分支 `feat/cad-agent-autocad-adapter`（从 main 67b17d4 切出；主树直接作业，用户已释放 worktree 约束）。
- Core 项目厂商无关：VendorProfile/VendorGuard/CuixLocator 仅 BCL（System.Diagnostics 等），不得引用任何厂商库或适配器项目。
- 适配器不编译期引用厂商 interop DLL——dynamic COM + ProgID（版本碎片化隔离，spec §3）。
- ACadSharp  pinned 3.8.35；CSMath 命名空间；(XYZ) 转换；MText : Entity。
- 真机测试必须串行：xunit.runner.json `parallelizeTestCollections: false` + 集合属性；真机测试期间禁并行厂商自动化/人工会话（CUIX 竞态根因 b）。
- 厂商矩阵 status=Verified 必须有真机测试证据；本切片 AutoCAD 保持 CompatibleUnverified（本机无 AutoCAD）。
- 所有 COM 失败统一 CadRuntimeException；Quit 前必须先 Close 文档（根因 a）；Dispose 等待进程退出（CUIX 回写窗口）。
- git 署名仅环境变量：`GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`，绝不改 git config。
- 终端 GBK：脚本输出中文写 UTF-8 文件再 Read，不直接 print；dotnet test 多项目参数报 MSB1008——用单项目或全 sln。
- 用户界面消息/注释中文；回复中文。
- 每个任务结束提交一次（TDD：红→绿→提交）。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Create `src/CadAgent.Core/Cad/VendorProfile.cs` | 厂商差异数据记录 + Zwcad/Acad 预置 | 1 |
| Create `src/CadAgent.Core/Cad/VendorGuard.cs` | 启动守卫 + 退出等待（参数化，seam 可注入） | 1 |
| Modify `src/CadAgent.Core/CadAgent.Core.csproj` | InternalsVisibleTo 三个测试项目 | 1 |
| Modify `src/CadAgent.Adapters.Zwcad/ZwcadRuntime.cs` | 删除自有守卫静态量，委托 VendorGuard；EnsureBlock 按 profile 分支 | 1 |
| Create `tests/CadAgent.Core.Tests/VendorGuardTests.cs` | 守卫参数化测试（双厂商 Theory） | 1 |
| Modify `tests/CadAgent.Adapters.Zwcad.Tests/ForeignInstanceGuardTests.cs` | 缩减为 Dispose 委托集成测试（其余移 Core.Tests） | 1 |
| Create `src/CadAgent.Core/Cad/CuixLocator.cs` | 厂商用户 profile CUIX 定位（glob，未装返回空） | 2 |
| Create `tests/CadAgent.Core.Tests/CuixLocatorTests.cs` | 临时目录假 profile 树定位测试 | 2 |
| Modify `tests/CadAgent.Adapters.Zwcad.Tests/CuixHealthTests.cs` | 金丝雀改用 CuixLocator | 2 |
| Create `src/CadAgent.Adapters.Acad/CadAgent.Adapters.Acad.csproj` | 新适配器项目 | 3 |
| Create `src/CadAgent.Adapters.Acad/AcadRuntime.cs` | ICadRuntime 全接口 dynamic COM 实现 | 3 |
| Create `tests/CadAgent.Adapters.Acad.Tests/CadAgent.Adapters.Acad.Tests.csproj` | 测试项目（含 SkippableFact） | 3 |
| Create `tests/CadAgent.Adapters.Acad.Tests/xunit.runner.json` | 串行化 | 3 |
| Create `tests/CadAgent.Adapters.Acad.Tests/AcadRuntimeTests.cs` | 无 COM 单元测试（seam 注入） | 3 |
| Modify `CadAgent.sln` | 加入两个新项目 | 3 |
| Create `tests/CadAgent.Adapters.Acad.Tests/AcadProbe.cs` | 真机可用性探针（Lazy 缓存） | 4 |
| Create `tests/CadAgent.Adapters.Acad.Tests/AcadLiveTests.cs` | 条件真机测试（RequiresAutoCAD） | 4 |
| Create `docs/verification/autocad-adapter-protocol.md` | 工作环境真机验证协议 | 4 |
| Modify `src/CadAgent.Cli/CommandRouter.Export.cs` | CreateRuntimeIfAvailable + CADAGENT_RUNTIME 选择 | 5 |
| Modify `src/CadAgent.Cli/CommandRouter.Live.cs:44` | 调用点改名 | 5 |
| Modify `src/CadAgent.Cli/CadAgent.Cli.csproj` | 引用 Acad 适配器 | 5 |
| Modify `src/CadAgent.Cli/vendor-support.json` | AutoCAD adapterVersion 1.0 | 5 |
| Create `tests/CadAgent.Cli.Tests/RuntimeSelectionTests.cs` | 运行时选择单元测试 | 5 |
| Modify `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §12 | 切片交付注记 | 6 |

---

### Task 1: VendorProfile + VendorGuard 参数化，ZwcadRuntime 行为保持重构

**Files:**
- Create: `src/CadAgent.Core/Cad/VendorProfile.cs`
- Create: `src/CadAgent.Core/Cad/VendorGuard.cs`
- Modify: `src/CadAgent.Core/CadAgent.Core.csproj`（加 InternalsVisibleTo）
- Modify: `src/CadAgent.Adapters.Zwcad/ZwcadRuntime.cs`（删 62-80 行守卫静态量与 244-255 行 ExitWait/WaitForProcessExit；EnsureApp/Dispose/EnsureBlock 改委托）
- Create: `tests/CadAgent.Core.Tests/VendorGuardTests.cs`
- Modify: `tests/CadAgent.Adapters.Zwcad.Tests/ForeignInstanceGuardTests.cs`（整体重写为 Dispose 集成测试）

**Interfaces:**
- Consumes: `CadAgent.Core.Cad.CadRuntimeException(string message)`（已有）
- Produces:
  - `public sealed record VendorProfile(string Vendor, string ProgId, string ProcessName, bool BlocksAddNameFirst)`
  - `public static class VendorProfiles { public static readonly VendorProfile Zwcad; public static readonly VendorProfile Acad; }`
  - `public static class VendorGuard`：`public static void EnsureNoForeignInstance(VendorProfile profile)`、`public static void WaitForProcessExit(VendorProfile profile, TimeSpan timeout)`；internal static seam `Func<string[]> ProcessNames`、`TimeSpan GuardWait`（默认 10s）、`TimeSpan ExitWait`（默认 15s）

- [ ] **Step 1: 切分支**

```bash
git checkout -b feat/cad-agent-autocad-adapter main
```

- [ ] **Step 2: 写失败测试 `tests/CadAgent.Core.Tests/VendorGuardTests.cs`**

```csharp
using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

/// <summary>
/// 厂商实例守卫参数化测试（PR#17 CUIX 竞态修复泛化）：
/// ProcessNames seam 注入，不触真机 COM。双厂商等价类覆盖。
/// </summary>
public class VendorGuardTests : IDisposable
{
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;
    private readonly TimeSpan _originalWait = VendorGuard.GuardWait;
    private readonly TimeSpan _originalExit = VendorGuard.ExitWait;

    public void Dispose()
    {
        VendorGuard.ProcessNames = _originalNames;
        VendorGuard.GuardWait = _originalWait;
        VendorGuard.ExitWait = _originalExit;
    }

    public static IEnumerable<object[]> Profiles()
    {
        yield return new object[] { VendorProfiles.Zwcad };
        yield return new object[] { VendorProfiles.Acad };
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void NoInstance_ReturnsImmediately(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
        VendorGuard.EnsureNoForeignInstance(profile);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void ForeignInstancePresent_ThrowsWithCuixMessage(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => new[] { profile.ProcessName };
        VendorGuard.GuardWait = TimeSpan.FromMilliseconds(300);
        var ex = Assert.Throws<CadRuntimeException>(() => VendorGuard.EnsureNoForeignInstance(profile));
        Assert.Contains("CUIX", ex.Message);
        Assert.Contains("串行", ex.Message);
        Assert.Contains(profile.Vendor, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void ForeignInstanceExitsWithinWait_Proceeds(VendorProfile profile)
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { profile.ProcessName } : Array.Empty<string>();
        VendorGuard.GuardWait = TimeSpan.FromSeconds(5);
        VendorGuard.EnsureNoForeignInstance(profile);
        Assert.True(calls >= 3);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void CaseInsensitiveProcessName_Detected(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => new[] { profile.ProcessName.ToUpperInvariant() };
        VendorGuard.GuardWait = TimeSpan.FromMilliseconds(300);
        Assert.Throws<CadRuntimeException>(() => VendorGuard.EnsureNoForeignInstance(profile));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void OtherVendorProcess_NotDetected(VendorProfile profile)
    {
        // 异厂商进程不触发守卫（ZWCAD 守卫不得拦 acad，反之亦然）
        var other = profile.Vendor == "ZWCAD" ? "acad" : "ZWCAD";
        VendorGuard.ProcessNames = () => new[] { other };
        VendorGuard.EnsureNoForeignInstance(profile);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void WaitForProcessExit_ClearsWithinWait_Returns(VendorProfile profile)
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { profile.ProcessName } : Array.Empty<string>();
        VendorGuard.WaitForProcessExit(profile, TimeSpan.FromSeconds(5));
        Assert.True(calls >= 3);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void WaitForProcessExit_Timeout_ReturnsSilently(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => new[] { profile.ProcessName };
        VendorGuard.WaitForProcessExit(profile, TimeSpan.FromMilliseconds(300));
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test tests/CadAgent.Core.Tests --filter VendorGuardTests`
Expected: 编译失败 CS0246（VendorProfile/VendorGuard 不存在）

- [ ] **Step 4: 实现 `src/CadAgent.Core/Cad/VendorProfile.cs`**

```csharp
namespace CadAgent.Core.Cad;

/// <summary>
/// 厂商运行时差异数据（spec §3/§11）：守卫进程名/ProgID/COM 签名序参数化。
/// Core 厂商无关——本记录仅数据，不含厂商逻辑。
/// </summary>
public sealed record VendorProfile(string Vendor, string ProgId, string ProcessName, bool BlocksAddNameFirst);

public static class VendorProfiles
{
    // ZWCAD Blocks.Add 实测签名序=(origin, Name)（2026-11-20 真机探针核定，与 AutoCAD 文档序相反）
    public static readonly VendorProfile Zwcad = new("ZWCAD", "ZWCAD.Application", "ZWCAD", BlocksAddNameFirst: false);

    // AutoCAD Blocks.Add 按 Autodesk 文档序=(Name, origin)；开发机无 AutoCAD，待工作环境真机探针核定
    // （docs/verification/autocad-adapter-protocol.md）。若实测相反，翻此标志即可，无需改适配器代码。
    public static readonly VendorProfile Acad = new("AutoCAD", "AutoCAD.Application", "acad", BlocksAddNameFirst: true);
}
```

- [ ] **Step 5: 实现 `src/CadAgent.Core/Cad/VendorGuard.cs`**

```csharp
using System.Diagnostics;

namespace CadAgent.Core.Cad;

/// <summary>
/// 厂商实例生命周期守卫（PR#17 CUIX 竞态修复泛化，2026-11-22 事故）：
/// 并发实例共享同一用户 profile → CUIX 加载/保存竞态 → 损坏 → 下次启动弹模态修复框
/// （挂起无人值守自动化）。启动前等待已有实例退出，超时拒绝；Quit 后等待进程退出
/// 闭合 CUIX 回写窗口。机制厂商无关，差异经 VendorProfile 参数化。
/// </summary>
public static class VendorGuard
{
    internal static Func<string[]> ProcessNames =
        () => Process.GetProcesses().Select(p => p.ProcessName).ToArray();
    internal static TimeSpan GuardWait = TimeSpan.FromSeconds(10);
    internal static TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    public static void EnsureNoForeignInstance(VendorProfile profile)
    {
        var deadline = DateTime.UtcNow + GuardWait;
        while (ProcessNames().Any(n => n.Equals(profile.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            if (DateTime.UtcNow >= deadline)
                throw new CadRuntimeException(
                    $"检测到已有 {profile.Vendor} 进程：并发实例共享用户配置会竞态损坏 CUIX。" +
                    $"请关闭已有 {profile.Vendor}（含人工会话）后重试；真机测试须串行运行。");
            Thread.Sleep(250);
        }
    }

    public static void WaitForProcessExit(VendorProfile profile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!ProcessNames().Any(n => n.Equals(profile.ProcessName, StringComparison.OrdinalIgnoreCase)))
                return;
            Thread.Sleep(250);
        }
    }
}
```

- [ ] **Step 6: Core csproj 加 InternalsVisibleTo**

在 `src/CadAgent.Core/CadAgent.Core.csproj` 的 `</PropertyGroup>` 后插入：

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="CadAgent.Core.Tests" />
    <InternalsVisibleTo Include="CadAgent.Adapters.Zwcad.Tests" />
    <InternalsVisibleTo Include="CadAgent.Adapters.Acad.Tests" />
  </ItemGroup>
```

- [ ] **Step 7: 运行守卫测试确认绿**

Run: `dotnet test tests/CadAgent.Core.Tests --filter VendorGuardTests`
Expected: PASS（14 个 Theory 实例）

- [ ] **Step 8: ZwcadRuntime 行为保持重构**

`src/CadAgent.Adapters.Zwcad/ZwcadRuntime.cs` 改动（其余逐字保留）：
1. 删 `using System.Diagnostics;`（不再直接用）。
2. 删 62-80 行（注释 + ProcessNames/GuardWait/EnsureNoForeignInstance 整块）。
3. `EnsureApp()` 中 `EnsureNoForeignInstance();` → `VendorGuard.EnsureNoForeignInstance(VendorProfiles.Zwcad);`
4. `EnsureBlock` 中 `dynamic block = blocks.Add(Pt(0, 0), blockName);` →

```csharp
            // Blocks.Add 签名序按厂商 profile 分支（ZWCAD 实测 origin 先；AutoCAD 文档 Name 先）
            dynamic block = VendorProfiles.Zwcad.BlocksAddNameFirst
                ? blocks.Add(blockName, Pt(0, 0))
                : blocks.Add(Pt(0, 0), blockName);
```

5. `Dispose()` 末尾 `WaitForProcessExit(ExitWait);` → `VendorGuard.WaitForProcessExit(VendorProfiles.Zwcad, VendorGuard.ExitWait);`（保留其上方两行中文注释）。
6. 删 244-255 行（ExitWait + WaitForProcessExit 整块）。

- [ ] **Step 9: 重写 `tests/CadAgent.Adapters.Zwcad.Tests/ForeignInstanceGuardTests.cs`**

守卫行为测试已移 Core.Tests（VendorGuardTests 双厂商覆盖）；本文件缩减为 Dispose 委托集成测试：

```csharp
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// ZwcadRuntime.Dispose → VendorGuard 退出等待委托集成测试（seam 注入，不触 COM）。
/// 守卫行为本身见 Core.Tests/VendorGuardTests（双厂商参数化）。
/// </summary>
public class ForeignInstanceGuardTests : IDisposable
{
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;
    private readonly TimeSpan _originalExit = VendorGuard.ExitWait;

    public void Dispose()
    {
        VendorGuard.ProcessNames = _originalNames;
        VendorGuard.ExitWait = _originalExit;
    }

    [Fact]
    public void Dispose_WaitsForProcessExit()
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { "ZWCAD" } : Array.Empty<string>();
        VendorGuard.ExitWait = TimeSpan.FromSeconds(5);
        using (var rt = new ZwcadRuntime()) { }
        Assert.True(calls >= 3);
    }
}
```

- [ ] **Step 10: 全量回归（含 ZWCAD 真机）**

Run: `dotnet test tests/CadAgent.Adapters.Zwcad.Tests`
Expected: PASS（含 13 真机 + 金丝雀；真机期间不得开人工会话）
Run: `dotnet test tests/CadAgent.Core.Tests`
Expected: PASS

- [ ] **Step 11: 提交**

```bash
git add src/CadAgent.Core/Cad/VendorProfile.cs src/CadAgent.Core/Cad/VendorGuard.cs src/CadAgent.Core/CadAgent.Core.csproj src/CadAgent.Adapters.Zwcad/ZwcadRuntime.cs tests/CadAgent.Core.Tests/VendorGuardTests.cs tests/CadAgent.Adapters.Zwcad.Tests/ForeignInstanceGuardTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "refactor: parameterize vendor guard/profile (PR#17 CUIX fix generalization)"
```

---

### Task 2: CuixLocator + 金丝雀泛化

**Files:**
- Create: `src/CadAgent.Core/Cad/CuixLocator.cs`
- Create: `tests/CadAgent.Core.Tests/CuixLocatorTests.cs`
- Modify: `tests/CadAgent.Adapters.Zwcad.Tests/CuixHealthTests.cs`

**Interfaces:**
- Consumes: `VendorProfile`/`VendorProfiles`（Task 1）
- Produces: `public static class CuixLocator { public static IReadOnlyList<string> FindCuix(VendorProfile profile); }`；internal static seam `Func<string> AppData`（默认 `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`）

- [ ] **Step 1: 写失败测试 `tests/CadAgent.Core.Tests/CuixLocatorTests.cs`**

```csharp
using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

/// <summary>
/// CUIX 定位测试：临时目录假 profile 树（等价类：ZWCAD 版本/语言目录、
/// AutoCAD 版本/release/语言目录、未安装空返回）。AppData seam 注入。
/// </summary>
public class CuixLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cuixloc-{Guid.NewGuid():N}");
    private readonly Func<string> _originalAppData = CuixLocator.AppData;

    public CuixLocatorTests()
    {
        Directory.CreateDirectory(_root);
        CuixLocator.AppData = () => _root;
    }

    public void Dispose()
    {
        CuixLocator.AppData = _originalAppData;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeFile(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void ZwcadProfile_FindsCuix_AcrossVersionAndLang()
    {
        var expected = MakeFile("ZWSOFT", "ZWCAD", "2025", "zh-CN", "Support", "ZWCAD.CUIX");
        var found = CuixLocator.FindCuix(VendorProfiles.Zwcad);
        Assert.Equal(new[] { expected }, found);
    }

    [Fact]
    public void AcadProfile_FindsCuix_AcrossReleaseAndLang()
    {
        var a = MakeFile("Autodesk", "AutoCAD 2025", "R25.0", "enu", "Support", "acad.cuix");
        var b = MakeFile("Autodesk", "AutoCAD 2024", "R24.3", "zh-CN", "Support", "acad.cuix");
        var found = CuixLocator.FindCuix(VendorProfiles.Acad);
        Assert.Equal(2, found.Count);
        Assert.Contains(a, found);
        Assert.Contains(b, found);
    }

    [Fact]
    public void AcadProfile_IgnoresNonAutoCadDirs()
    {
        MakeFile("Autodesk", "DWG TrueView 2025", "R25.0", "enu", "Support", "acad.cuix");
        Assert.Empty(CuixLocator.FindCuix(VendorProfiles.Acad));
    }

    [Fact]
    public void NoProfileDirs_ReturnsEmpty()
    {
        Assert.Empty(CuixLocator.FindCuix(VendorProfiles.Zwcad));
        Assert.Empty(CuixLocator.FindCuix(VendorProfiles.Acad));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Core.Tests --filter CuixLocatorTests`
Expected: 编译失败 CS0246（CuixLocator 不存在）

- [ ] **Step 3: 实现 `src/CadAgent.Core/Cad/CuixLocator.cs`**

```csharp
namespace CadAgent.Core.Cad;

/// <summary>
/// 厂商用户 profile CUIX 文件定位（健康金丝雀 + 事故留证）。
/// 目录 glob 覆盖版本/语言变体；未安装厂商返回空列表（不抛）。
/// </summary>
public static class CuixLocator
{
    internal static Func<string> AppData =
        () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static IReadOnlyList<string> FindCuix(VendorProfile profile)
    {
        var root = AppData();
        string vendorRoot;
        string dirPattern;
        string fileName;
        if (profile.Vendor == "ZWCAD")
        {
            vendorRoot = Path.Combine(root, "ZWSOFT", "ZWCAD");
            dirPattern = "*";
            fileName = "ZWCAD.CUIX";
        }
        else if (profile.Vendor == "AutoCAD")
        {
            vendorRoot = Path.Combine(root, "Autodesk");
            dirPattern = "AutoCAD*";
            fileName = "acad.cuix";
        }
        else
        {
            return Array.Empty<string>();
        }

        if (!Directory.Exists(vendorRoot)) return Array.Empty<string>();
        var found = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(vendorRoot, dirPattern))
            found.AddRange(Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories));
        return found;
    }
}
```

- [ ] **Step 4: 运行确认绿**

Run: `dotnet test tests/CadAgent.Core.Tests --filter CuixLocatorTests`
Expected: PASS（4 测试）

- [ ] **Step 5: 金丝雀改用 CuixLocator（重写 `tests/CadAgent.Adapters.Zwcad.Tests/CuixHealthTests.cs`）**

```csharp
using System.IO.Compression;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// CUIX 健康金丝雀（2026-11-22 事故）：真机轮后用户 profile 的 ZWCAD.CUIX
/// 必须 zip 完整；损坏即红，指向 outputs/cuix-evidence-20261122 留证流程。
/// 定位经 CuixLocator（覆盖版本/语言变体）。
/// </summary>
[Collection("ZwcadLive")]
public class CuixHealthTests
{
    [Fact]
    public void UserProfileCuix_ZipIntact()
    {
        var files = CuixLocator.FindCuix(VendorProfiles.Zwcad);
        if (files.Count == 0) return; // 无中望 profile 的环境跳过
        foreach (var prof in files)
        {
            using var z = ZipFile.OpenRead(prof);
            Assert.True(z.Entries.Count > 0);
            foreach (var e in z.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms); // CRC 校验在读时触发
            }
        }
    }
}
```

- [ ] **Step 6: 回归**

Run: `dotnet test tests/CadAgent.Adapters.Zwcad.Tests --filter CuixHealthTests`
Expected: PASS（本机有 ZWCAD profile，实际执行 CRC 校验）

- [ ] **Step 7: 提交**

```bash
git add src/CadAgent.Core/Cad/CuixLocator.cs tests/CadAgent.Core.Tests/CuixLocatorTests.cs tests/CadAgent.Adapters.Zwcad.Tests/CuixHealthTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: CuixLocator vendor-parameterized CUIX discovery + canary refactor"
```

---

### Task 3: CadAgent.Adapters.Acad 项目 + AcadRuntime + 单元测试

**Files:**
- Create: `src/CadAgent.Adapters.Acad/CadAgent.Adapters.Acad.csproj`
- Create: `src/CadAgent.Adapters.Acad/AcadRuntime.cs`
- Create: `tests/CadAgent.Adapters.Acad.Tests/CadAgent.Adapters.Acad.Tests.csproj`
- Create: `tests/CadAgent.Adapters.Acad.Tests/xunit.runner.json`
- Create: `tests/CadAgent.Adapters.Acad.Tests/AcadRuntimeTests.cs`
- Modify: `CadAgent.sln`（dotnet sln add）

**Interfaces:**
- Consumes: `ICadRuntime`/`RuntimeInfo`/`BlockPrimitive`/`BlockLine`/`BlockCircle`/`BlockText`/`CadRuntimeException`（Core.Cad 已有）；`VendorProfiles.Acad`/`VendorGuard`（Task 1）
- Produces: `public sealed class AcadRuntime : ICadRuntime`（构造无参）；internal static seam `Func<string, Type?> ProgIdResolver`（默认 `Type.GetTypeFromProgID`）、`Func<Type, object?> CreateComInstance`（默认 `t => Activator.CreateInstance(t)`）

- [ ] **Step 1: 写失败测试 `tests/CadAgent.Adapters.Acad.Tests/AcadRuntimeTests.cs`**

```csharp
using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AcadRuntime 单元测试（无 COM）：ProgIdResolver/CreateComInstance seam 注入。
/// 本机事实：AutoCAD 孤儿 ProgID 注册（CLSID 无 LocalServer32）→ 激活必败 →
/// GetInfo 必须优雅返回 Available=false（不得抛挂 CLI）。
/// </summary>
public class AcadRuntimeTests : IDisposable
{
    private readonly Func<string, Type?> _originalResolver = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _originalCreate = AcadRuntime.CreateComInstance;
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;

    public AcadRuntimeTests()
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.ProgIdResolver = _originalResolver;
        AcadRuntime.CreateComInstance = _originalCreate;
        VendorGuard.ProcessNames = _originalNames;
    }

    [Fact]
    public void GetInfo_ProgIdMissing_ReturnsUnavailable()
    {
        AcadRuntime.ProgIdResolver = _ => null;
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.Equal("AutoCAD", info.Vendor);
        Assert.False(info.Available);
        Assert.Equal("", info.Version);
    }

    [Fact]
    public void GetInfo_ActivationThrows_ReturnsUnavailable()
    {
        // 孤儿注册场景：ProgID 在但 COM 服务器不存在
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => throw new COMException("没有注册类 (0x80040154)");
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.False(info.Available);
    }

    [Fact]
    public void SaveAs_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.SaveAs("x.dwg"));
        Assert.Contains("无打开的图纸", ex.Message);
    }

    [Fact]
    public void EntityCount_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        Assert.Throws<CadRuntimeException>(() => rt.EntityCount());
    }

    [Fact]
    public void EnsureBlock_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        Assert.Throws<CadRuntimeException>(() => rt.EnsureBlock("B", Array.Empty<BlockPrimitive>()));
    }

    [Fact]
    public void Dispose_WaitsForAcadProcessExit()
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { "acad" } : Array.Empty<string>();
        var originalExit = VendorGuard.ExitWait;
        VendorGuard.ExitWait = TimeSpan.FromSeconds(5);
        try
        {
            using (var rt = new AcadRuntime()) { }
            Assert.True(calls >= 3);
        }
        finally { VendorGuard.ExitWait = originalExit; }
    }
}
```

- [ ] **Step 2: 创建项目骨架并确认编译失败**

`src/CadAgent.Adapters.Acad/CadAgent.Adapters.Acad.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\CadAgent.Core\CadAgent.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="CadAgent.Adapters.Acad.Tests" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

`tests/CadAgent.Adapters.Acad.Tests/CadAgent.Adapters.Acad.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
    <PackageReference Include="Xunit.SkippableFact" Version="1.4.13" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\CadAgent.Adapters.Acad\CadAgent.Adapters.Acad.csproj" />
    <ProjectReference Include="..\..\src\CadAgent.Data\CadAgent.Data.csproj" />
    <ProjectReference Include="..\CadAgent.Data.Tests\CadAgent.Data.Tests.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`tests/CadAgent.Adapters.Acad.Tests/xunit.runner.json`：

```json
{
  "parallelizeTestCollections": false
}
```

```bash
dotnet sln CadAgent.sln add src/CadAgent.Adapters.Acad/CadAgent.Adapters.Acad.csproj tests/CadAgent.Adapters.Acad.Tests/CadAgent.Adapters.Acad.Tests.csproj
dotnet test tests/CadAgent.Adapters.Acad.Tests
```

Expected: 编译失败 CS0246（AcadRuntime 不存在）

- [ ] **Step 3: 实现 `src/CadAgent.Adapters.Acad/AcadRuntime.cs`**

```csharp
using System.Collections.Concurrent;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// AutoCAD COM 适配器（spec §3/§11）：dynamic COM（ProgID AutoCAD.Application
/// 版本无关→CurVer；不编译期引用厂商 interop DLL）；所有 COM 调用经专用 STA 线程
/// 任务队列串行化；崩溃→CadRuntimeException，Restart 重建。
/// 状态：CompatibleUnverified——开发机无 AutoCAD（孤儿注册），真机验证按
/// docs/verification/autocad-adapter-protocol.md 在工作环境执行。
/// 与 ZwcadRuntime 同源结构；差异经 VendorProfiles.Acad 参数化
/// （Blocks.Add 文档序 Name 先，待真机探针核定）。
/// </summary>
public sealed class AcadRuntime : ICadRuntime
{
    private static readonly VendorProfile Profile = VendorProfiles.Acad;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _staThread;
    private dynamic? _app;
    private dynamic? _doc;
    private bool _disposed;

    internal static Func<string, Type?> ProgIdResolver = Type.GetTypeFromProgID;
    internal static Func<Type, object?> CreateComInstance = t => Activator.CreateInstance(t);

    public AcadRuntime()
    {
        _staThread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
    }

    private T Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void Run(Action a) => Run<object?>(() => { a(); return null; });

    private dynamic EnsureApp()
    {
        if (_app == null)
        {
            VendorGuard.EnsureNoForeignInstance(Profile);
            var type = ProgIdResolver(Profile.ProgId)
                ?? throw new CadRuntimeException($"AutoCAD COM 未注册: {Profile.ProgId}");
            try
            {
                _app = CreateComInstance(type)
                    ?? throw new CadRuntimeException("AutoCAD COM 实例创建失败");
            }
            catch (Exception ex) when (ex is not CadRuntimeException)
            {
                throw new CadRuntimeException(
                    $"AutoCAD COM 激活失败（ProgID 可能为孤儿注册，无 LocalServer32）: {ex.Message}", ex);
            }
            _app.Visible = false;
        }
        return _app;
    }

    // 孤儿注册/激活失败/守卫超时均优雅降级 Available=false（CLI 据此走 DXF 降级+警示）。
    // 守卫超时（已有实例运行）也报不可用→降级，安全侧选择；真机协议注记此语义。
    public RuntimeInfo GetInfo() => Run(() =>
    {
        if (ProgIdResolver(Profile.ProgId) == null)
            return new RuntimeInfo("AutoCAD", "", false);
        try
        {
            string version = EnsureApp().Version;
            return new RuntimeInfo("AutoCAD", version, true);
        }
        catch
        {
            return new RuntimeInfo("AutoCAD", "", false);
        }
    });

    public void Open(string drawingPath) => Run(() =>
    {
        try { _doc = EnsureApp().Documents.Open(drawingPath); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"打开图纸失败: {ex.Message}", ex); }
    });

    public void SaveAs(string outputPath) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        try { _doc.SaveAs(outputPath); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"SaveAs 失败: {ex.Message}", ex); }
    });

    public void Close(bool save) => Run(() =>
    {
        if (_doc != null) { _doc.Close(save); _doc = null; }
    });

    public void Restart() => Run(() =>
    {
        // 先关文档再 Quit：带开文档 Quit 会被判异常终止→下次启动弹 CUIX 修复对话框（根因 a）
        try { _doc?.Close(false); } catch { /* 崩溃后 Close 可能失败，忽略 */ }
        _doc = null;
        try { _app?.Quit(); } catch { /* 崩溃后 Quit 可能失败，忽略 */ }
        _app = null;
    });

    private dynamic ModelSpace()
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        return _doc.ModelSpace;
    }

    private static double[] Pt(double x, double y) => new[] { x, y, 0.0 };

    private void EnsureLayer(string layer)
    {
        try { dynamic l = _doc.Layers.Item(layer); if (l != null) return; }
        catch { /* 不存在→创建 */ }
        _doc.Layers.Add(layer);
    }

    private void SetLayer(dynamic e, string? layer)
    {
        if (layer == null) return;
        EnsureLayer(layer);
        e.Layer = layer;
    }

    public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().AddLine(Pt(x1, y1), Pt(x2, y2));
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddLine 失败: {ex.Message}", ex); }
    });

    public void AddText(double x, double y, string text, double height, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().AddText(text, Pt(x, y), height);
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddText 失败: {ex.Message}", ex); }
    });

    public void AddCircle(double x, double y, double radius, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().AddCircle(Pt(x, y), radius);
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddCircle 失败: {ex.Message}", ex); }
    });

    public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().InsertBlock(Pt(x, y), blockName, scale, scale, scale, rotation);
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddInsert 失败: {ex.Message}", ex); }
    });

    public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        try
        {
            dynamic blocks = _doc.Blocks;
            bool exists = false;
            try { dynamic b0 = blocks.Item(blockName); exists = b0 != null; }
            catch { exists = false; }
            if (exists) return;
            // AutoCAD 文档序=(Name, origin)；与 ZWCAD 实测序相反。
            // 真机探针若核定相反，翻 VendorProfiles.Acad.BlocksAddNameFirst 即可。
            dynamic block = Profile.BlocksAddNameFirst
                ? blocks.Add(blockName, Pt(0, 0))
                : blocks.Add(Pt(0, 0), blockName);
            foreach (var p in primitives)
            {
                switch (p)
                {
                    case BlockLine l: block.AddLine(Pt(l.X1, l.Y1), Pt(l.X2, l.Y2)); break;
                    case BlockCircle c: block.AddCircle(Pt(c.X, c.Y), c.Radius); break;
                    case BlockText t: block.AddText(t.Value, Pt(t.X, t.Y), t.Height); break;
                    default: throw new CadRuntimeException($"不支持的块基元: {p.GetType().Name}");
                }
            }
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EnsureBlock 失败: {ex.Message}", ex); }
    });

    public int EntityCount() => Run(() =>
    {
        try { return (int)ModelSpace().Count; }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EntityCount 失败: {ex.Message}", ex); }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Run(() =>
            {
                try { _doc?.Close(false); } catch { /* 退出时忽略 */ }
                _doc = null;
                try { _app?.Quit(); } catch { /* 退出时忽略 */ }
                _app = null;
            });
        }
        catch { /* 队列已关闭等 */ }
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(10));
        // Quit 返回≠进程退出：CUIX 回写发生在退出尾段（PR#17 机制，厂商无关）。
        VendorGuard.WaitForProcessExit(Profile, VendorGuard.ExitWait);
    }
}
```

- [ ] **Step 4: 运行确认绿**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests`
Expected: PASS（6 单元测试，无 COM 激活）

- [ ] **Step 5: ZWCAD 回归（确认 Task 1 重构无回归）**

Run: `dotnet test tests/CadAgent.Adapters.Zwcad.Tests`
Expected: PASS（含真机）

- [ ] **Step 6: 提交**

```bash
git add src/CadAgent.Adapters.Acad tests/CadAgent.Adapters.Acad.Tests CadAgent.sln
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: AcadRuntime COM adapter (CompatibleUnverified, seam-tested)"
```

---

### Task 4: 条件真机测试 + 工作环境验证协议

**Files:**
- Create: `tests/CadAgent.Adapters.Acad.Tests/AcadProbe.cs`
- Create: `tests/CadAgent.Adapters.Acad.Tests/AcadLiveTests.cs`
- Create: `docs/verification/autocad-adapter-protocol.md`

**Interfaces:**
- Consumes: `AcadRuntime`（Task 3）、`VendorGuard`/`VendorProfiles`/`CuixLocator`（Task 1/2）、`DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine)`（CadAgent.Data.Tests 已有）、ACadSharp `DwgReader`（经 Data 项目传递引用）
- Produces: `internal static class AcadProbe { public static bool Available { get; } }`（Lazy 缓存探针）；`[Trait("Category","RequiresAutoCAD")]` 测试类

- [ ] **Step 1: 写探针 `tests/CadAgent.Adapters.Acad.Tests/AcadProbe.cs`**

```csharp
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 真机可用性探针（Lazy 缓存，整套测试只探一次）。
/// 本机孤儿注册：ProgID 在但激活抛 COMException → false → 真机测试 SkippableFact 跳过。
/// 真机环境：激活成功→立即 Quit+等待进程退出（不留并发实例，CUIX 竞态守卫）。
/// </summary>
internal static class AcadProbe
{
    private static readonly Lazy<bool> _available = new(() =>
    {
        var type = Type.GetTypeFromProgID(VendorProfiles.Acad.ProgId);
        if (type == null) return false;
        try
        {
            dynamic app = Activator.CreateInstance(type)!;
            app.Visible = false;
            app.Quit();
            VendorGuard.WaitForProcessExit(VendorProfiles.Acad, TimeSpan.FromSeconds(20));
            return true;
        }
        catch
        {
            return false;
        }
    });

    public static bool Available => _available.Value;
}
```

- [ ] **Step 2: 写真机测试 `tests/CadAgent.Adapters.Acad.Tests/AcadLiveTests.cs`**

```csharp
using System.IO.Compression;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 条件真机测试（spec §11：Verified 须真机证据）。
/// 无 AutoCAD COM 服务器→SkippableFact 显式跳过（不是静默绿）。
/// 工作环境执行协议：docs/verification/autocad-adapter-protocol.md。
/// </summary>
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadLiveTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"acadops-{Guid.NewGuid():N}");
    private readonly string _dxf;
    private readonly string _dwg;

    public AcadLiveTests()
    {
        Directory.CreateDirectory(_project);
        _dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _dwg = Path.Combine(_project, "ops.dwg");
    }

    public void Dispose()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(_project, true);
                File.Delete(_dxf);
                return;
            }
            catch (IOException) { Thread.Sleep(500); }
        }
    }

    [SkippableFact]
    public void GetInfo_LiveVendorSession_Available()
    {
        Skip.If(!AcadProbe.Available, "本机无 AutoCAD COM 服务器（孤儿注册），跳过真机测试");
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.True(info.Available);
        Assert.Equal("AutoCAD", info.Vendor);
        Assert.False(string.IsNullOrEmpty(info.Version));
    }

    [SkippableFact]
    public void DrawingOps_LiveVendorSession_ReadBack()
    {
        Skip.If(!AcadProbe.Available, "本机无 AutoCAD COM 服务器（孤儿注册），跳过真机测试");
        using var rt = new AcadRuntime();
        rt.Open(_dxf);
        Assert.Equal(1, rt.EntityCount());
        rt.AddLine(0, 0, 100, 0);
        rt.AddText(50, 50, "生成式标注", 5);
        rt.AddCircle(20, 20, 7.5);
        Assert.Equal(4, rt.EntityCount());
        // EnsureBlock 走 BlocksAddNameFirst=true（AutoCAD 文档序）。
        // 若此处 COM 报错→真机核定签名序相反→翻 VendorProfiles.Acad 标志并重跑（协议文档步骤 4）。
        rt.EnsureBlock("SYM", new BlockPrimitive[]
        {
            new BlockCircle(0, 0, 1.5),
            new BlockLine(-2.5, 0, 2.5, 0),
            new BlockLine(0, -2.5, 0, 2.5),
        });
        rt.EnsureBlock("SYM", Array.Empty<BlockPrimitive>()); // 幂等
        rt.AddInsert("SYM", 60, 60);
        Assert.Equal(5, rt.EntityCount());
        rt.SaveAs(_dwg);
        rt.Close(false);

        using var fs = File.OpenRead(_dwg);
        var doc = new DwgReader(fs).Read();
        Assert.Equal(5, doc.Entities.Count());
        Assert.Contains(doc.Entities.OfType<TextEntity>(), t => t.Value == "生成式标注");
        Assert.Contains(doc.Entities.OfType<Insert>(), i => i.Block?.Name == "SYM");
        Assert.Contains(doc.Entities.OfType<Circle>(), c => Math.Abs(c.Radius - 7.5) < 1e-6);
    }

    [SkippableFact]
    public void OpenSaveAs_RoundTrip_EntityCountPreserved()
    {
        Skip.If(!AcadProbe.Available, "本机无 AutoCAD COM 服务器（孤儿注册），跳过真机测试");
        using var rt = new AcadRuntime();
        rt.Open(_dxf);
        rt.SaveAs(_dwg);
        rt.Close(false);
        using var fs = File.OpenRead(_dwg);
        var doc = new DwgReader(fs).Read();
        Assert.Equal(1, doc.Entities.Count());
    }

    [SkippableFact]
    public void UserProfileCuix_ZipIntact()
    {
        var files = CuixLocator.FindCuix(VendorProfiles.Acad);
        Skip.If(files.Count == 0, "本机无 AutoCAD 用户 profile CUIX，跳过金丝雀");
        foreach (var prof in files)
        {
            using var z = ZipFile.OpenRead(prof);
            Assert.True(z.Entries.Count > 0);
            foreach (var e in z.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms); // CRC 校验在读时触发
            }
        }
    }
}
```

- [ ] **Step 3: 运行确认跳过语义诚实**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter Category=RequiresAutoCAD`
Expected: 4 个 Skipped（本机孤儿注册；输出含跳过原因），0 Failed，0 静默 Pass

- [ ] **Step 4: 写协议文档 `docs/verification/autocad-adapter-protocol.md`**

```markdown
# AutoCAD 适配器真机验证协议（工作环境执行）

> 状态：开发机无 AutoCAD（孤儿 ProgID 注册，CLSID {363E5B47-885D-44C3-89EB-A2AB2129B57E}
> 无 LocalServer32）。AcadRuntime 已实现但 **CompatibleUnverified**。
> 本协议在工作环境（实装 AutoCAD 2024-2026）执行，产出 Verified 升级证据。

## 前置约束（CUIX 竞态根因 b，PR#17 事故教训）

1. 验证期间**禁止**并行运行任何 AutoCAD 自动化或人工会话——并发实例共享用户
   profile 会竞态损坏 acad.cuix，下次启动弹模态修复框挂起自动化。
2. 测试已串行化（xunit.runner.json + [Collection("AcadLive")]）；不得改回并行。
3. 验证前备份用户 profile CUIX（%APPDATA%\Autodesk\AutoCAD *\*\*\Support\acad.cuix），
   记录 SHA256；验证后复算比对（金丝雀测试自动做 zip CRC 校验）。

## 步骤

1. 确认安装：`AutoCAD.Application` ProgID 的 CLSID 必须有 LocalServer32
   （PowerShell：`Get-ChildItem 'Registry::HKEY_CLASSES_ROOT\CLSID\{...}'`）。
2. 运行条件真机测试：
   `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter Category=RequiresAutoCAD`
   预期：4 测试全绿（不再跳过）。记录 AutoCAD 版本号（GetInfo 输出）。
3. 运行全量回归：`dotnet test CadAgent.sln`（含 ZWCAD 真机，若工作环境也有中望）。
4. **Blocks.Add 签名序探针核定**：DrawingOps_LiveVendorSession_ReadBack 的
   EnsureBlock 若 COM 报错（参数序不符），翻 `VendorProfiles.Acad.BlocksAddNameFirst`
   为 false 重跑；核定结果（文档序成立/相反）写入矩阵 testCoverage 与 spec §12。
   ZWCAD 前车之鉴：实测序=(origin, Name) 与 AutoCAD 文档序相反——不赌文档，真机核定。
5. SaveAs 格式确认：读回闸口已断言 DWG 可读；另用 AutoCAD 人工打开 ops.dwg
   确认无修复提示、实体完整（保真锚=厂商运行时，spec §3）。
6. CUIX 金丝雀：UserProfileCuix_ZipIntact 绿 + 备份哈希比对一致。
7. 证据归档：测试输出、版本号、探针核定结论、CUIX 哈希前后比对 →
   `outputs/autocad-verify-<date>/`（含 MANIFEST.txt）。

## 矩阵升级（须全部满足）

- 步骤 2-6 全绿且证据归档。
- `src/CadAgent.Cli/vendor-support.json` AutoCAD 条目：
  status → "Verified"，verifiedAt → 执行日期，versionRange → 实测版本，
  testCoverage → 实测覆盖描述（含探针核定结论）。
- spec §12 追加验证注记。
- 未实测版本仍保持 CompatibleUnverified（打开时警示，决策权归人）。

## 已知语义注记

- GetInfo 在守卫超时（已有 acad 进程）时报 Available=false → CLI 降级 DXF+警示。
  这是安全侧选择：忙≠不可用，但降级不丢数据；如需区分，后续切片细化 RuntimeInfo。
- accoreconsole（真无头）暴露面更小（无 CUIX 加载），本切片 deferred；
  COM 路径与 ZWCAD 同构，守卫机制已参数化可直接复用。
```

- [ ] **Step 5: 提交**

```bash
git add tests/CadAgent.Adapters.Acad.Tests/AcadProbe.cs tests/CadAgent.Adapters.Acad.Tests/AcadLiveTests.cs docs/verification/autocad-adapter-protocol.md
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "test: conditional AutoCAD live tests (SkippableFact) + work-env verification protocol"
```

---

### Task 5: CLI 运行时选择 + 厂商矩阵更新

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.Export.cs:14-28,51-70`（工厂改名+选择逻辑）
- Modify: `src/CadAgent.Cli/CommandRouter.Live.cs:44`（调用点）
- Modify: `src/CadAgent.Cli/CadAgent.Cli.csproj`（加 Acad 项目引用）
- Modify: `src/CadAgent.Cli/vendor-support.json`（AutoCAD 条目）
- Create: `tests/CadAgent.Cli.Tests/RuntimeSelectionTests.cs`

**Interfaces:**
- Consumes: `AcadRuntime`（Task 3）、`ZwcadRuntime`（已有）
- Produces: `internal static Func<string?> CommandRouter.RuntimeSelector`（seam，默认读 `CADAGENT_RUNTIME` 环境变量）；`internal static string CommandRouter.ResolveRuntimeName()`（规范化+校验，未知值抛 CadRuntimeException）；`internal static ICadRuntime CommandRouter.CreateRuntimeInstance(string name)`（纯工厂，不做可用性检查）

- [ ] **Step 1: 写失败测试 `tests/CadAgent.Cli.Tests/RuntimeSelectionTests.cs`**

```csharp
using CadAgent.Core.Cad;

namespace CadAgent.Cli.Tests;

/// <summary>
/// CLI 运行时选择（CADAGENT_RUNTIME）：纯工厂+名称校验单元测试，不触 COM。
/// 等价类：默认/显式 zwcad/acad/大小写空白/未知值硬错误。
/// </summary>
public class RuntimeSelectionTests : IDisposable
{
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;

    public void Dispose() => CommandRouter.RuntimeSelector = _originalSelector;

    [Fact]
    public void ResolveRuntimeName_Default_Zwcad()
    {
        CommandRouter.RuntimeSelector = () => null;
        Assert.Equal("zwcad", CommandRouter.ResolveRuntimeName());
    }

    [Theory]
    [InlineData("acad", "acad")]
    [InlineData("ACAD", "acad")]
    [InlineData(" Acad ", "acad")]
    [InlineData("zwcad", "zwcad")]
    [InlineData("ZWCAD", "zwcad")]
    public void ResolveRuntimeName_Normalizes(string input, string expected)
    {
        CommandRouter.RuntimeSelector = () => input;
        Assert.Equal(expected, CommandRouter.ResolveRuntimeName());
    }

    [Fact]
    public void ResolveRuntimeName_Unknown_ThrowsWithUsage()
    {
        CommandRouter.RuntimeSelector = () => "solidworks";
        var ex = Assert.Throws<CadRuntimeException>(() => CommandRouter.ResolveRuntimeName());
        Assert.Contains("CADAGENT_RUNTIME", ex.Message);
        Assert.Contains("zwcad", ex.Message);
        Assert.Contains("acad", ex.Message);
    }

    [Fact]
    public void CreateRuntimeInstance_Zwcad_ReturnsZwcadRuntime()
    {
        using var rt = CommandRouter.CreateRuntimeInstance("zwcad");
        Assert.IsType<CadAgent.Adapters.Zwcad.ZwcadRuntime>(rt);
    }

    [Fact]
    public void CreateRuntimeInstance_Acad_ReturnsAcadRuntime()
    {
        using var rt = CommandRouter.CreateRuntimeInstance("acad");
        Assert.IsType<CadAgent.Adapters.Acad.AcadRuntime>(rt);
    }
}
```

注意：`CreateRuntimeInstance` 构造 ZwcadRuntime/AcadRuntime 仅启动 STA 队列线程，
不激活 COM（激活延迟到 EnsureApp）；Dispose 走守卫等待——测试进程无 ZWCAD/acad
进程时立即返回。若测试机有人工 ZWCAD 会话，Dispose 会等 15s（PR#17 已知 minor，
不阻塞）；真机测试期间禁人工会话的约束同样适用。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter RuntimeSelectionTests`
Expected: 编译失败 CS0117（RuntimeSelector/ResolveRuntimeName/CreateRuntimeInstance 不存在）

- [ ] **Step 3: 实现选择逻辑（`src/CadAgent.Cli/CommandRouter.Export.cs`）**

替换 14 行注释与 51-70 行 `CreateZwcadIfAvailable` 为：

```csharp
    /// <summary>测试注入运行时工厂；null→生产运行时（CADAGENT_RUNTIME 选择，Available 检查）。</summary>
    internal static Func<ICadRuntime?>? RuntimeFactory;

    /// <summary>运行时选择 seam（默认读 CADAGENT_RUNTIME 环境变量；null/空→zwcad）。</summary>
    internal static Func<string?> RuntimeSelector =
        () => Environment.GetEnvironmentVariable("CADAGENT_RUNTIME");

    internal static string ResolveRuntimeName()
    {
        var name = (RuntimeSelector() ?? "zwcad").Trim().ToLowerInvariant();
        if (name is not ("zwcad" or "acad"))
            throw new CadRuntimeException($"未知 CADAGENT_RUNTIME: {name}（支持: zwcad, acad）");
        return name;
    }

    /// <summary>纯工厂（不做可用性检查；构造不激活 COM）。</summary>
    internal static ICadRuntime CreateRuntimeInstance(string name) => name switch
    {
        "zwcad" => new CadAgent.Adapters.Zwcad.ZwcadRuntime(),
        "acad" => new CadAgent.Adapters.Acad.AcadRuntime(),
        _ => throw new CadRuntimeException($"未知运行时: {name}")
    };

    private static ICadRuntime? CreateRuntimeIfAvailable(TextWriter stderr)
    {
        var name = ResolveRuntimeName(); // 配置错误硬失败（用户显式选择的厂商不得静默降级）
        try
        {
            var rt = CreateRuntimeInstance(name);
            var info = rt.GetInfo();
            if (!info.Available)
            {
                stderr.WriteLine($"警示: 无可用厂商运行时（{name}），降级 DXF 交付（保真度未验证）");
                rt.Dispose();
                return null;
            }
            return rt;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"警示: 厂商运行时初始化失败（{ex.Message}），降级 DXF 交付");
            return null;
        }
    }
```

调用点更新（两处）：
- `CommandRouter.Export.cs:28`：`: CreateZwcadIfAvailable(stderr);` → `: CreateRuntimeIfAvailable(stderr);`
- `CommandRouter.Live.cs:44`：`RuntimeFactory != null ? RuntimeFactory() : CreateZwcadIfAvailable(stderr)` → `RuntimeFactory != null ? RuntimeFactory() : CreateRuntimeIfAvailable(stderr)`

- [ ] **Step 4: Cli csproj 加 Acad 引用**

在 `src/CadAgent.Cli/CadAgent.Cli.csproj` 的 Zwcad ProjectReference 旁加：

```xml
    <ProjectReference Include="..\CadAgent.Adapters.Acad\CadAgent.Adapters.Acad.csproj" />
```

- [ ] **Step 5: 更新 `src/CadAgent.Cli/vendor-support.json` AutoCAD 条目**

```json
    {
      "vendor": "AutoCAD",
      "versionRange": "2024-2026",
      "adapterVersion": "1.0",
      "status": "CompatibleUnverified",
      "verifiedAt": null,
      "testCoverage": "AcadRuntime COM 全接口实现 + seam 单元测试 + 条件真机测试（RequiresAutoCAD，无 AutoCAD 环境显式跳过）；Blocks.Add 按 Autodesk 文档序待真机探针核定；真机协议 docs/verification/autocad-adapter-protocol.md"
    }
```

- [ ] **Step 6: 运行确认绿 + 回归**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter RuntimeSelectionTests`
Expected: PASS（8 测试）
Run: `dotnet test tests/CadAgent.Cli.Tests`
Expected: PASS（全量；既有 ExportCommandTests/LiveReplayCliTests 经 RuntimeFactory 注入不受影响）

- [ ] **Step 7: 提交**

```bash
git add src/CadAgent.Cli/CommandRouter.Export.cs src/CadAgent.Cli/CommandRouter.Live.cs src/CadAgent.Cli/CadAgent.Cli.csproj src/CadAgent.Cli/vendor-support.json tests/CadAgent.Cli.Tests/RuntimeSelectionTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: CLI runtime selection (CADAGENT_RUNTIME) + AutoCAD matrix adapterVersion 1.0"
```

---

### Task 6: spec §12 注记 + 全量门禁 + PR

**Files:**
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md`（§12 追加）

**Interfaces:**
- Consumes: 全部前序任务交付
- Produces: 全量绿证据 + PR（base main）

- [ ] **Step 1: spec §12 追加注记**

在 §12 末尾追加（找到该节最后一条承接约束后插入）：

```markdown
- **AutoCAD 适配器切片交付（2026-11-22，feat/cad-agent-autocad-adapter）**：AcadRuntime 全接口 dynamic COM 实现（CompatibleUnverified——开发机孤儿 ProgID 注册无 LocalServer32，真机验证 deferred 至工作环境，协议 docs/verification/autocad-adapter-protocol.md）。PR#17 CUIX 守卫机制参数化为 Core.Cad.VendorGuard+VendorProfile（厂商无关，ZwcadRuntime 行为保持重构）；CuixLocator 双厂商 CUIX 定位。CLI CADAGENT_RUNTIME 选择运行时（默认 zwcad；未知值硬错误不静默降级）。Blocks.Add 签名序经 VendorProfile.BlocksAddNameFirst 参数化（AutoCAD 文档序 Name 先，待真机探针核定——ZWCAD 实测序相反的前车之鉴）。GetInfo 守卫超时→Available=false→DXF 降级（安全侧，忙≠不可用语义细化 deferred）。accoreconsole 真无头路径 deferred（暴露面更小，守卫机制可复用）。矩阵 Verified 升级须工作环境真机证据（spec §11 硬约束）。
```

- [ ] **Step 2: 全量门禁（单命令全 sln，避免 MSB1008）**

Run: `dotnet test CadAgent.sln`
Expected: 全绿。基线 829 + 新增（VendorGuardTests 14 + CuixLocatorTests 4 + AcadRuntimeTests 6 + RuntimeSelectionTests 8 = 32）≈ 861 通过；AutoCAD 真机 4 显式 Skipped；ZWCAD 真机 13 通过（真机期间禁人工会话）。记录精确数字。

- [ ] **Step 3: 连绿两次（真机稳定性，PR#17 教训）**

Run: `dotnet test CadAgent.sln`（第二次）
Expected: 与第一次一致全绿。

- [ ] **Step 4: 提交 spec 注记**

```bash
git add docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "docs: spec §12 AutoCAD adapter slice delivery notes"
```

- [ ] **Step 5: 推送 + 建 PR（用户批准后执行；gh GraphQL 不稳→REST 兜底）**

```bash
git push -u origin feat/cad-agent-autocad-adapter
gh pr create --base main --title "feat: AutoCAD COM adapter slice (CompatibleUnverified) + vendor guard parameterization" --body "..."
```

PR body 要点：切片范围/本机无 AutoCAD 事实与条件跳过/矩阵保持 CompatibleUnverified/真机协议路径/全量测试数字（含两次连绿）/PR#17 机制泛化说明。推送遇代理问题按记忆 auto-resolve-git-proxy-issues 处理（残留 http.proxy 用 `-c http.proxy=` 清空；端口 7892 探测）。

---

## Self-Review 记录

- **Spec 覆盖**：§3 适配器一等公民（Task 3）+ Core 厂商无关（Task 1/2 仅 BCL）；§11 矩阵 Verified 须真机证据（Task 4 协议 + Task 5 保持 CompatibleUnverified）；§12 AutoCAD 参数化承接（Task 1 VendorProfile/VendorGuard + Task 6 注记）。accoreconsole deferred 明示。
- **占位符扫描**：无 TBD/TODO；所有代码步含完整代码；PR body 以要点清单给出（执行时按实测数字填充）。
- **类型一致性**：VendorProfile 四参记录贯穿 Task 1/3/4；VendorGuard.EnsureNoForeignInstance(profile)/WaitForProcessExit(profile, timeout) 签名一致；CuixLocator.FindCuix(profile) 一致；AcadRuntime.ProgIdResolver/CreateComInstance seam 在 Task 3 定义 Task 4 探针不依赖（探针用真实激活，有意为之）；ResolveRuntimeName/CreateRuntimeInstance/RuntimeSelector Task 5 内自洽。
- **已知取舍**：GetInfo 守卫超时→不可用（安全侧降级，协议注记）；ForeignInstanceGuardTests 缩减为 Dispose 集成测试（行为测试移 Core.Tests 双厂商覆盖，意图保持）；RuntimeSelectionTests 的 CreateRuntimeInstance 构造真适配器但不激活 COM（Dispose 守卫等待在无人工会话时立即返回）。
---

## 执行记录（2026-11-22，子代理驱动）

- 分支 feat/cad-agent-autocad-adapter，9 提交（d2207d0..5d7f868），6 任务全完成 + 最终审查修复波。
- 门禁：dotnet test CadAgent.sln 连绿两次 **857 通过 / 0 失败 / 4 跳过**（14 项目；ZWCAD 真机 7/7；AutoCAD 真机 4 显式 Skipped——本机孤儿注册）。
- 任务内修复环：Task 3 Close 包装（Acad）、Task 4 探针收尾+四态 SkipReason；最终审查修复波：Zwcad Close 同款包装（Critical：ExportService 重试+审计旁路）、硬错误边界集成测试（变异核查红→绿）、AcadProbe 激活前守卫。
- **Deferred minors（最终审查 triage，post-merge）**：CuixLocator IgnoreInaccessible（优先级最高，金丝雀误红污染事故信号）；厂商目录布局数据并入 VendorProfile；Zwcad ProgId 归一 profile；VendorGuard 句柄 Dispose；EOF 换行×4；CS8602×2；Dispose 后调用异常类型；EnsureApp 部分失败缓存；xUnit2013；AcadLiveTests.Dispose 捕 UnauthorizedAccessException；协议步骤1 CLSID 解析命令（**工作环境验证前必补**）；协议步骤2 记年份+release 号（Version 返回 release 号与矩阵年份 versionRange 匹配前瞻）；Export.cs 类头注释；CreateRuntimeIfAvailable catch 释放 rt。Dismiss：ZipArchive Stored CRC（格式事实）；--live 配置错误无审计（CLI 全局惯例）。
- 既有 flaky 观察：Data.Tests Elevation_EmptyMode_StillChecksBoth 瞬态失败（切片未触；复发时 Data.Tests 加 runner.json 串行二分+断言诊断消息）。
- 真机验证 deferred：docs/verification/autocad-adapter-protocol.md（工作环境执行，Verified 升级须真机证据）。
