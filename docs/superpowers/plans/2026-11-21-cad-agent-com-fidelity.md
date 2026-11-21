# CAD 智能体终端 · COM 适配与保真导出与审计包 实现计划（计划 3c）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans（Side chat 内联执行）.

**Goal:** 中望 COM 适配器（STA 专用线程+dynamic COM，厂商无关接口）；保真导出（厂商 SaveAs DWG + ACadSharp 往返校验闸口，无厂商降级显式标注）；审计包（HMAC 外部锚堵尾截断/全链重算）；厂商支持矩阵（已验证状态须真机证据）。

**Architecture:** Core 定义 ICadRuntime/ExportService/AnchorService/VendorSupport（厂商无关）；新项目 CadAgent.Adapters.Zwcad 实现 dynamic COM（不编译期引用厂商 DLL）；Cli export/audit package/vendor 命令。本机 ZWCAD 2025 真机集成测试（Trait RequiresZwcad）。

**Tech Stack:** .NET 8、dynamic COM（Microsoft.CSharp）、ACadSharp 3.8.35、HMACSHA256+DPAPI、xUnit。

**Spec:** spec §3（适配器一等公民/保真双闸口）、§5.5（审计包）、§9（COM 崩溃处置）、§11（厂商矩阵）、§12（外部锚约束）。

## Global Constraints

- **厂商无关**：Core 不引用厂商/ACadSharp；Zwcad 适配器独立项目，dynamic COM（ProgID ZWCAD.Application），不编译期引用 interop DLL（版本碎片化隔离）。
- **STA 串行化**：COM 调用全部经专用 STA 线程任务队列（spec §9）。
- **保真双闸口**：导出经厂商 SaveAs→ACadSharp 读回比对（实体数+类型计数+文本内容抽样）；不过闸→拒绝+审计；无厂商→DXF 交付+降级审计（显式风险标注，不假装等价）。
- **外部锚**：每次 Append 更新 DB 旁 anchor.json（seq+headHash+HMAC）；密钥 DPAPI 保护（本机威胁模型）；验锚检测尾截断（DB head seq < anchor seq）与重算伪造（HMAC 不符）。
- **矩阵证据**：Verified 状态须真机测试证据（日期+覆盖）；未验证版本导出→警示+审计注记。
- 密钥/图纸内容不入审计（既有红线）；453 既有测试持续绿。
- TDD；feat: 前缀；git 署名环境变量；EOF 换行；真机测试 Trait("Category","RequiresZwcad")，无 ZWCAD 环境可过滤跳过。

## 文件结构

```
src/CadAgent.Core/
  Cad/ICadRuntime.cs          ICadRuntime/RuntimeInfo
  Cad/ExportService.cs        保真导出+往返校验+降级
  Cad/VendorSupport.cs        矩阵模型+加载+ gating
  Audit/AnchorService.cs      HMAC 锚+DPAPI 密钥+验锚
src/CadAgent.Adapters.Zwcad/  新项目（dynamic COM STA）
  ZwcadRuntime.cs
src/CadAgent.Cli/
  CommandRouter.Export.cs     export / audit package / vendor list
tests/...
  CadAgent.Adapters.Zwcad.Tests/  真机集成（RequiresZwcad）
```

---

### Task 1: ICadRuntime 契约 + VendorSupport 模型（Core）

**Files:**
- Create: `src/CadAgent.Core/Cad/ICadRuntime.cs`、`src/CadAgent.Core/Cad/VendorSupport.cs`
- Test: `tests/CadAgent.Core.Tests/VendorSupportTests.cs`

**Interfaces:**
- `sealed record RuntimeInfo(string Vendor, string Version, bool Available)`
- `interface ICadRuntime : IDisposable { RuntimeInfo GetInfo(); void Open(string drawingPath); void SaveAs(string outputPath); void Close(bool save); void Restart(); }`（Open/SaveAs/Close/Restart 失败抛 CadRuntimeException）
- `sealed class CadRuntimeException : Exception`
- `enum VendorStatus { Verified, CompatibleUnverified, Unsupported }`
- `sealed record VendorEntry(string Vendor, string VersionRange, string AdapterVersion, VendorStatus Status, string? VerifiedAt, string TestCoverage)`
- `sealed class VendorMatrix`：`static VendorMatrix Load(string jsonPath)`（缺失→空）；`IReadOnlyList<VendorEntry> Entries`；`VendorEntry? Find(string vendor, string version)`（版本前缀匹配 VersionRange）；`bool IsVerified(vendor, version)`

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

public class VendorSupportTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vendor-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Load_Find_VersionPrefixMatch()
    {
        File.WriteAllText(_path, """
            {"entries":[
              {"vendor":"ZWCAD","versionRange":"2025","adapterVersion":"1.0","status":"Verified","verifiedAt":"2026-11-21","testCoverage":"open/saveas/roundtrip"},
              {"vendor":"AutoCAD","versionRange":"2024-2026","adapterVersion":"0.0","status":"CompatibleUnverified","verifiedAt":null,"testCoverage":""}
            ]}
            """);
        var m = VendorMatrix.Load(_path);
        Assert.Equal(2, m.Entries.Count);
        Assert.True(m.IsVerified("ZWCAD", "2025"));
        Assert.False(m.IsVerified("AutoCAD", "2025"));
        Assert.Null(m.Find("GstarCAD", "2025"));
    }

    [Fact]
    public void Load_Missing_Empty()
    {
        Assert.Empty(VendorMatrix.Load(_path).Entries);
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**（JSON camelCase + JsonStringEnumConverter；VersionRange 前缀/区间简化：version.StartsWith(range split '-' 首段) 或相等）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: ICadRuntime contract + vendor support matrix model`

---

### Task 2: ZwcadRuntime COM 适配器（新项目，真机测试）

**Files:**
- Create: `src/CadAgent.Adapters.Zwcad/CadAgent.Adapters.Zwcad.csproj`（net8.0，引用 Core；ImplicitUsings；加入 sln）、`ZwcadRuntime.cs`
- Modify: `src/CadAgent.Cli/CadAgent.Cli.csproj`（引用 Zwcad 项目）
- Create: `tests/CadAgent.Adapters.Zwcad.Tests/`（xunit 项目，引用 Zwcad+Data；加入 sln）、`ZwcadRuntimeTests.cs`

**Interfaces:**
- `sealed class ZwcadRuntime : ICadRuntime`：
  - ctor：启动专用 STA 线程（BlockingCollection<Action> 队列，IsBackground）；`GetInfo()`→ProgID 注册检查（Type.GetTypeFromProgID("ZWCAD.Application") != null）+ 启动 app 取 Version（app.Visible=false）
  - 所有 COM 调用经 `T Run<T>(Func<T>)` 投递到 STA 线程（TaskCompletionSource 同步等待）
  - `Open(path)`：Documents.Open（dynamic）；`SaveAs(path)`：doc.SaveAs(path)（默认 DWG 格式）；`Close(save)`：doc.Close(save)；`Restart()`：Quit+重建 app；`Dispose()`：Quit+队列 CompleteAdding+线程 Join
  - COMException→CadRuntimeException（消息脱敏不含路径外敏感信息）
- 真机测试 `[Trait("Category","RequiresZwcad")]`：GetInfo Available+Version 非空；Open DXF→SaveAs DWG→ACadSharp DwgReader 读回实体数==源；Close+Dispose 干净退出

- [ ] **Step 1: 写失败测试**

```csharp
using ACadSharp.IO;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.Adapters.Zwcad.Tests;

[Trait("Category", "RequiresZwcad")]
public class ZwcadRuntimeTests : IDisposable
{
    private readonly string _dxf;
    private readonly string _dwg = Path.Combine(Path.GetTempPath(), $"zwcad-{Guid.NewGuid():N}.dwg");
    public ZwcadRuntimeTests() => _dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose()
    {
        File.Delete(_dxf);
        if (File.Exists(_dwg)) File.Delete(_dwg);
    }

    [Fact]
    public void GetInfo_Available_Version()
    {
        using var rt = new ZwcadRuntime();
        var info = rt.GetInfo();
        Assert.True(info.Available);
        Assert.Equal("ZWCAD", info.Vendor);
        Assert.False(string.IsNullOrEmpty(info.Version));
    }

    [Fact]
    public void OpenSaveAs_RoundTrip_EntityCountPreserved()
    {
        using var rt = new ZwcadRuntime();
        rt.Open(_dxf);
        rt.SaveAs(_dwg);
        rt.Close(false);
        Assert.True(File.Exists(_dwg));
        var source = DrawingSession.Load(_dxf);
        using var reader = new DwgReader(_dwg);
        var exported = reader.Read();
        Assert.Equal(source.Summarize().Entities.Count, exported.Entities.Count());
    }
}
```
（DxfFixture 在 Data.Tests——Zwcad.Tests 引用 Data.Tests 项目复用；或复制最小夹具。裁定：引用 Data.Tests。）

- [ ] **Step 2: RED**（项目/类型不存在）。
- [ ] **Step 3: 实现** + `dotnet test --filter Category=RequiresZwcad` **真机运行**（本机 ZWCAD 2025；失败则诊断 COM 细节——SaveAs 格式参数/Visible 等，记录实际 API）。
- [ ] **Step 4: GREEN（真机）+ 全套件**。
- [ ] **Step 5: Commit** — `feat: ZWCAD COM runtime adapter (STA queue, dynamic COM) with live-machine roundtrip proof`

---

### Task 3: AnchorService 外部锚（Core）

**Files:**
- Create: `src/CadAgent.Core/Audit/AnchorService.cs`
- Modify: `src/CadAgent.Core/SqliteAuditStore.cs`（Append 后更新锚——加性可选 AnchorService? 参数 ctor 尾部）
- Test: `tests/CadAgent.Core.Tests/AnchorServiceTests.cs`

**Interfaces:**
- `sealed class AnchorService`：ctor(string anchorDir)——密钥文件 anchor-key.bin（DPAPI CurrentUser，首次生成 32 字节随机）；`void Update(long seq, string headHash)`→anchor.json {seq, headHash, signedAt, hmac}（HMACSHA256(key, $"{seq}|{headHash}|{signedAt:O}")，原子写）；`AnchorVerification Verify(long dbHeadSeq, string dbHeadHash)`→`enum AnchorStatus { Ok, Truncated, Forged, Missing }`（anchor 缺失→Missing；dbHeadSeq<anchor.seq→Truncated；HMAC 不符或 headHash 不符→Forged；否则 Ok）
- SqliteAuditStore ctor 加性 `AnchorService? anchor = null`；Append commit 后 `anchor?.Update(seq, hash)`
- DPAPI：复用 System.Security.Cryptography.ProtectedData——**Core csproj 需加包**（3a 曾回退；此次 Core 需要——裁定：AnchorService 放 Core 但 DPAPI 包加回 Core；或锚密钥经注入 IKeyProtector？简化：Core 加 ProtectedData 包，Windows-only 已有程序集属性）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core.Audit;

namespace CadAgent.Core.Tests;

public class AnchorServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}");
    public AnchorServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void UpdateVerify_Ok()
    {
        var a = new AnchorService(_dir);
        a.Update(5, "HASH5");
        Assert.Equal(AnchorStatus.Ok, a.Verify(5, "HASH5"));
    }

    [Fact]
    public void Verify_Truncated()
    {
        var a = new AnchorService(_dir);
        a.Update(10, "HASH10");
        Assert.Equal(AnchorStatus.Truncated, a.Verify(7, "HASH7"));
    }

    [Fact]
    public void Verify_Forged_TamperedAnchor()
    {
        var a = new AnchorService(_dir);
        a.Update(5, "HASH5");
        string path = Path.Combine(_dir, "anchor.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("HASH5", "EVIL"));
        Assert.Equal(AnchorStatus.Forged, new AnchorService(_dir).Verify(5, "EVIL"));
    }

    [Fact]
    public void Verify_Missing()
    {
        Assert.Equal(AnchorStatus.Missing, new AnchorService(_dir).Verify(1, "H"));
    }

    [Fact]
    public void StoreAppend_UpdatesAnchor()
    {
        string db = Path.Combine(_dir, "a.db");
        var anchor = new AnchorService(_dir);
        using var store = new SqliteAuditStore(db, anchor);
        var r = store.Append(new AuditEntry(DateTimeOffset.UtcNow, "u", "Draw", "s", "t", "k", "t", "{}", "", "", "", ""));
        Assert.Equal(AnchorStatus.Ok, anchor.Verify(r.Seq, r.Hash));
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**（Core 加 ProtectedData 8.0.0 包；SqliteAuditStore ctor 加性参数——既有调用零改动）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: HMAC audit anchor with DPAPI key — truncation/forgery detection (spec §12 external anchor)`

---

### Task 4: ExportService 保真导出 + CLI export（Core/Data/Cli）

**Files:**
- Create: `src/CadAgent.Core/Cad/ExportService.cs`、`src/CadAgent.Cli/CommandRouter.Export.cs`
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（路由 case "export"）
- Test: `tests/CadAgent.Core.Tests/ExportServiceTests.cs`、`tests/CadAgent.Cli.Tests/ExportCommandTests.cs`

**Interfaces:**
- ExportService（Core）：`sealed record ExportResult(bool Ok, string Mode, string? Error, int SourceEntities, int ExportedEntities)`（Mode: vendor/dxf-degraded）
  - `ExportService(ICadRuntime? runtime, Func<string, int> countEntities)`——countEntities 由 Data 注入（ACadSharp 读回计数；Core 不引用 ACadSharp）
  - `ExportResult Export(string sourceDxf, string outDwg, string sessionId, IAuditStore audit, OperatorIdentity op)`：
    - runtime 可用→Open(sourceDxf)→SaveAs(outDwg)→Close(false)→countEntities(outDwg) vs countEntities(sourceDxf)→相等→审计 export-committed {mode=vendor, counts}→Ok；不等→审计 export-rejected {counts}→拒绝（不交付）
    - COM 异常→Restart 一次重试→仍失败→审计 export-failed→失败
    - runtime null→File.Copy(sourceDxf, outDxf 同名 .dxf)→审计 export-degraded {mode=dxf, risk="无厂商运行时，DXF 交付，保真度未验证"}→Ok（Mode=dxf-degraded）
- CLI `export --drawing <file> --out <path> [--project dir]`：runtime = ZwcadRuntime（GetInfo.Available）否则 null；矩阵 gating：Find(vendor,version) 非 Verified→stderr 警示+审计注记；结果 JSON+exit 0/1

- [ ] **Step 1: 写失败测试**

Core（fake runtime + fake counter）：
```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

public class ExportServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    public ExportServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FakeRuntime : ICadRuntime
    {
        public bool Fail;
        public int Restarts;
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) { if (Fail) throw new CadRuntimeException("COM 崩溃"); }
        public void SaveAs(string p) => File.WriteAllText(p, "dwg");
        public void Close(bool save) { }
        public void Restart() { Restarts++; Fail = false; }
        public void Dispose() { }
    }

    private static readonly OperatorIdentity Op = new("T\\u", Role.Draw);

    [Fact]
    public void VendorMode_RoundTripMatch_Committed()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        string outDwg = Path.Combine(_dir, "o.dwg");
        var svc = new ExportService(new FakeRuntime(), _ => 3);
        var r = svc.Export(src, outDwg, "s1", _audit, Op);
        Assert.True(r.Ok);
        Assert.Equal("vendor", r.Mode);
        Assert.Contains(_audit.ReadAll(), x => x.Entry.Kind == "export-committed");
    }

    [Fact]
    public void VendorMode_CountMismatch_Rejected()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        var svc = new ExportService(new FakeRuntime(), p => p.EndsWith(".dwg") ? 2 : 3);
        var r = svc.Export(src, Path.Combine(_dir, "o.dwg"), "s1", _audit, Op);
        Assert.False(r.Ok);
        Assert.Contains(_audit.ReadAll(), x => x.Entry.Kind == "export-rejected");
    }

    [Fact]
    public void ComCrash_RestartOnce_ThenSuccess()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        var rt = new FakeRuntime { Fail = true };
        var svc = new ExportService(rt, _ => 3);
        var r = svc.Export(src, Path.Combine(_dir, "o.dwg"), "s1", _audit, Op);
        Assert.True(r.Ok);
        Assert.Equal(1, rt.Restarts);
    }

    [Fact]
    public void NoRuntime_DxfDegraded_ExplicitRisk()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        var svc = new ExportService(null, _ => 3);
        var r = svc.Export(src, Path.Combine(_dir, "o.dwg"), "s1", _audit, Op);
        Assert.True(r.Ok);
        Assert.Equal("dxf-degraded", r.Mode);
        Assert.Contains(_audit.ReadAll(), x => x.Entry.Kind == "export-degraded" && x.Entry.ParamsJson.Contains("保真度未验证"));
    }
}
```
Cli：export 无 ZWCAD 环境降级路径（本机有 ZWCAD——Cli 测试用注入？裁定：Cli export 测试走降级路径需 runtime null——internal static Func<ICadRuntime?>? RuntimeFactory 测试注入 null；真机路径留 Task 7 E2E）。

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** + Cli export（RuntimeFactory 注入）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: fidelity export with vendor SaveAs + roundtrip gate + explicit degradation`

---

### Task 5: 审计包 + 验锚命令（Cli）

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.Export.cs`（audit package / audit verify --anchor）
- Test: `tests/CadAgent.Cli.Tests/AuditPackageTests.cs`

**Interfaces:**
- `audit package --out <dir> [--project dir]`：复制 audit.db + snapshots/ + semantic.json（若存在）+ adjudications.json（若存在）+ anchor.json + manifest.json {createdAt, headSeq, headHash, recordCount, chainVerified, anchorStatus, contents[]}；**llm 配置不复制（含密钥引用）**；stdout 包路径+摘要；exit 0
- `audit verify [--anchor] [--project dir]`：既有链验证 + --anchor 时 AnchorService.Verify(dbHeadSeq, dbHeadHash)→Truncated/Forged→exit 1+stderr；Missing→警示 exit 0（锚未启用）
- 测试：package 内容齐全+manifest chainVerified=true+无 llm-profiles.json；verify --anchor 正常 Ok；篡改 DB 尾截断（DELETE 末条）→Truncated exit 1

- [ ] **Step 1: 写失败测试**（完整代码：run 产生审计→package→manifest 断言→截断检测）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: audit package with manifest + anchor verification (truncation/forgery detection)`

---

### Task 6: 厂商矩阵清单 + vendor list + gating（Cli/资源）

**Files:**
- Create: `src/CadAgent.Cli/vendor-support.json`（内容复制输出目录）、`CommandRouter.Vendor.cs`
- Modify: `CommandRouter.cs`（路由 case "vendor"）、`CommandRouter.Export.cs`（gating 警示）
- Test: `tests/CadAgent.Cli.Tests/VendorCommandTests.cs`

**Interfaces:**
- vendor-support.json 初始：ZWCAD 2025 Verified（verifiedAt 真机测试日期，testCoverage "COM open/saveas/roundtrip 真机"）；AutoCAD 2024-2026 CompatibleUnverified；TArch * Unsupported（运行时预留）
- `vendor list`：矩阵 JSON→exit 0
- export gating：runtime.GetInfo→Find→非 Verified→stderr 警示+审计 export-committed paramsJson 加 verified=false 注记

- [ ] **Step 1: 写失败测试**（vendor list 含 ZWCAD Verified；export 未验证厂商警示——fake runtime Vendor="AutoCAD"）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: vendor support manifest + vendor list + export verification gating`

---

### Task 7: 真机保真 E2E + 承接记录

**Files:**
- Create: `tests/CadAgent.Adapters.Zwcad.Tests/FidelityE2ETests.cs`（Trait RequiresZwcad）
- Modify: spec §12 + vendor-support.json verifiedAt 更新

**Interfaces:**
- E2E（真机 ZWCAD）：构造 DXF（线+文字+块）→export --out DWG（RuntimeFactory 真实 ZwcadRuntime）→ACadSharp 读回实体数/文本内容比对→audit export-committed mode=vendor→audit package→verify --anchor Ok→矩阵 ZWCAD Verified 证据日期更新
- spec §12 追加 3c 完成记录：COM 适配器（STA/dynamic/崩溃重启）/保真双闸口/降级显式/审计包+HMAC 外部锚（截断/伪造检测）/厂商矩阵（ZWCAD 2025 真机 Verified）；3d 布灯基准就绪；诚实边界：锚密钥本机 DPAPI（跨机验证需密钥分发留后续）；AutoCAD 适配器未实现（矩阵 CompatibleUnverified）；SaveAs 格式默认（版本参数留后续）

- [ ] **Step 1: E2E 测试**
- [ ] **Step 2: 真机运行 + 全套件**
- [ ] **Step 3: spec §12 + 矩阵日期**
- [ ] **Step 4: Commit** — `test: live-machine fidelity E2E (ZWCAD); docs: plan-3c completion, 3d readiness`

---

## 自审记录

- 厂商无关：Core ICadRuntime/ExportService（计数经 Func 注入）/AnchorService/VendorMatrix 均不引用 ACadSharp/厂商；Zwcad 独立项目 dynamic COM。
- 外部锚威胁模型：本机 DPAPI 密钥——防尾截断+无密钥重算伪造；跨机分发留后续（诚实注记）。
- 保真闸口：实体计数比对（第一 slice）+文本抽样（E2E）；几何逐点比对留后续（注记）。
- 真机证据：Task 2/7 RequiresZwcad 测试本机运行，矩阵 Verified 有日期+覆盖。
- 453 既有测试保护；SqliteAuditStore ctor 加性锚参数。