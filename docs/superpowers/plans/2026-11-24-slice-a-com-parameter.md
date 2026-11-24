# 切片 A：COM 原生读参 + 逐实体精准松绑 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 原生 TCH 图纸经 COM 读参补全 TDb 实体原生参数落 overlay 独立表 `NativeEntities`，并对读参完整的实体在 live 通道精准开放修改（不升整图 Green）。

**Architecture:** 方案一——扩展 AcadRuntime（新增 profile 感知 /p 启动+ROT 附着分支 + ReadNativeParams 读参编排），ICadRuntime 加只读参数读方法，Data 层 Seeder/GateRefinement 消费抽象结果。复用既有 STA 队列 + RetryBusy 忙窗重试框架。census 主闸不动，松绑为逐实体补充判据。

**Tech Stack:** C#/.NET 8，dynamic COM（IDispatch），P/Invoke（ole32 CLSIDFromProgID / oleaut32 GetActiveObject），ACadSharp 3.8.35（仅文件级对照），xUnit + SkippableFact（条件真机）。

**Spec:** `docs/superpowers/specs/2026-11-24-slice-a-com-parameter-design.md`（五节用户认可，commit 0938068）

## Global Constraints

- Core 零 COM：`NativeEntityParam`/`TDbClassMap`/`ICadRuntime` 接口不 using 任何 COM/厂商库；COM 细节全在 CadAgent.Adapters.Acad。
- 忙窗重试复用 AcadRuntime 既有 `RetryBusy`/`IsBusyRejection`（0x80010001/0x8001010A + MissingMemberException 包装形态）——不得新建第二套重试逻辑。
- **空读≠不存在**：每次 COM 读经 RetryBusy 至非空；耗尽→该实体 `ReadOk=false`，计 `nativeReadFailed` 披露，不静默当"无此实体"。
- **UNKNOWNNAME（0x80020006）=属性不存在**：该属性记缺省不重试；逐属性独立判定，单属性缺不致整实体 ReadOk=false（除非 ObjectName 本身不可读）。
- census 主闸 `GateRefusesRun`（Level!=Green）不动；松绑是独立补充判据，**只在 live 通道**（文件级 `HandlerHelpers.Find` 走 `Doc.Entities`，TDb 文件级不可见）。
- **重读校验作放行前提**：本轮 live 会话重读目标句柄 ObjectName+bbox 与 overlay 逐字相等、数值参数字符串化相等、句柄运行时存在——任一不符→拒。
- 逐实体松绑**非整图 Green**；未读参/ReadOk=false/未命中实体仍禁触。
- 真机禁并行：`VendorGuard.EnsureNoForeignInstance` 前置；读参全程只读副本零写入；CUIX/源件/副本 SHA 前后一致；tasklist 前后清零；自家实例 Close+Quit 清理；绝不强杀非自家进程。
- JSON 加性：旧 overlay 无 NativeEntities 字段→反序列化 null→归一为空 List（消费端免判空）；Save 沿用原子写（tmp+Move）。
- Provenance 枚举**末尾**追加 `NativeParameter`（置信度 0.95）——JSON 字符串枚举兼容，既有成员数值序不扰动。
- git 身份仅环境变量（GIT_AUTHOR_NAME=Fake-msn 等），绝不 `git config`；commit 用 HEREDOC。
- 真实标本 skip-gated 不入库；dotnet test 单项目或全 sln（多项目参数→MSB1008）。

---

### Task 1: Core 数据模型 —— NativeEntityParam + TDbClassMap + Provenance + ICadRuntime 接口

**Files:**
- Create: `src/CadAgent.Core/Cad/NativeEntityParam.cs`
- Create: `src/CadAgent.Core/Cad/TDbClassMap.cs`
- Modify: `src/CadAgent.Core/SemanticModel.cs:6`（Provenance 枚举末尾加 NativeParameter）
- Modify: `src/CadAgent.Core/Cad/ICadRuntime.cs`（接口加 ReadNativeParams + 默认空实现）
- Test: `tests/CadAgent.Core.Tests/TDbClassMapTests.cs`

**Interfaces:**
- Consumes: `SemanticClass`（src/CadAgent.Core/SemanticModel.cs:4）。
- Produces:
  - `record NativeEntityParam(string Handle, string ObjectName, SemanticClass Class, IReadOnlyDictionary<string,string> Params, double[]? Bbox, DateTimeOffset ReadAt, bool ReadOk, string? Layer = null)`（Layer 末位可选——change-layer 读回校验需要；COM Layer 标准属性对 TDb 可读，probe 实证）
  - `static class TDbClassMap { static SemanticClass ClassFor(string objectName) }`
  - `Provenance.NativeParameter`
  - `ICadRuntime.ReadNativeParams(string? drawingPath = null) : IReadOnlyList<NativeEntityParam>`（接口默认实现返回空；path!=null=自启+自开+读完关，null=读当前已开文档）
  - `ICadRuntime.ChangeLayerByHandle(string hexHandle, string layer)` / `MoveByHandle(string hexHandle, double dx, double dy)` / `DeleteByHandle(string hexHandle)`——接口默认实现 `throw new CadRuntimeException("运行时不支持句柄寻址操作")`（Zwcad 零改动，运行时响亮拒绝）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Core.Tests;

public class TDbClassMapTests
{
    [Theory]
    [InlineData("TDbWall", SemanticClass.Wall)]
    [InlineData("TDbColumn", SemanticClass.Column)]
    [InlineData("TDbEquipment", SemanticClass.Equipment)]
    [InlineData("TDbText", SemanticClass.Annotation)]
    [InlineData("TDbDrawingName", SemanticClass.Annotation)]
    [InlineData("TDbSymbArrow", SemanticClass.Annotation)]
    [InlineData("TDbUnknownNew", SemanticClass.Unknown)]
    [InlineData("AcDbLine", SemanticClass.Unknown)]
    public void ClassFor_MapsObjectName(string objectName, SemanticClass expected)
        => Assert.Equal(expected, TDbClassMap.ClassFor(objectName));

    [Fact]
    public void Provenance_HasNativeParameter()
        => Assert.True(Enum.IsDefined(typeof(Provenance), "NativeParameter"));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Core.Tests --filter TDbClassMapTests`
Expected: 编译失败（TDbClassMap/NativeEntityParam 不存在）

- [ ] **Step 3: 实现**

`src/CadAgent.Core/Cad/NativeEntityParam.cs`：
```csharp
namespace CadAgent.Core.Cad;

/// <summary>一实体一次 COM 原生读参结果（spec 切片A §3）。Core 零 COM——纯数据。
/// Layer 末位可选（change-layer 读回校验需要；COM 标准 Layer 属性对 TDb 可读）。</summary>
public sealed record NativeEntityParam(
    string Handle,
    string ObjectName,
    SemanticClass Class,
    IReadOnlyDictionary<string, string> Params,
    double[]? Bbox,
    DateTimeOffset ReadAt,
    bool ReadOk,
    string? Layer = null);
```

`src/CadAgent.Core/Cad/TDbClassMap.cs`：
```csharp
namespace CadAgent.Core.Cad;

/// <summary>TDb C++ 类名→语义类静态映射（spec 切片A §3）。初版覆盖 probe 实证类，新类增量补。</summary>
public static class TDbClassMap
{
    public static SemanticClass ClassFor(string objectName) => objectName switch
    {
        "TDbWall" => SemanticClass.Wall,
        "TDbColumn" => SemanticClass.Column,
        "TDbEquipment" => SemanticClass.Equipment,
        "TDbText" or "TDbDrawingName" or "TDbSymbArrow" => SemanticClass.Annotation,
        _ => SemanticClass.Unknown,
    };
}
```

`SemanticModel.cs` Provenance 末尾加 `NativeParameter`；`ICadRuntime.cs` 接口加：
```csharp
    /// <summary>COM 原生读参（spec 切片A）：drawingPath!=null=自启+自开+读完关；null=读当前已开文档。
    /// 无厂商/不支持→空（默认实现）。</summary>
    IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) =>
        Array.Empty<NativeEntityParam>();

    /// <summary>句柄寻址修改（spec 切片A §4.2 native-live 通道）；不支持→响亮拒绝（默认实现）。</summary>
    void ChangeLayerByHandle(string hexHandle, string layer) =>
        throw new CadRuntimeException("运行时不支持句柄寻址操作（change-layer）");
    void MoveByHandle(string hexHandle, double dx, double dy) =>
        throw new CadRuntimeException("运行时不支持句柄寻址操作（move）");
    void DeleteByHandle(string hexHandle) =>
        throw new CadRuntimeException("运行时不支持句柄寻址操作（delete）");
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Core.Tests --filter TDbClassMapTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core tests/CadAgent.Core.Tests
git commit -m "feat(core): 切片A数据模型——NativeEntityParam+TDbClassMap+Provenance.NativeParameter+ICadRuntime.ReadNativeParams默认空"
```

---

### Task 2: SemanticOverlay.NativeEntities 独立表 + UpsertNative

**Files:**
- Modify: `src/CadAgent.Core/SemanticOverlay.cs`（加 NativeEntities 属性 + UpsertNative + null 归一）
- Test: `tests/CadAgent.Core.Tests/SemanticOverlayNativeTests.cs`

**Interfaces:**
- Consumes: `NativeEntityParam`（Task 1）、`SemanticOverlay`（src/CadAgent.Core/SemanticOverlay.cs）。
- Produces:
  - `SemanticOverlay.NativeEntities : List<NativeEntityParam>`（JSON 加性，旧文件 null→空）
  - `SemanticOverlay.UpsertNative(NativeEntityParam)`（by Handle，同句柄覆盖含 ReadAt 更新）

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Core.Tests;

public class SemanticOverlayNativeTests
{
    private static NativeEntityParam P(string handle, bool ok = true) => new(
        handle, "TDbWall", SemanticClass.Wall,
        new Dictionary<string, string> { ["LeftWidth"] = "100" },
        new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, ok);

    [Fact]
    public void NativeEntities_DefaultEmpty()
        => Assert.Empty(new SemanticOverlay().NativeEntities);

    [Fact]
    public void UpsertNative_SameHandle_Overwrites()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(P("21E00"));
        var updated = P("21E00") with { Bbox = new[] { 9.0, 9, 9, 9, 9, 9 } };
        o.UpsertNative(updated);
        Assert.Single(o.NativeEntities);
        Assert.Equal(9.0, o.NativeEntities[0].Bbox![0]);
    }

    [Fact]
    public void UpsertNative_DifferentHandle_KeepsBoth()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(P("21E00")); o.UpsertNative(P("321A"));
        Assert.Equal(2, o.NativeEntities.Count);
    }

    [Fact]
    public void Json_LegacyFileWithoutField_LoadsEmpty()
    {
        string legacy = """{"Annotations":[],"Mappings":[],"Version":1}""";
        var o = JsonSerializer.Deserialize<SemanticOverlay>(legacy,
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.NotNull(o.NativeEntities);
        Assert.Empty(o.NativeEntities);
    }

    [Fact]
    public void Json_RoundTrip_PreservesNative()
    {
        var dir = Directory.CreateTempSubdirectory("ovnative").FullName;
        try
        {
            string path = Path.Combine(dir, "semantic.json");
            var o = new SemanticOverlay(); o.UpsertNative(P("21E00")); o.Save(path);
            var loaded = SemanticOverlay.Load(path);
            Assert.Single(loaded.NativeEntities);
            Assert.Equal("21E00", loaded.NativeEntities[0].Handle);
            Assert.Equal(SemanticClass.Wall, loaded.NativeEntities[0].Class);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Core.Tests --filter SemanticOverlayNativeTests`
Expected: 编译失败（NativeEntities/UpsertNative 不存在）

- [ ] **Step 3: 实现**（SemanticOverlay.cs 加属性+方法；Load 后 null 归一）

```csharp
    // 切片A（spec §3）：COM 原生读参独立表——与 Annotations 并列，语义分类与原生参数分离。
    // JSON 加性：旧 overlay 无此字段→反序列化 null→Load 归一为空 List（消费端免判空）。
    public List<NativeEntityParam> NativeEntities { get; set; } = new();

    public void UpsertNative(NativeEntityParam p)
    {
        int i = NativeEntities.FindIndex(x => x.Handle == p.Handle);
        if (i >= 0) NativeEntities[i] = p; else NativeEntities.Add(p);
    }
```
Load 反序列化后加 `overlay.NativeEntities ??= new();`。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Core.Tests --filter SemanticOverlayNativeTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core/SemanticOverlay.cs tests/CadAgent.Core.Tests
git commit -m "feat(core): SemanticOverlay.NativeEntities独立表+UpsertNative幂等+JSON加性null归一"
```

---

### Task 3: NativeParamSeeder（读参→overlay + 同步产 Annotation）

**Files:**
- Create: `src/CadAgent.Data/Semantic/NativeParamSeeder.cs`
- Test: `tests/CadAgent.Data.Tests/NativeParamSeederTests.cs`

**Interfaces:**
- Consumes: `NativeEntityParam`（Task 1）、`SemanticOverlay.UpsertNative/UpsertAnnotation`（Task 2/既有）、`Provenance.NativeParameter`（Task 1）、`SemanticAnnotation`（src/CadAgent.Core/SemanticModel.cs:9）。
- Produces: `static class NativeParamSeeder { static void Seed(SemanticOverlay overlay, IReadOnlyList<NativeEntityParam> parms) }`

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class NativeParamSeederTests
{
    private static NativeEntityParam P(string handle, SemanticClass cls, bool ok) => new(
        handle, "TDbWall", cls, new Dictionary<string, string>(),
        new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, ok);

    [Fact]
    public void Seed_ClassifiedAndOk_ProducesAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        Assert.Single(o.NativeEntities);
        var a = Assert.Single(o.Annotations);
        Assert.Equal("21E00", a.TargetHandle);
        Assert.Equal(Provenance.NativeParameter, a.Provenance);
        Assert.Equal(0.95, a.Confidence);
        Assert.Equal(SemanticClass.Wall, a.Class);
    }

    [Fact]
    public void Seed_UnknownClass_NoAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Unknown, ok: true) });
        Assert.Single(o.NativeEntities);
        Assert.Empty(o.Annotations);
    }

    [Fact]
    public void Seed_ReadOkFalse_NoAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: false) });
        Assert.Single(o.NativeEntities);
        Assert.Empty(o.Annotations);
    }

    [Fact]
    public void Seed_Rescan_Idempotent()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        Assert.Single(o.NativeEntities);
        Assert.Single(o.Annotations);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter NativeParamSeederTests`
Expected: 编译失败（NativeParamSeeder 不存在）

- [ ] **Step 3: 实现**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

/// <summary>COM 原生读参落 overlay（spec 切片A §3）：NativeEntities 幂等 + Class!=Unknown&&ReadOk
/// 同步产 Annotation(NativeParameter 0.95)，供语义闸口复用。重扫幂等（Upsert 键=handle+provenance）。</summary>
public static class NativeParamSeeder
{
    private const double NativeConfidence = 0.95;

    public static void Seed(SemanticOverlay overlay, IReadOnlyList<NativeEntityParam> parms)
    {
        foreach (var p in parms)
        {
            overlay.UpsertNative(p);
            if (p.Class != SemanticClass.Unknown && p.ReadOk)
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    p.Handle, p.Class, Provenance.NativeParameter, NativeConfidence,
                    $"COM 原生读参: {p.ObjectName}"));
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Data.Tests --filter NativeParamSeederTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Data/Semantic/NativeParamSeeder.cs tests/CadAgent.Data.Tests
git commit -m "feat(data): NativeParamSeeder——COM读参落overlay独立表+分类且读全实体产NativeParameter标注"
```

---

### Task 4: AcadRuntime profile 感知启动（/p 启动 + ROT 附着）

**Files:**
- Create: `src/CadAgent.Adapters.Acad/AcadAttach.cs`（P/Invoke + ROT 附着 + Gate1 等待）
- Modify: `src/CadAgent.Adapters.Acad/AcadRuntime.cs`（EnsureApp 加 /p 启动分支）
- Test: `tests/CadAgent.Adapters.Acad.Tests/AcadAttachTests.cs`

**Interfaces:**
- Consumes: `VendorGuard.EnsureNoForeignInstance`（src/CadAgent.Core/Cad/VendorGuard.cs）、`VendorProfile.Acad`（src/CadAgent.Core/Cad/VendorProfile.cs:18）、AcadRuntime 既有 `RetryBusy`/`IsBusyRejection`/`StartupRetryAttempts`。
- Produces:
  - `static class AcadAttach { static object AttachOnly(string progId, int maxAttempts = 180) }`
  - `internal static Func<string,object?> AcadAttach.ActiveObjectResolver`（GetActiveObject 包装缝，测试注入）
  - AcadRuntime 内部方法 `EnsureAppTArch(string profileName)`（/p 启动+ROT 附着，与既有 COM 激活 `EnsureApp` 互斥）

- [ ] **Step 1: 写失败测试**（缝注入，无真机）

```csharp
using CadAgent.Adapters.Acad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

public class AcadAttachTests : IDisposable
{
    private readonly Func<string, object?> _origResolver = AcadAttach.ActiveObjectResolver;
    public void Dispose() => AcadAttach.ActiveObjectResolver = _origResolver;

    [Fact]
    public void Attach_WhenResolverReturnsNull_ThrowsCadRuntime()
    {
        AcadAttach.ActiveObjectResolver = _ => null;
        Assert.Throws<CadAgent.Core.Cad.CadRuntimeException>(
            () => AcadAttach.AttachOnly("AutoCAD.Application", maxAttempts: 2));
    }

    [Fact]
    public void Attach_WhenResolverSucceeds_ReturnsApp()
    {
        var fake = new object();
        AcadAttach.ActiveObjectResolver = _ => fake;
        Assert.Same(fake, AcadAttach.AttachOnly("AutoCAD.Application", maxAttempts: 2));
    }

    [Fact]
    public void Attach_RetryAfterFailure_Succeeds()
    {
        int calls = 0; var fake = new object();
        AcadAttach.ActiveObjectResolver = _ => (++calls >= 3) ? fake : null;
        Assert.Same(fake, AcadAttach.AttachOnly("AutoCAD.Application", maxAttempts: 5));
        Assert.Equal(3, calls);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter AcadAttachTests`
Expected: 编译失败（AcadAttach 不存在）

- [ ] **Step 3: 实现 AcadAttach**

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad;

/// <summary>天正 profile 感知附着（spec 切片A §4.1，spike 协议步骤2-3）。
/// CLSIDFromProgID 在 ole32、GetActiveObject 在 oleaut32（标错→EntryPointNotFound 静默吞）。</summary>
public static class AcadAttach
{
    [DllImport("ole32.dll")] private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string progid, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(
        ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    internal static Func<string, object?> ActiveObjectResolver = progid =>
    {
        if (CLSIDFromProgID(progid, out var g) < 0) return null;
        return GetActiveObject(ref g, IntPtr.Zero, out var o) < 0 ? null : o;
    };

    internal static object AttachOnly(string progId, int maxAttempts = 180)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var app = ActiveObjectResolver(progId);
            if (app != null) return app;
            Thread.Sleep(1000);
        }
        throw new CadRuntimeException($"天正实例 ROT 附着超时（{maxAttempts}s）：{progId} 未就绪");
    }
}
```

AcadRuntime 增 `EnsureAppTArch(string profileName)`：`VendorGuard.EnsureNoForeignInstance(Profile)` 前置 → `Process.Start(acadExe, $"/p \"{profileName}\"")`（exePath 经 internal 缝 `ProcessLauncher` 注入，默认自 VendorProfile 派生）→ `AcadAttach.AttachOnly(Profile.ProgId)` 赋 `_app` → Gate1（`RetryBusy` 读 Version 非空 + Documents.Count≥1）。与既有 `EnsureApp`（COM 激活）互斥——`_app` 已存在则复用不重启。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter AcadAttachTests`
Expected: PASS（缝注入，不触真机）

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Adapters.Acad tests/CadAgent.Adapters.Acad.Tests
git commit -m "feat(acad): profile感知启动——/p启动+ROT附着+Gate1等待（AcadAttach+EnsureAppTArch）"
```

---

### Task 5: AcadRuntime.ReadNativeParams 读参编排（fake COM 缝）

**Files:**
- Modify: `src/CadAgent.Adapters.Acad/AcadRuntime.cs`（ReadNativeParams 实现 + ModelSpace 逐实体读）
- Test: `tests/CadAgent.Adapters.Acad.Tests/AcadReadNativeParamsTests.cs`

**Interfaces:**
- Consumes: `NativeEntityParam`/`TDbClassMap`（Task 1）、`AcadAttach`（Task 4）、AcadRuntime 既有 `Run<T>`/`RetryBusy`/`ModelSpace()`（src/CadAgent.Adapters.Acad/AcadRuntime.cs:216）。
- Produces:
  - `AcadRuntime.ReadNativeParams(string? drawingPath = null) : IReadOnlyList<NativeEntityParam>`（覆盖接口默认空实现；path!=null → EnsureAppTArch("TElec20V5") + Open(path) + Gate2 + 读 + Close(false)；path==null → 仅读当前 `_doc` ModelSpace，不启不开不关）
  - `internal static NativeEntityParam? ReadOneEntity(dynamic entity, string handle, DateTimeOffset readAt)`（null=非 TDb 跳过）
  - 内部逐实体读缝：`internal static Func<dynamic,string?> ObjectNameReader`、`internal static Func<dynamic,string,string?> ParamReader`（null=UNKNOWNNAME）、`internal static Func<dynamic,double[]?> BboxReader`、`internal static Func<dynamic,string?> LayerReader`——测试注入 fake
  - `internal static Func<string, object>? TArchAttach`（EnsureAppTArch 测试缝）

- [ ] **Step 1: 写失败测试**（fake 实体 + 缝注入）

```csharp
using CadAgent.Adapters.Acad;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

public class AcadReadNativeParamsTests : IDisposable
{
    private readonly Func<dynamic, string?> _origObj = AcadRuntime.ObjectNameReader;
    private readonly Func<dynamic, string, string?> _origParam = AcadRuntime.ParamReader;
    private readonly Func<dynamic, double[]?> _origBbox = AcadRuntime.BboxReader;
    private readonly Func<dynamic, string?> _origLayer = AcadRuntime.LayerReader;
    public void Dispose()
    {
        AcadRuntime.ObjectNameReader = _origObj;
        AcadRuntime.ParamReader = _origParam;
        AcadRuntime.BboxReader = _origBbox;
        AcadRuntime.LayerReader = _origLayer;
    }

    private static Dictionary<string, string?> _params = new();
    private void Seams(string? objName)
    {
        AcadRuntime.ObjectNameReader = _ => objName;
        AcadRuntime.ParamReader = (_, n) => _params.TryGetValue(n, out var v) ? v : null;
        AcadRuntime.BboxReader = _ => new[] { 0.0, 0, 0, 1, 1, 1 };
        AcadRuntime.LayerReader = _ => "0dt";
    }

    [Fact]
    public void Read_TDbWall_ReadsParams()
    {
        _params = new() { ["LeftWidth"] = "100", ["Height"] = "3000", ["Volume"] = "2.895" };
        Seams("TDbWall");
        var p = AcadRuntime.ReadOneEntity(new object(), "21E00", DateTimeOffset.UtcNow)!;
        Assert.Equal("TDbWall", p.ObjectName);
        Assert.Equal(SemanticClass.Wall, p.Class);
        Assert.Equal("100", p.Params["LeftWidth"]);
        Assert.True(p.ReadOk);
    }

    [Fact]
    public void Read_UnknownNameProperty_RecordsDefaultNotFail()
    {
        _params = new() { ["LeftWidth"] = "100" };  // Height 等缺→UNKNOWNNAME(null)
        Seams("TDbWall");
        var p = AcadRuntime.ReadOneEntity(new object(), "21E00", DateTimeOffset.UtcNow)!;
        Assert.True(p.ReadOk);                       // 单属性缺不致 ReadOk=false
        Assert.False(p.Params.ContainsKey("Height"));
    }

    [Fact]
    public void Read_ObjectNameUnreadable_ReadOkFalse()
    {
        Seams(null);  // ObjectName 不可读（忙窗耗尽）
        var p = AcadRuntime.ReadOneEntity(new object(), "21E00", DateTimeOffset.UtcNow)!;
        Assert.False(p.ReadOk);
    }

    [Fact]
    public void Read_NonTDb_ReturnsNull()
    {
        Seams("AcDbLine");
        Assert.Null(AcadRuntime.ReadOneEntity(new object(), "524C", DateTimeOffset.UtcNow));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter AcadReadNativeParamsTests`
Expected: 编译失败（ReadOneEntity/ObjectNameReader 等缝不存在）

- [ ] **Step 3: 实现 ReadNativeParams + ReadOneEntity + 缝**

```csharp
    // 逐实体读缝（spec 切片A §5）：默认走 dynamic COM + RetryBusy；测试注入 fake。
    // UNKNOWNNAME(0x80020006)=属性不存在→记缺省不重试；忙窗拒绝经 RetryBusy 内层处理。
    // 用 InvokeMember 拿真实 COMException.HResult 以区分 UNKNOWNNAME 与忙窗（dynamic 属性访问
    // 对未知成员抛 RuntimeBinderException 不含 HRESULT）——与既有 AddLine 纯 dynamic 不同，此处需 hr 判定。
    internal static Func<dynamic, string?> ObjectNameReader = e =>
        RetryBusy(() => (string?)e.ObjectName);
    internal static Func<dynamic, string?> LayerReader = e =>
        RetryBusy(() => (string?)e.Layer);
    internal static Func<dynamic, string, string?> ParamReader = (e, n) =>
    {
        try
        {
            object? v = RetryBusy(() => e.GetType().InvokeMember(
                n, System.Reflection.BindingFlags.GetProperty, null, (object)e, null));
            return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (System.Runtime.InteropServices.COMException ce) when (ce.HResult == unchecked((int)0x80020006))
        { return null; }  // UNKNOWNNAME→缺省；忙窗耗尽上抛不在此吞（RetryBusy 已重试）
    };
    internal static Func<dynamic, double[]?> BboxReader = e =>
    {
        try
        {
            return RetryBusy(() =>
            {
                // GetBoundingBox([ref]min,[ref]max)：IDispatch 出参经 object[] args 传递（spike phasea 实证）。
                object[] args = { new double[3], new double[3] };
                e.GetType().InvokeMember("GetBoundingBox",
                    System.Reflection.BindingFlags.InvokeMethod, null, (object)e, args);
                var min = (double[])args[0]; var max = (double[])args[1];
                return new[] { min[0], min[1], min[2], max[0], max[1], max[2] };
            });
        }
        catch (Exception)
        { return null; }  // bbox 尽力读（无此面/类型不匹配→null，ReadOk 仍 true；spec §4 诚实边界）
    };

    // spec 切片A §4.1：逐实体 ObjectName 分类→TDb 读自定义名参数；非 TDb 返回 null 跳过。
    internal static NativeEntityParam? ReadOneEntity(dynamic entity, string handle, DateTimeOffset readAt)
    {
        string? objName = ObjectNameReader(entity);
        if (objName == null)
            return new NativeEntityParam(handle, "", SemanticClass.Unknown,
                new Dictionary<string, string>(), null, readAt, ReadOk: false);
        if (!objName.StartsWith("TDb", StringComparison.Ordinal))
            return null;
        var cls = TDbClassMap.ClassFor(objName);
        var parms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ParamNamesFor(objName))
        {
            string? v = ParamReader(entity, name);
            if (v != null) parms[name] = v;
        }
        return new NativeEntityParam(handle, objName, cls, parms, BboxReader(entity), readAt, ReadOk: true,
            Layer: LayerReader(entity));
    }

    internal static string[] ParamNamesFor(string objName) => objName switch
    {
        "TDbWall" => new[] { "LeftWidth", "RightWidth", "Height", "Volume", "IsArc", "TotalWidth" },
        "TDbText" or "TDbDrawingName" => new[] { "Text" },
        "TDbColumn" => new[] { "Width", "Volume" },
        "TDbEquipment" => new[] { "PScale", "LRotation" },
        _ => Array.Empty<string>(),
    };
```

`ReadNativeParams(string? drawingPath = null)` 公开方法（全程在 `Run<T>` STA 队列内）：
- `drawingPath != null`：`EnsureAppTArch("TElec20V5")` → `Open(drawingPath)` → Gate2（RetryBusy Count>0）→ 遍历 ModelSpace（`RetryBusy` 取 Count 与 Item(i)）逐实体 `ReadOneEntity`（Handle 经 RetryBusy 读 `.Handle`）→ 过滤 null → **Close(false)** → 返回 List（自启自开自关；CLI --native 用此形态）
- `drawingPath == null`：仅遍历当前 `_doc` ModelSpace 读参（不启不开不关；native-live 会话已 Open 原图后用此形态）
- `_doc == null && drawingPath == null` → `CadRuntimeException("无打开的图纸")`
- 句柄口径：`(string)` `.Handle` 读 COM 十六进制串（IAcadEntity.Handle 标准属性，与文件级同源实证 21E00）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter AcadReadNativeParamsTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Adapters.Acad tests/CadAgent.Adapters.Acad.Tests
git commit -m "feat(acad): ReadNativeParams读参编排——ObjectName分类+TDb自定义名读参+UNKNOWNNAME记缺省+非TDb跳过"
```

---

### Task 6: CLI `semantic scan --native` 接线 + stdout/审计加性

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（SemanticScan 加 --native 分支，:632-739 区域）
- Test: `tests/CadAgent.Cli.Tests/SemanticScanNativeTests.cs`

**Interfaces:**
- Consumes: `ICadRuntime.ReadNativeParams`（Task 1/5）、`NativeParamSeeder.Seed`（Task 3）、`RuntimeFactory`/`CreateRuntimeIfAvailable`（src/CadAgent.Cli/CommandRouter.Export.cs:18,251）、既有 SemanticScan 的 overlay 加载/Save/审计结构（src/CadAgent.Cli/CommandRouter.cs:632-739）。
- Produces: CLI `semantic scan --native`；stdout/审计加性键 `nativeRead/nativeClassified/nativeReadFailed`（无 --native 恒 0，旧键逐字不变）

- [ ] **Step 1: 写失败测试**（RuntimeFactory 注入 fake runtime）

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Cli.Tests;

public class SemanticScanNativeTests
{
    private sealed class FakeRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) => new[]
        {
            new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
                new Dictionary<string, string> { ["LeftWidth"] = "100" },
                new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, true, "0dt")
        };
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void Dispose() { }
    }

    [Fact]
    public void ScanNative_SeedsOverlay_AndReportsCounts()
    {
        // 临时项目 + 合成 DXF 夹具 + RuntimeFactory 注入 FakeRuntime（镜像既有 SemanticScan 测试先例）。
        // 调 SemanticScan 带 --native；断言：
        //   ① overlay.NativeEntities 含 21E00 + Annotations 含 NativeParameter(Wall)
        //   ② stdout JSON nativeRead=1/nativeClassified=1/nativeReadFailed=0
        //   ③ 旧键 version/annotations/ambiguous 在（逐字兼容）
        // 测试后 RuntimeFactory 还原为 null。
    }
}
```
（测试体搭建镜像既有 SemanticScan CLI 测试的临时项目 + DXF fixture + stdout 捕获 + `CommandRouter.RuntimeFactory` 注入/还原模式。）

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter SemanticScanNativeTests`
Expected: 失败（--native 未实现，无 nativeRead 键）

- [ ] **Step 3: 实现 SemanticScan --native 分支**

在 SemanticScan 主流程（`result.Overlay.Save(overlayPath)` 之后、审计/stdout 之前）加：
```csharp
        int nativeRead = 0, nativeClassified = 0, nativeReadFailed = 0;
        if (args.Contains("--native"))
        {
            ICadRuntime? nrt = RuntimeFactory != null ? RuntimeFactory() : CreateRuntimeIfAvailable(stderr);
            if (nrt != null)
            {
                using (nrt)
                {
                    // ReadNativeParams(path) 自启（/p 天正 profile）+自开+读完关；不需外部 Open。
                    var parms = nrt.ReadNativeParams(drawingPath);
                    nativeRead = parms.Count;
                    nativeClassified = parms.Count(p => p.Class != SemanticClass.Unknown && p.ReadOk);
                    nativeReadFailed = parms.Count(p => !p.ReadOk);
                    NativeParamSeeder.Seed(result.Overlay, parms);
                    result.Overlay.Save(overlayPath);
                }
            }
            else stderr.WriteLine("--native: 无可用厂商运行时，跳过 COM 读参（理解侧降级）");
        }
```
stdout JSON 与审计 params 各加三键 `nativeRead/nativeClassified/nativeReadFailed`（无 --native 时恒 0，旧键逐字不变）。`using CadAgent.Core.Cad;` 与 `using CadAgent.Data.Semantic;` 若缺则补。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter SemanticScanNativeTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Cli tests/CadAgent.Cli.Tests
git commit -m "feat(cli): semantic scan --native——COM读参落overlay+stdout/审计加性nativeRead/nativeClassified/nativeReadFailed"
```

---

### Task 7: NativeGateRefinement 逐实体松绑判据（纯逻辑）

**Files:**
- Create: `src/CadAgent.Data/Semantic/NativeGateRefinement.cs`
- Test: `tests/CadAgent.Data.Tests/NativeGateRefinementTests.cs`

**Interfaces:**
- Consumes: `SemanticOverlay.NativeEntities`（Task 2）、`NativeEntityParam`（Task 1）。
- Produces:
  - `enum NativeGateVerdict { Pass, NotRead, ReadIncomplete, StaleMismatch }`
  - `static class NativeGateRefinement { static NativeGateVerdict Evaluate(SemanticOverlay overlay, string handle, NativeEntityParam? reread) }`——reread=本轮 live 会话重读结果（null=运行时句柄不存在）
  - 一致性判据：reread 非 null 且 reread.ObjectName==record.ObjectName 且 bbox 逐字相等且 Params 字符串化值相等

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class NativeGateRefinementTests
{
    private static NativeEntityParam P(string h, string obj = "TDbWall", bool ok = true, double b = 0.0) => new(
        h, obj, SemanticClass.Wall, new Dictionary<string, string> { ["LeftWidth"] = "100" },
        new[] { b, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, ok);

    private static SemanticOverlay OvWith(NativeEntityParam p)
    { var o = new SemanticOverlay(); o.UpsertNative(p); return o; }

    [Fact]
    public void NotInOverlay_ReturnsNotRead()
        => Assert.Equal(NativeGateVerdict.NotRead,
            NativeGateRefinement.Evaluate(new SemanticOverlay(), "21E00", P("21E00")));

    [Fact]
    public void ReadOkFalse_ReturnsReadIncomplete()
    {
        var o = OvWith(P("21E00", ok: false));
        Assert.Equal(NativeGateVerdict.ReadIncomplete,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00")));
    }

    [Fact]
    public void RereadNull_ReturnsStaleMismatch()  // 运行时句柄不存在
    {
        var o = OvWith(P("21E00"));
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(o, "21E00", reread: null));
    }

    [Fact]
    public void BboxDiffers_ReturnsStaleMismatch()  // stale 防护
    {
        var o = OvWith(P("21E00", b: 0.0));
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00", b: 9.0)));
    }

    [Fact]
    public void ObjectNameDiffers_ReturnsStaleMismatch()
    {
        var o = OvWith(P("21E00", obj: "TDbWall"));
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00", obj: "TDbColumn")));
    }

    [Fact]
    public void AllMatch_ReturnsPass()
    {
        var o = OvWith(P("21E00"));
        Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00")));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter NativeGateRefinementTests`
Expected: 编译失败（NativeGateRefinement 不存在）

- [ ] **Step 3: 实现**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

public enum NativeGateVerdict { Pass, NotRead, ReadIncomplete, StaleMismatch }

/// <summary>逐实体精准松绑判据（spec 切片A §4.2）：census Yellow 下目标句柄在 NativeEntities
/// 且 ReadOk 且本轮 live 重读一致才放行。重读=null（运行时句柄不存在）/ObjectName/bbox/Params
/// 任一项不符→StaleMismatch（stale overlay 防护）。安全关键：拒绝面全 reason 覆盖 100%。</summary>
public static class NativeGateRefinement
{
    public static NativeGateVerdict Evaluate(SemanticOverlay overlay, string handle, NativeEntityParam? reread)
    {
        var rec = overlay.NativeEntities.FirstOrDefault(n => n.Handle == handle);
        if (rec == null) return NativeGateVerdict.NotRead;
        if (!rec.ReadOk) return NativeGateVerdict.ReadIncomplete;
        if (reread == null) return NativeGateVerdict.StaleMismatch;
        if (reread.ObjectName != rec.ObjectName) return NativeGateVerdict.StaleMismatch;
        if (!BboxEq(reread.Bbox, rec.Bbox)) return NativeGateVerdict.StaleMismatch;
        if (!ParamsEq(reread.Params, rec.Params)) return NativeGateVerdict.StaleMismatch;
        return NativeGateVerdict.Pass;
    }

    private static bool BboxEq(double[]? a, double[]? b) =>
        a == null && b == null || (a != null && b != null && a.SequenceEqual(b));

    private static bool ParamsEq(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Data.Tests --filter NativeGateRefinementTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Data/Semantic/NativeGateRefinement.cs tests/CadAgent.Data.Tests
git commit -m "feat(data): NativeGateRefinement逐实体松绑判据——ReadOk+重读一致(ObjectName/bbox/Params)才放行+stale防护"
```

---

### Task 8: 句柄寻址执行器 —— AcadRuntime handle ops + ComReplayService 编辑类 kind

**Files:**
- Modify: `src/CadAgent.Adapters.Acad/AcadRuntime.cs`（实现 ChangeLayerByHandle/MoveByHandle/DeleteByHandle + HandleReader 缝）
- Modify: `src/CadAgent.Data/Cad/ComReplayService.cs`（case change-layer/move/delete + HandleToHex 归一）
- Test: `tests/CadAgent.Adapters.Acad.Tests/AcadHandleOpsTests.cs`
- Test: `tests/CadAgent.Data.Tests/ComReplayHandleKindTests.cs`

**Interfaces:**
- Consumes: `ICadRuntime.ChangeLayerByHandle/MoveByHandle/DeleteByHandle`（Task 1 接口默认 throw）、AcadRuntime 既有 `Run<T>`/`RetryBusy`/`ModelSpace()`、`HandlerHelpers.Req/OptString`（src/CadAgent.Data/Handlers/LineHandlers.cs）。
- Produces:
  - `AcadRuntime.ChangeLayerByHandle/MoveByHandle/DeleteByHandle`（ModelSpace 逐实体 Handle 扫描定位 → 单基元 RetryBusy 执行；找不到句柄→`CadRuntimeException("句柄不存在")`）
  - `internal static Func<dynamic,string?> AcadRuntime.HandleReader`（读 `.Handle`，RetryBusy；测试注入）
  - `internal static string ComReplayService.HandleToHex(string handle)`（十进制串→十六进制大写；已 hex 串→大写；`0x` 前缀剥离；非法→原样返回）
  - `ComReplayService` 支持 kind `change-layer`（params handle+layer）/`move`（handle+dx+dy）/`delete`（handle）

- [ ] **Step 1: 写失败测试**

`tests/CadAgent.Adapters.Acad.Tests/AcadHandleOpsTests.cs`：
```csharp
using CadAgent.Adapters.Acad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

public class AcadHandleOpsTests : IDisposable
{
    private readonly Func<dynamic, string?> _origHandle = AcadRuntime.HandleReader;
    public void Dispose() => AcadRuntime.HandleReader = _origHandle;

    [Fact]
    public void HandleReader_ReadsHexString()
    {
        AcadRuntime.HandleReader = _ => "21E00";
        Assert.Equal("21E00", AcadRuntime.HandleReader(new object()));
    }
}
```

`tests/CadAgent.Data.Tests/ComReplayHandleKindTests.cs`（FakeRuntime 记录调用）：
```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Cad;
using Xunit;

namespace CadAgent.Data.Tests;

public class ComReplayHandleKindTests
{
    public sealed class RecordingRuntime : ICadRuntime
    {
        public readonly List<string> Ops = new();
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void ChangeLayerByHandle(string h, string layer) => Ops.Add($"change-layer:{h}:{layer}");
        public void MoveByHandle(string h, double dx, double dy) => Ops.Add($"move:{h}:{dx}:{dy}");
        public void DeleteByHandle(string h) => Ops.Add($"delete:{h}");
        public void Dispose() { }
    }

    private static StepPlan Plan((string Kind, string Json) step) => new("p1",
        new[] { new StepSpec(step.Kind, JsonDocument.Parse(step.Json).RootElement.Clone()) });

    [Fact]
    public void Replay_ChangeLayer_InvokesHandleOp_WithHexNormalized()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("change-layer", "{\"handle\":\"138752\",\"layer\":\"WALL\"}")), null!);
        Assert.True(r[0].Ok);
        Assert.Equal("change-layer:21E00:WALL", Assert.Single(rt.Ops));
    }

    [Fact]
    public void Replay_Move_InvokesHandleOp()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("move", "{\"handle\":\"21E00\",\"dx\":10,\"dy\":5}")), null!);
        Assert.True(r[0].Ok);
        Assert.Contains("21E00", Assert.Single(rt.Ops));
    }

    [Fact]
    public void Replay_Delete_InvokesHandleOp()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("delete", "{\"handle\":\"21E00\"}")), null!);
        Assert.True(r[0].Ok);
        Assert.Equal("delete:21E00", Assert.Single(rt.Ops));
    }

    [Fact]
    public void Replay_UnknownKind_FailsAndBreaks()
    {
        var rt = new RecordingRuntime();
        var r = new ComReplayService(rt).Replay(
            Plan(("bogus", "{\"handle\":\"21E00\"}")), null!);
        Assert.False(r[0].Ok);
        Assert.Single(r);
    }

    [Theory]
    [InlineData("138752", "21E00")]
    [InlineData("21e00", "21E00")]
    [InlineData("0x21E00", "21E00")]
    public void HandleToHex_Normalizes(string input, string expected)
        => Assert.Equal(expected, ComReplayService.HandleToHex(input));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter ComReplayHandleKindTests`
Expected: 编译失败（HandleToHex 不存在、编辑类 case 未实现）

- [ ] **Step 3: 实现**

ComReplayService.cs：`Map` switch 加三 case（handle 兼容数字/字符串，镜像 HandlerHelpers.Find 契约）：
```csharp
            case "change-layer":
                _rt.ChangeLayerByHandle(HandleToHex(HandleString(p)),
                    p.GetProperty("layer").GetString() ?? "");
                break;
            case "move":
                _rt.MoveByHandle(HandleToHex(HandleString(p)),
                    HandlerHelpers.Req(p, "dx"), HandlerHelpers.Req(p, "dy"));
                break;
            case "delete":
                _rt.DeleteByHandle(HandleToHex(HandleString(p)));
                break;
```
加内部静态（InternalsVisibleTo 可见；Data csproj 若缺则走 csproj 内 `InternalsVisibleTo CadAgent.Data.Tests` —— 既有 Data.Tests 已用 internal？若无先例，改 public static）：
```csharp
    internal static string HandleString(JsonElement p) => p.GetProperty("handle").ValueKind switch
    {
        JsonValueKind.Number => p.GetProperty("handle").GetUInt64().ToString(),
        _ => p.GetProperty("handle").GetString() ?? "",
    };

    internal static string HandleToHex(string handle)
    {
        if (handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) handle = handle[2..];
        if (ulong.TryParse(handle, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var dec))
            return dec.ToString("X");
        return handle.ToUpperInvariant();
    }
```
AcadRuntime.cs：三操作 = `Run(() => { FindByHandle 定位 + 单基元 RetryBusy })`：
```csharp
    internal static Func<dynamic, string?> HandleReader = e =>
        RetryBusy(() => (string?)e.Handle);

    private dynamic FindByHandle(string hexHandle)
    {
        dynamic ms = ModelSpace();
        int count = RetryBusy(() => (int)ms.Count);
        for (int i = 0; i < count; i++)
        {
            dynamic item = RetryBusy<object?>(() => ms.Item(i))!;
            string? h = HandleReader(item);
            if (string.Equals(h, hexHandle, StringComparison.OrdinalIgnoreCase)) return item;
        }
        throw new CadRuntimeException($"句柄不存在于模型空间: {hexHandle}");
    }

    public void ChangeLayerByHandle(string hexHandle, string layer) => Run(() =>
    {
        try
        {
            dynamic e = FindByHandle(hexHandle);
            EnsureLayer(layer);                        // 单写调用各自忙重试（既有基元）
            RetryBusy(() => { e.Layer = layer; });      // 单写调用独立忙重试（I2 口径）
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"ChangeLayerByHandle 失败: {ex.Message}", ex); }
    });

    public void MoveByHandle(string hexHandle, double dx, double dy) => Run(() =>
    {
        try
        {
            dynamic e = FindByHandle(hexHandle);
            // IAcadEntity.Move(Point1, Point2)：位移=Point2−Point1；此处 (0,0)→(dx,dy)。
            RetryBusy(() => { e.Move(Pt(0, 0), Pt(dx, dy)); });
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"MoveByHandle 失败: {ex.Message}", ex); }
    });

    public void DeleteByHandle(string hexHandle) => Run(() =>
    {
        try
        {
            dynamic e = FindByHandle(hexHandle);
            RetryBusy(() => { e.Delete(); });
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"DeleteByHandle 失败: {ex.Message}", ex); }
    });
```
实现注意：`EnsureLayer`/`Pt` 为既有 private 成员（AcadRuntime.cs:222-230），新方法同 class 内直接可用。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Data.Tests --filter ComReplayHandleKindTests`
Expected: PASS（AcadRuntime COM 实现由 Task 10 真机覆盖；单测覆盖映射与归一）

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Adapters.Acad src/CadAgent.Data/Cad/ComReplayService.cs tests
git commit -m "feat(exec): 句柄寻址执行器——AcadRuntime handle ops(层/移/删)+ComReplay编辑类kind+HandleToHex归一"
```

---

### Task 9: run 主闸逐实体松绑准入 + native-live 独执行路径

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（RunPlan 主闸区 :237 之前插 Yellow+native 准入分支）
- Create: `src/CadAgent.Cli/CommandRouter.NativeLive.cs`（native-live 独执行：判据→执行→读回→交付）
- Test: `tests/CadAgent.Cli.Tests/NativeLiveAdmissionTests.cs`

**Interfaces:**
- Consumes: `NativeGateRefinement.Evaluate`（Task 7）、`SemanticOverlay.NativeEntities`（Task 2）、`ICadRuntime.ReadNativeParams(path?)` + handle ops（Task 1/5/8）、`ComReplayService.Replay` + `HandleToHex`（Task 8）、`GateDetector.Inspect`/`GateLevel`（src/CadAgent.Data/GateDetector.cs:5-22）、`SemanticOverlayPath`/`OpenAudit`/`Identity`（CommandRouter 既有）、`RuntimeFactory`（CommandRouter.Export.cs:18）。
- Produces:
  - `private static int? NativeLivePlan(...)`（返回 null=不适用回落既有闸；非 null=已处理退出码）
  - 准入条件：`Level==Yellow` + `--live` + 计划**全部**步骤含 `handle` 参数；否则回落既有 run-rejected（生成类/全图级修改仍拒）
  - 全 Pass（重读一致）→ native-live 独执行：`rt.Open(drawingPath)`（原生 DWG，TDb 存活）→ ops → 读回校验 → `SaveAs(drawingPath + ".live.dwg")`
  - 审计 kind：`native-gate-pass`/`native-gate-refused`/`native-live-step`/`native-live-committed`/`native-live-failed`
  - **原图零写入**（源件 SHA 前后比对入审计 params；唯一写出是 `.live.dwg` 新文件）

- [ ] **Step 1: 写失败测试**（RuntimeFactory FakeRuntime + 三态，夹具镜像 UnparsedGateCliTests 的 Yellow 合成夹具）

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Cli.Tests;

public class NativeLiveAdmissionTests
{
    // FakeRuntime：ReadNativeParams(path) 返回 21E00(TDbWall ReadOk,Layers="0dt",bbox 固定)；
    // handle ops 记录调用；SaveAs 记录 outPath。夹具=Yellow 图纸（合成 TCH DXF 通知）
    // + overlay 含 21E00 + plan change-layer 21E00→WALL。
    [Fact]
    public void Yellow_HandleStep_AllPass_ExecutesOps_DeliversLiveDwg()
    { /* 期望 exit 0；ops=[change-layer:21E00:WALL]；审计 native-gate-pass + native-live-step + native-live-committed；原图 SHA 不变 */ }

    [Fact]
    public void Yellow_HandleStep_RereadMismatch_Refuses_NoOps()
    { /* 重读 bbox 不符 → exit 1；审计 native-gate-refused 含 StaleMismatch；ops 空；无 .live.dwg */ }

    [Fact]
    public void Yellow_GenerationStep_StillRunRejected()
    { /* 无 handle 的 add-line → 既有 run-rejected 语义（松绑不放开全图级修改） */ }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter NativeLiveAdmissionTests`
Expected: 失败（Yellow 一律 run-rejected，native 准入路径不存在）

- [ ] **Step 3: 实现**

CommandRouter.cs RunPlan 主闸区（`if (GateRefusesRun(report))` 分支之前）：
```csharp
        // spec 切片A §4.2：census Yellow 逐实体精准松绑（仅 --live 且计划全部为句柄寻址步骤）。
        if (report.Level == GateLevel.Yellow && args.Contains("--live"))
        {
            int? nativeRc = NativeLivePlan(drawingPath, plan, agentDir, session, audit, op, stderr);
            if (nativeRc != null) return nativeRc.Value;
        }
```
`CommandRouter.NativeLive.cs` 要点（完整实现由实现者按此规格写）：
1. 句柄步骤分类：任一 step 无 `handle` 参数 → `return null`（回落既有拒绝）。
2. overlay 加载（缺失/损坏 → `native-gate-refused{reason}` 审计 + stderr + return 1）。
3. `using(rt)`：`rt.Open(drawingPath)`（原图）；`reread = rt.ReadNativeParams()`（null=当前文档，**只读一次**）；逐 step `HandleToHex(HandleString)` → `NativeGateRefinement.Evaluate(overlay, hex, reread.FirstOrDefault(n=>n.Handle==hex))`；任一非 Pass → `native-gate-refused{verdicts}` + return 1。
4. 全 Pass → `native-gate-pass{handles}` 审计 → `ComReplayService(rt).Replay(plan, drawing)`（句柄类不触 shadow，传 drawing 即兼容签名）→ 任一 step 失败 → `native-live-failed{step,error}` + return 1（**不 SaveAs**，原图零写入）。
5. 读回闸：`after = rt.ReadNativeParams()`；逐 step 期望校验（change-layer→Layer==目标层；move→bbox 各分量恰平移 dx/dy（容差 1e-6）；delete→句柄消失），不符 → `native-live-failed{readback}` + return 1。
6. `rt.SaveAs(drawingPath + ".live.dwg")`；`native-live-committed{outPath, sourceSha, liveSha}`（sourceSha/liveSha=SHA256 文件字节）；return 0。
7. 异常路径 `native-live-failed{error}` + return 1；任何路径不写原图。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter NativeLiveAdmissionTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Cli tests/CadAgent.Cli.Tests
git commit -m "feat(cli): run主闸逐实体松绑准入+native-live独执行——Yellow+全句柄步骤+重读全Pass才执行,交付.live.dwg源件零写入"
```

---

### Task 10: 条件真机测试 + spec §12 闭环 + 全量门禁

**Files:**
- Create: `tests/CadAgent.Adapters.Acad.Tests/AcadNativeLiveTests.cs`（条件真机）
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md`（§12 追加闭环注记）
- Modify: `docs/superpowers/specs/2026-11-24-slice-a-com-parameter-design.md`（§7 范围外更正：句柄执行器以 T8/T9 补入）

**Interfaces:**
- Consumes: 全部前序任务实现；`AcadProbe.SkipReason` 四态（既有）；标本 `outputs/tarch-elec/16#1116.dwg`（真实件，不入库）。
- Produces: 真机证据（读参基准/忙窗注入/句柄层改读回/金丝雀/进程零残留）；spec §12 闭环注记。

- [ ] **Step 1: 写条件真机测试**（Category=RequiresAutoCAD + Collection=AcadLive + SkippableFact，骨架镜像既有 AcadLiveTests）

```csharp
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadNativeLiveTests : IDisposable
{
    // skip-gated（缺标本/无 AutoCAD → Skip 显式原因，非静默绿）：
    // ① ReadNativeParams(副本路径)：TDbWall LeftWidth=100/Height=3000/Volume=2.895 逐字闭合（spike 基准）；
    //    TDb vs AcDb ObjectName 计数与 census 398 相邻。
    // ② 新启即读（忙窗 A 注入）→ 重试后全非空、ReadOk 全 true。
    // ③ 句柄层改（**副本**上）：ChangeLayerByHandle(21E00, 临时层) → 读回 Layer==新层；
    //    MoveByHandle(21E00,10,0) → bbox 平移；DeleteByHandle → 句柄消失。
    // ④ 金丝雀：CUIX/源件/副本 SHA 前后一致；tasklist 前后零 acad；自家 Close+Quit 清理。
}
```
夹具：先 CopySpecimen 到临时目录（镜像既有先例）；所有修改只作用副本；源件只 SHA 比对。

- [ ] **Step 2: 跑真机测试**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter AcadNativeLiveTests`
Expected: 真机全绿（无 AutoCAD/缺标本显式跳过并录原因）

- [ ] **Step 3: spec §12 追加闭环注记**

spec §12 末尾追加切片 A 闭环块：COM 原生读参通道（/p 附着+忙窗协议生产化+UNKNOWNNAME 语义）+ 逐实体精准松绑（run 主闸准入+native-live 独执行+重读校验+源件零写入）+ 诚实边界（COM 读参不补全文件级解析/census Yellow 不变/松绑非整图 Green/仅句柄寻址步骤）。切片 A design §7 范围外更正注记。

- [ ] **Step 4: 全量门禁**

Run: `dotnet test CadAgent.sln`
Expected: 全绿（含条件真机若 AutoCAD 在场；ZWCAD 真机串行）

- [ ] **Step 5: Commit**

```bash
git add tests docs/superpowers/specs
git commit -m "test(acad): 切片A条件真机证据（读参基准/忙窗/句柄层改读回/金丝雀）+spec§12闭环注记"
```

---

## 执行记录（SDD 填写）

（每任务完成状态/fix round/裁决 R1..Rn 由 controller 执行时追加于此。）

### 执行记录（SDD 子代理驱动，2026-11-24/25，分支 feat/cad-agent-slice-a-com-params）

- 预检裁定 R1-R7（签名/载体/句柄 ops/拆分/生成类拒/Layer/bbox 尽力）；执行期裁定 R8-R12（--native 禁预激活/异常降级审计/助手 public/句柄双形态/层名忽略大小写）。详见 `.superpowers/sdd/2026-11-24-slice-a-com-parameter/progress.md`（workspace 已随收尾删除，裁定已收录于 controller 终报）。
- T1 4aed43c / T2 e200f08+46085a2（fix1）/ T3 6936b19 / T4 060531f+7b90baf（fix1）/ T5 ad250c3 / T6 bd2ff2a / T7 6e5ace2 / T8 f6472a0 / T9 3807657+61c42f4（fix1）/ T10 b6e40ac+1372d76+91201b7。
- T10 真机迫使的生产修复：acad.exe 路径解析链（注册表→ProgramFiles→裸名）+ GetBoundingBox by-ref 回写（ParameterModifier）。真机 3/3 绿；全量门禁 1445/0/0（Acad 65/65 零跳过）。
