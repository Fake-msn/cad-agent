# 切片 B：TArch .NET 深度读 + explode-native 放行 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 经 NETLOAD 托管桥读取天正 TDb 实体的 GeometricExtents/递归 Explode/XData，宿主推导直墙中心线/符号块候选/XData 并入 overlay（Provenance.NativeExplode 0.9），并新增 `explode-native` 操作把 TDb 实体炸开替换为普通图元（桥内单事务、交付 `.live.dwg`、源件零写入）。

**Architecture:** 方案一——桥＝受控执行器。`Protocol`（netstandard2.0）承载 TSV 协议 DTO；`TArchBridge`（net45，AcMgd 19.1）在 AutoCAD 内执行 READ / EXPLODE_REPLACE 两个无参命令；宿主 `AcadRuntime` 经 channel 环境变量+req/res/.done 文件、NETLOAD 看门狗（3600s）与命令轮询（120s）驱动；`TarchDerive` 与 `NativeGateRefinement` 在宿主侧推导与准入，审计/读回/零写入裁决全在宿主。

**Tech Stack:** C#/.NET 8（宿主）× netstandard2.0（协议）× net45（桥，Microsoft.NETFramework.ReferenceAssemblies + AcMgd/AcDbMgd 19.1）；xUnit + SkippableFact；P/Invoke（user32 窗口枚举）。

**Spec:** `docs/superpowers/specs/2026-11-25-slice-b-tarch-deep-read-design.md`（七节用户认可，commit 91703f2）

## Global Constraints

- 桥**不写审计、不写源件、不改配置**；唯一写出=宿主传入的 `.live.dwg`；审计/读回/零写入裁决全在宿主。源件零写入以 Dispose 后 SHA 复核兜底（切片 A 先例）。
- 桥内任何异常 → `status fail` + `error\t<type: msg>` 行，**绝不空结果扮成功**（empty≠nonexistent）。
- NETLOAD 看门狗：**总上限 3600s**（常量 `NetLoadTimeout`）；20s 无进展**无条件**打印一次指引（含 TRUSTEDPATHS 配置/回滚模板）后继续等待；误拒（桥命令未注册）→ `native-load-denied` 明文排障；命令 `.done` 轮询 120s。
- census Yellow 不变；explode 是附加通道。admission 只对「参数门 Pass ∨ 炸开门 Pass」实体放行；符号块仅候选披露不落 ≥0.8；TDbSymbArrow 永久 FAIL 披露；弧墙证据不足→CenterlineJson=null；替换后新基元如实记录"降级为普通图元"。
- overlay JSON 加性：旧文件缺 `ExplodeEvidence` 字段→null 归一空；旧 stdout 键逐字不变（无新旗标时加性键恒 0/空）。`Provenance.NativeExplode` 追加于枚举末尾。
- 桥工程缺 AcMgd 引用时编译唯一空桩（`NoAcadRefsStub`）——解决方案在任何机器可构建可测；桥胶水逻辑仅真机覆盖。
- 真机：`Category=RequiresAutoCAD`+`Collection("AcadLive")`；副本只读纪律+金丝雀（CUIX/源件/副本 SHA、tasklist 前后）；TRUSTEDPATHS 预检只读；实际配置变更由用户执行（执行前单独确认）。
- 身份：git 提交仅环境变量 `GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`，绝不 `git config`；HEREDOC 提交。
- Windows GBK：dotnet/CLI 输出重定向 UTF-8 文件再 Read，不直接打印中文；测试单项目或全 sln。

---

### Task 1: TArchBridge.Protocol 工程 + TSV 编解码

**Files:**
- Create: `src/CadAgent.TArchBridge.Protocol/CadAgent.TArchBridge.Protocol.csproj`
- Create: `src/CadAgent.TArchBridge.Protocol/BridgeRequest.cs`
- Create: `src/CadAgent.TArchBridge.Protocol/BridgeResult.cs`
- Create: `src/CadAgent.TArchBridge.Protocol/TsCodec.cs`
- Create: `tests/CadAgent.TArchBridge.Tests/CadAgent.TArchBridge.Tests.csproj`
- Create: `tests/CadAgent.TArchBridge.Tests/TsCodecTests.cs`
- Modify: `CadAgent.sln`（两工程入 sln）

**csproj 规格**（两处都要，否则编译不过）：
- 测试工程：`TargetFramework net8.0`；`PackageReference` xunit 2.9.x + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk（版本对齐任一既有测试工程）；`ProjectReference ..\..\src\CadAgent.TArchBridge.Protocol\...`
- 后续 Task 5 再加 `ProjectReference` 到既有 `tests/CadAgent.Data.Tests`？不需要——Data.Tests 不依赖桥。

**Interfaces:**
- Produces（namespace `CadAgent.TArchBridge.Protocol`）：
  - `static string TsCodec.Escape(string) / Unescape(string)`；`static string[] TsCodec.SplitLine(string line)`（按未转义 `\t` 切，反斜杠转义 `\\ \t \n`）
  - `record BridgeRequest(string Op, string Dwg, string? Handle, int MaxDepth, string? LiveOut)`：`string Write()`；`static BridgeRequest Parse(string text)`（缺 key/非法值→`FormatException`）
  - `record EvidenceDto(string Handle, string RxClass, string? WrapperType, string? Layer, double[]? Bbox, bool FullyExplodable)`
  - `record PrimDto(string Handle, int Depth, string RxClass, string[] GeomTokens)`
  - `record XDataDto(string Handle, int TypeCode, string Value)`
  - `record BlockDto(string Handle, string Name, double[] Position)`
  - `record FailDto(string Handle, string Reason)`
  - `record ReplaceDto(string Handle, int NewCount, string SavedPath, bool BboxMatch, double Tolerance)`
  - `sealed class BridgeResult { string Status; string? Error; List<EvidenceDto> Evidence; List<PrimDto> Prims; List<XDataDto> XData; List<BlockDto> Blocks; List<FailDto> Fails; ReplaceDto? Replace; static BridgeResult Parse(string text); string Write(); }`

- [ ] **Step 1: 写失败测试** `tests/CadAgent.TArchBridge.Tests/TsCodecTests.cs`

```csharp
using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class TsCodecTests
{
    [Fact]
    public void Escape_ThenUnescape_RoundTripsSpecialChars()
    {
        string raw = "E:\\图纸\\16#1116.dwg\t线\n名\\";
        Assert.Equal(raw, TsCodec.Unescape(TsCodec.Escape(raw)));
    }

    [Fact]
    public void SplitLine_EscapedTab_IsNotDelimiter()
    {
        Assert.Equal(new[] { "a", "b\tc", "d" }, TsCodec.SplitLine("a\tb\\tc\td"));
    }

    [Fact]
    public void Request_RoundTrip_ChinesePathAndNulls()
    {
        var req = new BridgeRequest("explode-replace", "E:\\图纸\\16#1116.dwg", "21E00", 4, "E:\\out\\x.live.dwg");
        var parsed = BridgeRequest.Parse(req.Write());
        Assert.Equal(req.Op, parsed.Op);
        Assert.Equal(req.Dwg, parsed.Dwg);
        Assert.Equal("21E00", parsed.Handle);
        Assert.Equal(4, parsed.MaxDepth);
        Assert.Equal(req.LiveOut, parsed.LiveOut);
    }

    [Fact]
    public void Request_MissingOp_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => BridgeRequest.Parse("dwg\tx.dwg\n"));
        Assert.Contains("op", ex.Message);
    }

    [Fact]
    public void Result_RoundTrip_AllRecordKinds()
    {
        var r = new BridgeResult
        {
            Status = "ok",
            Evidence = { new EvidenceDto("21E00", "TDbWall", "ImpCurve", "0dt",
                new[] { 1.0, 2, 0, 3, 4, 3000 }, true) },
            Prims = { new PrimDto("21E00", 0, "AcDbLine", new[] { "start=1,2,0 end=3,4,0" }) },
            XData = { new XDataDto("3EB5D", 1000, "HYDRANT_RADIUS") },
            Blocks = { new BlockDto("1A2B", "$TWTSYS$00000508", new[] { 5.0, 6, 0 }) },
            Fails = { new FailDto("321A", "eInvalidInput") },
            Replace = new ReplaceDto("21E00", 4, "E:\\out\\x.live.dwg", true, 1e-3),
        };
        var parsed = BridgeResult.Parse(r.Write());
        Assert.Equal("ok", parsed.Status);
        Assert.Single(parsed.Evidence);
        Assert.Equal(3000, parsed.Evidence[0].Bbox![5]);
        Assert.True(parsed.Evidence[0].FullyExplodable);
        Assert.Equal("AcDbLine", parsed.Prims[0].RxClass);
        Assert.Equal("HYDRANT_RADIUS", parsed.XData[0].Value);
        Assert.Equal("$TWTSYS$00000508", parsed.Blocks[0].Name);
        Assert.Equal("eInvalidInput", parsed.Fails[0].Reason);
        Assert.True(parsed.Replace!.BboxMatch);
        Assert.Equal(1e-3, parsed.Replace.Tolerance);
    }

    [Fact]
    public void Result_StatusFail_CarriesError()
    {
        var r = BridgeResult.Parse("status\tfail\nerror\tSystem.Exception: boom\n");
        Assert.Equal("fail", r.Status);
        Assert.Equal("System.Exception: boom", r.Error);
    }

    [Fact]
    public void Result_UnknownRecord_Throws()
    {
        Assert.Throws<FormatException>(() => BridgeResult.Parse("status\tok\nWAT\tx\n"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.TArchBridge.Tests --filter TsCodecTests`
Expected: 编译失败（Protocol 类型不存在）

- [ ] **Step 3: 实现**（三文件+csproj）

`CadAgent.TArchBridge.Protocol.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

`TsCodec.cs`：
```csharp
namespace CadAgent.TArchBridge.Protocol;

public static class TsCodec
{
    public static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");
    public static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 't' => '\t', 'n' => '\n', '\\' => '\\', _ => n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
    public static string[] SplitLine(string line)
    {
        var parts = new System.Collections.Generic.List<string>();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { sb.Append(line[i]).Append(line[++i]); }
            else if (line[i] == '\t') { parts.Add(Unescape(sb.ToString())); sb.Clear(); }
            else sb.Append(line[i]);
        }
        parts.Add(Unescape(sb.ToString()));
        return parts.ToArray();
    }
}
```

`BridgeRequest.cs`：
```csharp
using System.Globalization;

namespace CadAgent.TArchBridge.Protocol;

public sealed record BridgeRequest(string Op, string Dwg, string? Handle, int MaxDepth, string? LiveOut)
{
    public string Write()
    {
        var lines = new System.Collections.Generic.List<string>
        {
            "op\t" + TsCodec.Escape(Op),
            "dwg\t" + TsCodec.Escape(Dwg),
            "maxdepth\t" + MaxDepth.ToString(CultureInfo.InvariantCulture),
        };
        if (Handle != null) lines.Add("handle\t" + TsCodec.Escape(Handle));
        if (LiveOut != null) lines.Add("liveout\t" + TsCodec.Escape(LiveOut));
        return string.Join("\n", lines) + "\n";
    }

    public static BridgeRequest Parse(string text)
    {
        string? op = null, dwg = null, handle = null, liveout = null; int depth = 4;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0) continue;
            var kv = TsCodec.SplitLine(raw);
            if (kv.Length != 2) throw new FormatException($"请求行非法: {raw}");
            switch (kv[0])
            {
                case "op": op = kv[1]; break;
                case "dwg": dwg = kv[1]; break;
                case "handle": handle = kv[1]; break;
                case "liveout": liveout = kv[1]; break;
                case "maxdepth":
                    if (!int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth))
                        throw new FormatException($"maxdepth 非法: {kv[1]}");
                    break;
                default: throw new FormatException($"未知请求键: {kv[0]}");
            }
        }
        if (op == null) throw new FormatException("缺少 op");
        if (dwg == null) throw new FormatException("缺少 dwg");
        if (op == "explode-replace" && (handle == null || liveout == null))
            throw new FormatException("explode-replace 需要 handle 与 liveout");
        return new BridgeRequest(op, dwg, handle, depth, liveout);
    }
}
```

`BridgeResult.cs`：所有 DTO record 如 Interfaces；`Parse` 逐行按首列分派（`status/error/ENTITY/PRIM/XData/BLOCK/FAIL/REPLACED/VERIFY`，未知→`FormatException($"未知记录: {tag}")`）；`Write` 反向输出。`ENTITY` 行：`ENTITY\thandle\trx\trx2\tlayer\tmin\tmax\tfull`，bbox 两点各 `x,y,z`（不可读→空串→null）；`REPLACED\thandle\tcount\tpath` 与 `VERIFY\thandle\t0|1\ttol` 组成 `Replace`。实现者按此逐字段写，字符串数值 `InvariantCulture`。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.TArchBridge.Tests --filter TsCodecTests`
Expected: PASS（7/7）

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.TArchBridge.Protocol tests/CadAgent.TArchBridge.Tests CadAgent.sln
git commit -m "feat(protocol): TArchBridge TSV协议——请求/结果DTO编解码+转义+畸形行显式拒绝"
```

---

### Task 2: TArchBridge 桥工程（READ 命令）+ 条件构建

**Files:**
- Create: `src/CadAgent.TArchBridge/CadAgent.TArchBridge.csproj`
- Create: `src/CadAgent.TArchBridge/NoAcadRefsStub.cs`（无 AcMgd 时唯一编译文件）
- Create: `src/CadAgent.TArchBridge/BridgeFiles.cs`（channel 读请求/写结果，net45 兼容）
- Create: `src/CadAgent.TArchBridge/ReadCommand.cs`（`CADAGENT_TARCH_READ`）
- Create: `tests/CadAgent.TArchBridge.Tests/BridgeFilesTests.cs`
- Modify: `CadAgent.sln`

**Interfaces:**
- Consumes: `Protocol`（Task 1）。
- Produces：
  - `static class BridgeFiles`：`string? ChannelDir()`（env `CADAGENT_TARCH_CHANNEL`）；`(string reqPath, string resPath, string donePath, string id)? NextPending()`（扫描 `req-<id>.txt` 无对应 `.done` 的最小 id）；`void WriteResultAtomic(string resPath, string donePath, string text)`（先写 res 再写 done，`File.Move` 原子）
  - `public class Commands`（桥内）：`[CommandMethod("CADAGENT_TARCH_READ")] void Read()`（channel 优先，无法定位 channel→`Editor.WriteMessage` 报错）；`[CommandMethod("CADAGENT_TARCH_EXPLODE_REPLACE")]` 于 Task 3
  - csproj 条件：`Exists('$(CADAGENT_ACMG_DIR)\AcMgd.dll')` → 编译桥文件+引用 AcMgd/AcDbMgd；否则仅编译 `NoAcadRefsStub.cs`；`CADAGENT_ACMG_DIR` 默认 `C:\Program Files\Autodesk\AutoCAD 2014`

- [ ] **Step 1: 写失败测试** `BridgeFilesTests.cs`（net8 测试直接引用桥工程的 BridgeFiles？桥工程 net45 不可被 net8 引用——**BridgeFiles 放 Protocol 内**，测试走 Protocol）

修正：`BridgeFiles` 移至 `CadAgent.TArchBridge.Protocol`（Task 1 工程），桥引用之。测试：

```csharp
using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class BridgeFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bridge").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void NextPending_SkipsCompletedAndPicksLowestId()
    {
        File.WriteAllText(Path.Combine(_dir, "req-3.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "res-3.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "res-3.done"), "x");
        File.WriteAllText(Path.Combine(_dir, "req-5.txt"), "y");
        var p = BridgeFiles.NextPending(_dir)!;
        Assert.Equal("5", p.Value.Id);
    }

    [Fact]
    public void NextPending_NonePending_ReturnsNull()
        => Assert.Null(BridgeFiles.NextPending(_dir));

    [Fact]
    public void WriteResultAtomic_WritesResThenDone()
    {
        BridgeFiles.WriteResultAtomic(_dir, "7", "status\tok\n");
        Assert.True(File.Exists(Path.Combine(_dir, "res-7.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "res-7.done")));
        Assert.Equal("status\tok\n", File.ReadAllText(Path.Combine(_dir, "res-7.txt")));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.TArchBridge.Tests --filter BridgeFilesTests`
Expected: 编译失败（BridgeFiles 不存在）

- [ ] **Step 3: 实现**

`Protocol/BridgeFiles.cs`（netstandard2.0 兼容）：
```csharp
using System.Globalization;

namespace CadAgent.TArchBridge.Protocol;

public static class BridgeFiles
{
    public static string? ChannelDir() => Environment.GetEnvironmentVariable("CADAGENT_TARCH_CHANNEL");

    public static (string ReqPath, string ResPath, string DonePath, string Id)? NextPending(string channelDir)
    {
        string? best = null; int bestId = int.MaxValue;
        foreach (var f in Directory.GetFiles(channelDir, "req-*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length <= 4) continue;
            string id = name.Substring(4);
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
            if (File.Exists(Path.Combine(channelDir, "res-" + id + ".done"))) continue;
            if (n < bestId) { bestId = n; best = id; }
        }
        return best == null ? null
            : (Path.Combine(channelDir, "req-" + best + ".txt"),
               Path.Combine(channelDir, "res-" + best + ".txt"),
               Path.Combine(channelDir, "res-" + best + ".done"), best);
    }

    public static void WriteResultAtomic(string channelDir, string id, string text)
    {
        string res = Path.Combine(channelDir, "res-" + id + ".txt");
        string done = Path.Combine(channelDir, "res-" + id + ".done");
        string tmp = res + ".tmp";
        File.WriteAllText(tmp, text, System.Text.Encoding.UTF8);
        File.Move(tmp, res, overwrite: true);
        File.WriteAllText(done, "1", System.Text.Encoding.ASCII);
    }
}
```
（注意：`WriteResultAtomic` 测试签名用 `(dir,id,text)`；`NextPending` 用 dir 参——`ChannelDir()` 仅桥内用。）

`CadAgent.TArchBridge.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net45</TargetFramework>
    <Nullable>disable</Nullable>
    <LangVersion>7.3</LangVersion>
    <CADAGENT_ACMG_DIR Condition="'$(CADAGENT_ACMG_DIR)' == ''">C:\Program Files\Autodesk\AutoCAD 2014</CADAGENT_ACMG_DIR>
    <HasAcadRefs Condition="Exists('$(CADAGENT_ACMG_DIR)\AcMgd.dll') and Exists('$(CADAGENT_ACMG_DIR)\AcDbMgd.dll')">true</HasAcadRefs>
    <HasAcadRefs Condition="'$(HasAcadRefs)' == ''">false</HasAcadRefs>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
    <ProjectReference Include="..\CadAgent.TArchBridge.Protocol\CadAgent.TArchBridge.Protocol.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(HasAcadRefs)' == 'true'">
    <Reference Include="AcMgd"><HintPath>$(CADAGENT_ACMG_DIR)\AcMgd.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="AcDbMgd"><HintPath>$(CADAGENT_ACMG_DIR)\AcDbMgd.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
  <ItemGroup Condition="'$(HasAcadRefs)' != 'true'">
    <Compile Remove="**\*.cs" />
    <Compile Include="NoAcadRefsStub.cs" />
  </ItemGroup>
</Project>
```

`NoAcadRefsStub.cs`：`namespace CadAgent.TArchBridge { internal static class NoAcadRefsStub { } }`

`ReadCommand.cs`（net45，AcMgd 全调用；核心逻辑）：
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CadAgent.TArchBridge.Protocol;

[assembly: CommandClass(typeof(CadAgent.TArchBridge.Commands))]

namespace CadAgent.TArchBridge
{
    public class Commands
    {
        [CommandMethod("CADAGENT_TARCH_READ")]
        public void ReadCmd()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            string channel = BridgeFiles.ChannelDir();
            if (channel == null) { ed.WriteMessage("\nCADAGENT_TARCH_READ: 缺少 CADAGENT_TARCH_CHANNEL\n"); return; }
            var pending = BridgeFiles.NextPending(channel);
            if (pending == null) { ed.WriteMessage("\nCADAGENT_TARCH_READ: 无待处理请求\n"); return; }
            var p = pending.Value;
            var result = new BridgeResult { Status = "ok" };
            try
            {
                var req = BridgeRequest.Parse(System.IO.File.ReadAllText(p.ReqPath));
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (!string.Equals(System.IO.Path.GetFileName(doc.Name), System.IO.Path.GetFileName(req.Dwg), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("错图: 当前文档 " + doc.Name + " ≠ " + req.Dwg);
                ReadInto(result, doc.Database, req.MaxDepth);
            }
            catch (System.Exception ex)
            { result.Status = "fail"; result.Error = ex.GetType().Name + ": " + ex.Message; }
            BridgeFiles.WriteResultAtomic(channel, p.Id, result.Write());
            ed.WriteMessage("\nCADAGENT_TARCH_READ done\n");
        }

        internal static void ReadInto(BridgeResult result, Database db, int maxDepth)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string rx; try { rx = ent.GetRXClass().Name; } catch { continue; }
                    if (!rx.StartsWith("TDb", StringComparison.Ordinal)) continue;
                    string handle = id.Handle.ToString();
                    double[] bbox = null;
                    try { var ext = ent.GeometricExtents; bbox = new[] { ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z, ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z }; }
                    catch (System.Exception ex) { result.Fails.Add(new FailDto(handle, "extents:" + ex.GetType().Name)); }
                    var prims = new List<PrimDto>();
                    bool fully = TryExplodeDeep(ent, handle, 0, maxDepth, prims, result);
                    result.Evidence.Add(new EvidenceDto(handle, rx, ent.GetType().Name, ent.Layer, bbox, fully));
                    foreach (var p2 in prims.Take(64)) result.Prims.Add(p2);
                    if (prims.Count > 64) result.Prims.Add(new PrimDto(handle, -1, "PRIM-TRUNCATED", new[] { prims.Count.ToString() }));
                    try
                    {
                        var xd = ent.XData;
                        if (xd != null) foreach (TypedValue tv in xd) result.XData.Add(new XDataDto(handle, tv.TypeCode, Convert.ToString(tv.Value)));
                    }
                    catch (System.Exception ex) { result.Fails.Add(new FailDto(handle, "xdata:" + ex.GetType().Name)); }
                }
                tr.Commit();
            }
        }

        // 递归炸开：产物继续炸到无 TDb；返回是否"完整"（>0 产物且全非 TDb 且无异常）。
        internal static bool TryExplodeDeep(Entity ent, string handle, int depth, int maxDepth, List<PrimDto> outp, BridgeResult result)
        {
            if (depth > maxDepth) { result.Fails.Add(new FailDto(handle, "递归超深")); return false; }
            DBObjectCollection exploded = null;
            try
            {
                exploded = new DBObjectCollection();
                ent.Explode(exploded);
                if (exploded.Count == 0) return false;
                bool fully = true;
                foreach (DBObject o in exploded)
                {
                    var e2 = o as Entity;
                    if (e2 == null) { result.Fails.Add(new FailDto(handle, "非实体产物:" + o.GetType().Name)); fully = false; continue; }
                    string rx2; try { rx2 = e2.GetRXClass().Name; } catch { rx2 = "?"; }
                    if (rx2.StartsWith("TDb", StringComparison.Ordinal))
                    {
                        fully &= TryExplodeDeep(e2, handle, depth + 1, maxDepth, outp, result);
                    }
                    else
                    {
                        outp.Add(new PrimDto(handle, depth, rx2, GeomTokens(e2)));
                        if (e2 is BlockReference br) result.Blocks.Add(new BlockDto(handle, br.Name, new[] { br.Position.X, br.Position.Y, br.Position.Z }));
                    }
                }
                return fully;
            }
            catch (System.Exception ex)
            { result.Fails.Add(new FailDto(handle, "explode:" + ex.GetType().Name)); return false; }
            finally { if (exploded != null) foreach (DBObject o in exploded) { try { o.Dispose(); } catch { } } }
        }

        internal static string[] GeomTokens(Entity e)
        {
            var line = e as Line;
            if (line != null) return new[] { "start=" + F(line.StartPoint), "end=" + F(line.EndPoint) };
            var arc = e as Arc;
            if (arc != null) return new[] { "center=" + F(arc.Center), "r=" + arc.Radius.ToString("0.###"), "ang=" + arc.StartAngle.ToString("0.######") + ".." + arc.EndAngle.ToString("0.######") };
            var circ = e as Circle;
            if (circ != null) return new[] { "center=" + F(circ.Center), "r=" + circ.Radius.ToString("0.###") };
            var txt = e as DBText;
            if (txt != null) return new[] { "text=" + txt.TextString, "pos=" + F(txt.Position) };
            var mt = e as MText;
            if (mt != null) return new[] { "mtext=" + mt.Contents.Replace("\n", "\\n"), "loc=" + F(mt.Location) };
            var pl = e as Polyline;
            if (pl != null)
            {
                var s = new List<string> { "verts=" + pl.NumberOfVertices };
                for (int i = 0; i < pl.NumberOfVertices && i < 32; i++) s.Add(F(pl.GetPoint3dAt(i)));
                return s.ToArray();
            }
            try { var x = e.GeometricExtents; return new[] { "extents=" + F(x.MinPoint) + ".." + F(x.MaxPoint) }; }
            catch { return new[] { "geom=?" }; }
        }

        internal static string F(Autodesk.AutoCAD.Geometry.Point3d p)
            => p.X.ToString("0.###") + "," + p.Y.ToString("0.###") + "," + p.Z.ToString("0.###");
    }
}
```

- [ ] **Step 4: 构建 + 测试**

Run: `dotnet build src/CadAgent.TArchBridge` （本机有 AcMgd → 真编译；无 AcMgd 机器 → 空桩仍绿）
Run: `dotnet test tests/CadAgent.TArchBridge.Tests --filter BridgeFilesTests`
Expected: 构建成功；3/3 PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.TArchBridge tests/CadAgent.TArchBridge.Tests CadAgent.sln
git commit -m "feat(bridge): TArchBridge桥工程(READ命令+递归Explode/XData/extents)+AcMgd条件构建空桩兜底"
```

---

### Task 3: 桥 EXPLODE_REPLACE 命令（事务替换+校验+SaveAs）

**Files:**
- Create: `src/CadAgent.TArchBridge/ExplodeReplaceCommand.cs`
- Modify: `src/CadAgent.TArchBridge/ReadCommand.cs`（`Commands` 类加第二命令方法；`TryExplodeDeep` 复用）
- Test: `tests/CadAgent.TArchBridge.Tests/ReplaceResultShapeTests.cs`（协议层断言，桥胶水由真机 T10 覆盖）

**Interfaces:**
- Consumes: `BridgeRequest/BridgeResult.Replace`、`Commands.TryExplodeDeep`、`BridgeFiles`。
- Produces: `[CommandMethod("CADAGENT_TARCH_EXPLODE_REPLACE")] void ExplodeReplaceCmd()`：请求 `handle`+`liveout`；校验链：文档名→句柄存在→递归炸开（全非 TDb 且产物>0）→bbox 并集≈原 bbox（容差 1e-3）→事务内删除原实体+添加产物→Commit→`db.SaveAs(liveout, DwgVersion.Current)`→写 `REPLACED`+`VERIFY`；任一校验失败→事务 Abort+`status fail`。

- [ ] **Step 1: 写失败测试**（协议形状）

```csharp
using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class ReplaceResultShapeTests
{
    [Fact]
    public void Replace_And_Verify_ParseIntoReplaceDto()
    {
        var parsed = BridgeResult.Parse(
            "status\tok\nREPLACED\t21E00\t4\tE:\\out\\x.live.dwg\nVERIFY\t21E00\t1\t0.001\n");
        Assert.NotNull(parsed.Replace);
        Assert.Equal(4, parsed.Replace!.NewCount);
        Assert.True(parsed.Replace.BboxMatch);
        Assert.Equal(0.001, parsed.Replace.Tolerance);
    }

    [Fact]
    public void Replace_FailureShape_StatusFail()
    {
        var parsed = BridgeResult.Parse("status\tfail\nerror\tInvalidOperationException: bbox 不匹配\n");
        Assert.Null(parsed.Replace);
        Assert.Contains("bbox", parsed.Error);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.TArchBridge.Tests --filter ReplaceResultShapeTests`
Expected: PASS 或编译失败——若 Task 1 已实现 REPLACED/VERIFY 解析则本步即为绿（协议先行）；若失败按下方实现补齐解析。

- [ ] **Step 3: 实现桥命令**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadAgent.TArchBridge.Protocol;

namespace CadAgent.TArchBridge
{
    public partial class Commands
    {
        private const double BboxTolerance = 1e-3;

        [CommandMethod("CADAGENT_TARCH_EXPLODE_REPLACE")]
        public void ExplodeReplaceCmd()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            string channel = BridgeFiles.ChannelDir();
            if (channel == null) { ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE: 缺少 CADAGENT_TARCH_CHANNEL\n"); return; }
            var pending = BridgeFiles.NextPending(channel);
            if (pending == null) { ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE: 无待处理请求\n"); return; }
            var p = pending.Value;
            var result = new BridgeResult { Status = "ok" };
            try
            {
                var req = BridgeRequest.Parse(File.ReadAllText(p.ReqPath));
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (!string.Equals(Path.GetFileName(doc.Name), Path.GetFileName(req.Dwg), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("错图: 当前文档 " + doc.Name + " ≠ " + req.Dwg);
                result.Replace = ReplaceCore(doc.Database, req.Handle, req.LiveOut, req.MaxDepth);
            }
            catch (System.Exception ex)
            { result.Status = "fail"; result.Error = ex.GetType().Name + ": " + ex.Message; }
            BridgeFiles.WriteResultAtomic(channel, p.Id, result.Write());
            ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE done\n");
        }

        internal static ReplaceDto ReplaceCore(Database db, string handle, string liveOut, int maxDepth)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                ObjectId targetId = ObjectId.Null; Entity target = null;
                foreach (ObjectId id in ms)
                {
                    if (!string.Equals(id.Handle.ToString(), handle, StringComparison.OrdinalIgnoreCase)) continue;
                    targetId = id; target = tr.GetObject(id, OpenMode.ForRead) as Entity; break;
                }
                if (target == null) throw new InvalidOperationException("句柄不存在: " + handle);
                Extents3d orig; try { orig = target.GeometricExtents; } catch (System.Exception ex)
                { throw new InvalidOperationException("原实体无 extents: " + ex.GetType().Name); }

                var prims = new List<PrimDto>();
                var scratch = new BridgeResult();
                bool fully = TryExplodeDeep(target, handle, 0, maxDepth, prims, scratch);
                if (!fully || prims.Count == 0)
                    throw new InvalidOperationException("炸开不完整: " + string.Join(";", scratch.Fails.Select(f => f.Reason)));
                if (prims.Any(x => x.RxClass.StartsWith("TDb", StringComparison.Ordinal)))
                    throw new InvalidOperationException("炸开残留 TDb: " + prims.First(x => x.RxClass.StartsWith("TDb", StringComparison.Ordinal)).RxClass);

                // bbox 并集 vs 原 bbox（事务外几何重算前需要真实实体；此处直接以 Explode 产物重算）
                Extents3d union = default; bool first = true;
                DBObjectCollection exploded = new DBObjectCollection();
                Entity targetW = (Entity)tr.GetObject(targetId, OpenMode.ForRead);
                targetW.Explode(exploded);
                foreach (DBObject o in exploded)
                {
                    var e2 = o as Entity; if (e2 == null) { o.Dispose(); continue; }
                    try { var b = e2.GeometricExtents; if (first) { union = b; first = false; } else union = Union(union, b); } catch { }
                }
                bool bboxMatch = first || (Math.Abs(union.MinPoint.X - orig.MinPoint.X) <= BboxTolerance
                    && Math.Abs(union.MinPoint.Y - orig.MinPoint.Y) <= BboxTolerance
                    && Math.Abs(union.MaxPoint.X - orig.MaxPoint.X) <= BboxTolerance
                    && Math.Abs(union.MaxPoint.Y - orig.MaxPoint.Y) <= BboxTolerance);
                if (!bboxMatch) { foreach (DBObject o in exploded) { try { o.Dispose(); } catch { } } throw new InvalidOperationException("bbox 不匹配（并集≠原实体）"); }

                // 替换：产物需归属当前事务——重新 Explode 注入空间
                var msW = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                var added = new DBObjectCollection(); ((Entity)tr.GetObject(targetId, OpenMode.ForRead)).Explode(added);
                int newCount = 0;
                foreach (DBObject o in added) { msW.AppendEntity((Entity)o); tr.AddNewlyCreatedDBObject(o, true); newCount++; }
                ((Entity)tr.GetObject(targetId, OpenMode.ForWrite)).Erase();
                tr.Commit();
                foreach (DBObject o in exploded) { try { o.Dispose(); } catch { } }
                db.SaveAs(liveOut, DwgVersion.Current);
                return new ReplaceDto(handle, newCount, liveOut, true, BboxTolerance);
            }
        }

        private static Extents3d Union(Extents3d a, Extents3d b)
        {
            var min = new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y), Math.Min(a.MinPoint.Z, b.MinPoint.Z));
            var max = new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), Math.Max(a.MaxPoint.Z, b.MaxPoint.Z));
            return new Extents3d(min, max);
        }
    }
}
```
（`Commands` 在 `ReadCommand.cs` 为 `public class`；本文件用 `partial`——**Task 2 落地时把 ReadCommand.cs 的 `public class Commands` 声明为 `public partial class Commands`**，两文件同工程。）

硬约束：SaveAs 仅写 `liveOut`；事务 Abort 由 `using` 未 Commit 自动回滚；产物与原实体同空间（模型空间）。

- [ ] **Step 4: 构建 + 协议测试**

Run: `dotnet build src/CadAgent.TArchBridge`（本机真编译）
Run: `dotnet test tests/CadAgent.TArchBridge.Tests --filter ReplaceResultShapeTests`
Expected: 构建成功；2/2 PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.TArchBridge tests/CadAgent.TArchBridge.Tests
git commit -m "feat(bridge): EXPLODE_REPLACE命令——递归炸开校验/bbox并集比对/事务替换/SaveAs(liveout)"
```

---

### Task 4: Core 模型 + overlay 表 + 接口默认实现

**Files:**
- Create: `src/CadAgent.Core/Cad/ExplodeEvidence.cs`
- Modify: `src/CadAgent.Core/SemanticModel.cs`（Provenance 末尾加 `NativeExplode`）
- Modify: `src/CadAgent.Core/Cad/TDbClassMap.cs`（保守扩类）
- Modify: `src/CadAgent.Core/Cad/ICadRuntime.cs`（`ReadExplodeEvidence` + `ExplodeReplaceByHandle` 默认实现）
- Modify: `src/CadAgent.Core/SemanticOverlay.cs`（`ExplodeEvidence` 表 + `UpsertExplode` + Load null 归一）
- Test: `tests/CadAgent.Core.Tests/ExplodeEvidenceModelTests.cs`

**Interfaces:**
- Produces：
  - `record ExplodePrimitive(string Kind, double[] Points, string? Text, string? BlockName)`
  - `record ExplodeEvidence(string Handle, string RxClass, string? WrapperType, string? Layer, double[]? Bbox, bool FullyExplodable, int PrimitiveCount, int NestedTdbCount, IReadOnlyList<ExplodePrimitive> Primitives, IReadOnlyDictionary<string,string> XData, string? CenterlineJson, DateTimeOffset ReadAt)`
  - `record ExplodeReplaceResult(string Handle, int NewCount, string SavedPath, bool BboxMatch, double Tolerance)`
  - `Provenance.NativeExplode`
  - `ICadRuntime.ReadExplodeEvidence(string drawingPath) : IReadOnlyList<ExplodeEvidence>`（默认空）；`ICadRuntime.ExplodeReplaceByHandle(string hexHandle, string liveOutPath) : ExplodeReplaceResult`（默认 throw "运行时不支持深度炸开（explode-native）"）
  - `SemanticOverlay.ExplodeEvidence : List<ExplodeEvidence>`；`UpsertExplode(ExplodeEvidence)`（键=Handle）
  - TDbClassMap：`TDbDimension2→Annotation、TDbSpace→Room、TDbElevation→Annotation`

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Core.Tests;

public class ExplodeEvidenceModelTests
{
    private static ExplodeEvidence E(string h, bool full = true) => new(
        h, "TDbWall", "ImpCurve", "0dt", new[] { 0.0, 0, 0, 1, 1, 3000 }, full, 4, 0,
        new List<ExplodePrimitive> { new("line", new[] { 0.0, 0, 0, 1.0, 0, 0 }, null, null) },
        new Dictionary<string, string> { ["HYDRANT_RADIUS"] = "28" },
        null, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("TDbDimension2", SemanticClass.Annotation)]
    [InlineData("TDbSpace", SemanticClass.Room)]
    [InlineData("TDbElevation", SemanticClass.Annotation)]
    [InlineData("TDbOpening", SemanticClass.Unknown)]
    public void ClassMap_ExtendedClasses(string obj, SemanticClass expected)
        => Assert.Equal(expected, TDbClassMap.ClassFor(obj));

    [Fact]
    public void Provenance_HasNativeExplode()
        => Assert.True(Enum.IsDefined(typeof(Provenance), "NativeExplode"));

    [Fact]
    public void Overlay_UpsertExplode_Idempotent()
    {
        var o = new SemanticOverlay();
        o.UpsertExplode(E("21E00"));
        o.UpsertExplode(E("21E00", full: false));
        Assert.Single(o.ExplodeEvidence);
        Assert.False(o.ExplodeEvidence[0].FullyExplodable);
    }

    [Fact]
    public void Overlay_Json_Additive_And_RoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("explode").FullName;
        try
        {
            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacy, """{"Annotations":[],"Mappings":[],"Version":1}""");
            Assert.Empty(SemanticOverlay.Load(legacy).ExplodeEvidence);

            string path = Path.Combine(dir, "semantic.json");
            var o = new SemanticOverlay(); o.UpsertExplode(E("21E00")); o.Save(path);
            var loaded = SemanticOverlay.Load(path);
            Assert.Single(loaded.ExplodeEvidence);
            Assert.Equal("0dt", loaded.ExplodeEvidence[0].Layer);
            Assert.Equal("28", loaded.ExplodeEvidence[0].XData["HYDRANT_RADIUS"]);
            Assert.Equal(1, loaded.ExplodeEvidence[0].Primitives[0].Points[3]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Core.Tests --filter ExplodeEvidenceModelTests`
Expected: 编译失败（类型不存在）

- [ ] **Step 3: 实现**（records 按 Interfaces；`SemanticOverlay` 加 `public List<ExplodeEvidence> ExplodeEvidence { get; set; } = new();`、`UpsertExplode` 按 Handle FindIndex 替换、`Load` 后 `overlay.ExplodeEvidence ??= new();`；`ICadRuntime` 两默认成员——`ReadExplodeEvidence => Array.Empty<ExplodeEvidence>()`、`ExplodeReplaceByHandle => throw new CadRuntimeException("运行时不支持深度炸开（explode-native）")`）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Core.Tests --filter ExplodeEvidenceModelTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Core tests/CadAgent.Core.Tests
git commit -m "feat(core): 切片B模型——ExplodeEvidence/ExplodeReplaceResult表+Provenance.NativeExplode+ICadRuntime深度炸开默认拒绝"
```

---

### Task 5: AcadRuntime 桥通道（NETLOAD 看门狗 + READ + EXPLODE_REPLACE）

**Files:**
- Create: `src/CadAgent.Adapters.Acad/BridgeChannel.cs`（channel/发送/轮询/看门狗；internal 静态缝）
- Create: `src/CadAgent.Adapters.Acad/AcadWindows.cs`（user32 EnumWindows/GetWindowThreadProcessId/GetWindowTextW；`internal static Func<int,bool> SecurityDialogDetector` 默认实现，标题含"安全"/"Security"）
- Modify: `src/CadAgent.Adapters.Acad/AcadRuntime.cs`（`ReadExplodeEvidence` + `ExplodeReplaceByHandle` + `_bridgeLoaded` + 默认 `BridgeChannel.SendCommand` 注入 + `GetTrustedPaths`）
- Modify: `src/CadAgent.Adapters.Acad/CadAgent.Adapters.Acad.csproj`（`ProjectReference` 加 `..\CadAgent.TArchBridge.Protocol\CadAgent.TArchBridge.Protocol.csproj`——netstandard2.0 由 net8 直接引用）
- Test: `tests/CadAgent.Adapters.Acad.Tests/BridgeChannelTests.cs`

**Interfaces:**
- Consumes: `CadAgent.TArchBridge.Protocol`（Protocol 工程需被 Adapters.Acad 引用——netstandard2.0 可被 net8 引用）；`AcadRuntime` 既有 `Run<T>`/`RetryBusy`/`EnsureAppTArch`/`Open` 私有路径。
- Produces（`BridgeChannel`，全 internal + seam）：
  - `static Func<string,string,string> SendCommand`（默认 dynamic `_app.ActiveDocument.SendCommand` 包装——由 AcadRuntime 注入而非 BridgeChannel 持有 app，签名 `(command, echoMark)`）
  - `static string WriteRequest(string channelDir, string requestText)`（写 `req-<id>.txt`，id=现有最大+1）
  - `static string? WaitDone(string channelDir, string id, TimeSpan timeout)`（500ms 轮询 `.done`，返回 `res-<id>.txt` 内容或 null）
  - `static void EnsureNetLoad(string bridgeDll, string channelDir, Func<TimeSpan,bool> watchdog, TimeSpan netLoadTimeout)`：写 netload 请求标记→SendCommand(`(command "NETLOAD" "…")`)→watchdog 回调（20s 指引由调用方打印）→超时抛 `CadRuntimeException("NETLOAD 超时（3600s）：可能安全弹窗未处理")`
  - 误拒归因：NETLOAD 返回后调命令 → 返回内容含"未知命令/Unknown command"→`CadRuntimeException("NETLOAD 被拒绝：可能点击【不加载】/TRUSTEDPATHS 未含桥目录/SECURELOAD 阻止")`

- [ ] **Step 1: 写失败测试**（fake seam）

```csharp
using CadAgent.Adapters.Acad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

public class BridgeChannelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chan").FullName;
    private readonly Func<string,string,string> _origSend = BridgeChannel.SendCommand;
    public void Dispose() { BridgeChannel.SendCommand = _origSend; Directory.Delete(_dir, true); }

    [Fact]
    public void WriteRequest_IncrementsId()
    {
        Assert.Equal("1", BridgeChannel.WriteRequest(_dir, "a"));
        Assert.Equal("2", BridgeChannel.WriteRequest(_dir, "b"));
        Assert.Equal("a", File.ReadAllText(Path.Combine(_dir, "req-1.txt")));
    }

    [Fact]
    public void WaitDone_ReturnsResultContent()
    {
        File.WriteAllText(Path.Combine(_dir, "res-1.txt"), "status\tok\n");
        File.WriteAllText(Path.Combine(_dir, "res-1.done"), "1");
        Assert.Equal("status\tok\n", BridgeChannel.WaitDone(_dir, "1", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WaitDone_TimesOut_ReturnsNull()
        => Assert.Null(BridgeChannel.WaitDone(_dir, "9", TimeSpan.FromMilliseconds(100)));

    [Fact]
    public void EnsureNetLoad_UnknownCommandEcho_ThrowsDenied()
    {
        BridgeChannel.SendCommand = (cmd, mark) => mark == "netload" ? "" : "未知命令 \"CADAGENT_TARCH_READ\"。";
        var ex = Assert.Throws<CadAgent.Core.Cad.CadRuntimeException>(
            () => BridgeChannel.EnsureNetLoad("b.dll", _dir, _ => true, TimeSpan.FromSeconds(1)));
        Assert.Contains("NETLOAD 被拒绝", ex.Message);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter BridgeChannelTests`
Expected: 编译失败

- [ ] **Step 3: 实现**

`BridgeChannel` 要点（完整实现由实现者按 Interfaces 写）：
- `EnsureNetLoad`：`SendCommand($"(command \"NETLOAD\" \"{bridgeDll}\")", "netload")`；然后再次 `SendCommand("CADAGENT_TARCH_READ x", "probe")` 感知注册（探针命令调用会被 channel 消耗——实现上用 `(if (null (c:CADAGENT_TARCH_READ)) ...)`? 不引入 LISP 探针：直接以首次真实命令的送出结果判断"未知命令"即误拒）
- `AcadRuntime.ReadExplodeEvidence(path)`：`Run` 体内 EnsureAppTArch→`app.Documents.Open(path, true)`(ReadOnly)→建 channel 目录（`Path.Combine(Path.GetTempPath(), "cadagent-bridge-" + Guid.NewGuid())`）→`BridgeChannel.EnsureNetLoad(BridgeDllPath(), channel, watchdog, NetLoadTimeout)`→`WriteRequest`→`SendCommand("CADAGENT_TARCH_READ","cmd")`→`WaitDone(120s)`→`BridgeResult.Parse`→DTO→Core `ExplodeEvidence` 列表（Bbox/Primitives/XData 映射；`prim.RxClass=="PRIM-TRUNCATED"` 行忽略）→Close(false)→清 channel；`status fail`→`CadRuntimeException("桥失败: " + Error)`
- `ExplodeReplaceByHandle(hex, liveOut)`：同类编排，请求 `op=explode-replace`，结果必有 `Replace`；`status fail`→throw
- `BridgeDllPath()`：`internal static string? BridgeDllOverride`（测试缝）；默认 `AppContext.BaseDirectory/CadAgent.TArchBridge.dll` 存在性检查，缺失→`CadRuntimeException("桥未构建（需 AutoCAD 19.1 引用）：CadAgent.TArchBridge.dll")`
- 看门狗：`NetLoadTimeout=TimeSpan.FromHours(1)`；`internal static Action? NetLoadSlowGuidance`（20s 无进展回调，CLI 注入打印）；`internal static TimeSpan NetLoadNoProgressThreshold = TimeSpan.FromSeconds(20)`；窗口枚举 `internal static Func<int,bool> SecurityDialogDetector`（默认 `AcadWindows.HasSecurityDialog`——user32 `EnumWindows/GetWindowThreadProcessId/GetWindowTextW`，标题含"安全"/"Security"；实现于 `AcadWindows.cs`）
- `_bridgeLoaded` 标志：同实例第二次调用跳过 NETLOAD

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter BridgeChannelTests`
Expected: PASS（4/4，无真机）

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Adapters.Acad tests/CadAgent.Adapters.Acad.Tests
git commit -m "feat(acad): 桥通道——channel三文件协议+NETLOAD 3600s看门狗+误拒归因+READ/EXPLODE_REPLACE编排"
```

---

### Task 6: TarchDerive（中心线/符号块候选/XData 并入/标注种子）

**Files:**
- Create: `src/CadAgent.Data/Semantic/TarchDerive.cs`
- Test: `tests/CadAgent.Data.Tests/TarchDeriveTests.cs`

**Interfaces:**
- Consumes: `ExplodeEvidence/ExplodePrimitive`（Task 4）、`SemanticOverlay`（ExplodeEvidence 表 + NativeEntities）、`TDbClassMap`、`Provenance.NativeExplode`。
- Produces: `record SymbolCandidate(string Handle, string BlockName, string Kind, string ClassHint)`；`record TarchDeriveResult(IReadOnlyList<SymbolCandidate> SymbolCandidates)`；`static TarchDeriveResult TarchDerive.Enrich(SemanticOverlay overlay, IReadOnlyList<ExplodeEvidence> evidences)`——幂等（Upsert by Handle，重扫刷新 CenterlineJson）；陈旧行清理（overlay 中存在但本轮未读到的 ExplodeEvidence 行移除，镜像 NativeParamSeeder 先例）。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class TarchDeriveTests
{
    private static ExplodePrimitive Line(double x1, double y1, double x2, double y2) =>
        new("line", new[] { x1, y1, 0, x2, y2, 0 }, null, null);

    private static ExplodeEvidence Wall(string h, params ExplodePrimitive[] prims) => new(
        h, "TDbWall", "ImpCurve", "0dt", new[] { 0.0, 0, 0, 100, 300, 3000 }, true,
        prims.Length, 0, prims, new Dictionary<string, string>(), null, DateTimeOffset.UtcNow);

    [Fact]
    public void Enrich_ParallelPair_MatchingTotalWidth_DerivesCenterline()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "100" },
            new[] { 0.0, 0.0, 0, 100.0, 300.0, 3000 }, DateTimeOffset.UtcNow, true, "0dt"));
        var r = TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        var cl = o.ExplodeEvidence.Single(e => e.Handle == "21E00").CenterlineJson;
        Assert.NotNull(cl);
        Assert.Contains("\"kind\":\"line\"", cl);
        Assert.Contains("50", cl);   // 中线 y=50
        Assert.Empty(r.SymbolCandidates);
    }

    [Fact]
    public void Enrich_WidthMismatch_NoCenterline()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "150" },
            null, DateTimeOffset.UtcNow, true, null));
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        Assert.Null(o.ExplodeEvidence[0].CenterlineJson);
    }

    [Fact]
    public void Enrich_SingleLine_NoCenterline()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0)) });
        Assert.Null(o.ExplodeEvidence[0].CenterlineJson);
    }

    [Fact]
    public void Enrich_XData_MergesOnlyIntoExistingParams()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("3EB5D", "TDbEquipment", SemanticClass.Equipment,
            new Dictionary<string, string> { ["PScale"] = "100" }, null, DateTimeOffset.UtcNow, true, null));
        TarchDerive.Enrich(o, new[]
        {
            new ExplodeEvidence("3EB5D", "TDbEquipment", "ImpEntity", "E-设备", null, true, 1, 0,
                new[] { Line(0, 0, 1, 0) },
                new Dictionary<string, string> { ["HYDRANT_RADIUS"] = "28" }, null, DateTimeOffset.UtcNow),
        });
        var p = o.NativeEntities.Single(n => n.Handle == "3EB5D");
        Assert.Equal("28", p.Params["XData:HYDRANT_RADIUS"]);
        Assert.Equal("100", p.Params["PScale"]);
    }

    [Fact]
    public void Enrich_FullyAndClassKnown_SeedsNativeExplodeAnnotation()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        var a = Assert.Single(o.Annotations);
        Assert.Equal(Provenance.NativeExplode, a.Provenance);
        Assert.Equal(0.9, a.Confidence);
    }

    [Fact]
    public void Enrich_NotFully_NoAnnotation_ButRowKept()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("21E00") with { FullyExplodable = false } });
        Assert.Single(o.ExplodeEvidence);
        Assert.Empty(o.Annotations);
    }

    [Fact]
    public void Enrich_Rescan_RemovesStaleRows()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("OLD1", Line(0, 0, 1, 0)) });
        TarchDerive.Enrich(o, Array.Empty<ExplodeEvidence>());
        Assert.Empty(o.ExplodeEvidence);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter TarchDeriveTests`
Expected: 编译失败

- [ ] **Step 3: 实现**

`TarchDerive.Enrich` 核心：
```csharp
public static TarchDeriveResult Enrich(SemanticOverlay overlay, IReadOnlyList<ExplodeEvidence> evidences)
{
    var live = new HashSet<string>(evidences.Select(e => e.Handle), StringComparer.Ordinal);
    overlay.ExplodeEvidence.RemoveAll(e => !live.Contains(e.Handle));   // 陈旧行清理（重扫口径）

    var candidates = new List<SymbolCandidate>();
    foreach (var ev in evidences)
    {
        var centerline = DeriveCenterline(overlay, ev);                  // null=证据不足
        overlay.UpsertExplode(ev with { CenterlineJson = centerline });

        // 种子：Fully && ClassKnown &&（参数面存在→bbox 一致容差 1e-3；缺失→不比较）
        var cls = TDbClassMap.ClassFor(ev.RxClass);
        if (ev.FullyExplodable && cls != SemanticClass.Unknown)
        {
            var p = overlay.NativeEntities.FirstOrDefault(n => n.Handle == ev.Handle);
            bool bboxOk = p?.Bbox == null || ev.Bbox == null || BboxClose(p.Bbox, ev.Bbox, 1e-3);
            if (bboxOk)
                overlay.UpsertAnnotation(new SemanticAnnotation(ev.Handle, cls, Provenance.NativeExplode, 0.9,
                    $"NETLOAD 炸开证据: {ev.RxClass}"));
        }

        // XData 并入（仅既有记录）
        if (ev.XData.Count > 0)
        {
            int idx = overlay.NativeEntities.FindIndex(n => n.Handle == ev.Handle);
            if (idx >= 0)
            {
                var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in overlay.NativeEntities[idx].Params) merged[kv.Key] = kv.Value;
                foreach (var kv in ev.XData) merged["XData:" + kv.Key] = kv.Value;
                overlay.NativeEntities[idx] = overlay.NativeEntities[idx] with { Params = merged };
            }
        }

        // 符号块候选（不落标注）
        foreach (var prim in ev.Primitives.Where(pr => pr.Kind == "block" && pr.BlockName != null))
        {
            var hit = TarchSymbolPrefixes.Match(prim.BlockName!);
            if (hit != null)
                candidates.Add(new SymbolCandidate(ev.Handle, prim.BlockName!, hit.Value.Kind, hit.Value.ClassHint));
        }
    }
    return new TarchDeriveResult(candidates);
}
```
`DeriveCenterline`：从 `Primitives` 取 `Kind=="line"` 且 6 点全有限；首线方向 d 归一，法向 n=(-dy,dx)；非平行（方向叉积 >1e-3）丢弃；每条线两端点在 n 上投影均值 offset；唯一线→null；min/max offset 两线间距 vs `NativeEntities[handle].Params["TotalWidth"]`（double 解析 InvariantCulture；缺失则跳过宽度比对）；间距不匹配（>0.5mm）→null；中线 offset=(min+max)/2，端点=两条最外线投影区间并集（min 投影起点、max 投影终点）重建；输出 `{"kind":"line","p1":[x,y,z],"p2":[x,y,z],"width":w}`（System.Text.Json）。
`TarchSymbolPrefixes.Match`：`$DorLib2D$`→`("door-window","门窗库块（候选）")`、`$TWTSYS$`→`("equipment-sym","设备系统符号块（候选）")`，其余 null。`BboxClose` 逐分量容差比较。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Data.Tests --filter TarchDeriveTests`
Expected: PASS（7/7）

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Data/Semantic/TarchDerive.cs tests/CadAgent.Data.Tests
git commit -m "feat(data): TarchDerive——直墙中心线推导(平行对+宽度匹配)/符号块候选披露/XData并入/NativeExplode种子/陈旧清理"
```

---

### Task 7: NativeGateRefinement 炸开门扩展

**Files:**
- Modify: `src/CadAgent.Data/Semantic/NativeGateRefinement.cs`
- Test: `tests/CadAgent.Data.Tests/NativeGateRefinementExplodeTests.cs`

**Interfaces:**
- Produces：`enum NativeGateVerdict` 末尾追加 `ExplodeNotRead, NotFullyExplodable, StaleExplode`；`static NativeGateVerdict EvaluateCombined(SemanticOverlay overlay, string handle, NativeEntityParam? rereadParams, ExplodeEvidence? rereadExplode)`——参数门 `Evaluate(overlay, handle, rereadParams)==Pass` → Pass；否则炸开门：overlay.ExplodeEvidence 缺行→ExplodeNotRead；`!FullyExplodable`→NotFullyExplodable；`rereadExplode==null`→StaleExplode；RxClass 不等或 bbox 不等（容差 1e-3）→StaleExplode；全过→Pass；均不过→返回参数门原 verdict。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class NativeGateRefinementExplodeTests
{
    private static ExplodeEvidence E(string h, string rx = "TDbOpening", bool full = true, double b = 0.0) => new(
        h, rx, "ImpEntity", "L", new[] { b, 0, 0, 10, 10, 0 }, full, 2, 0,
        Array.Empty<ExplodePrimitive>(), new Dictionary<string, string>(), null, DateTimeOffset.UtcNow);

    private static SemanticOverlay Ov(params ExplodeEvidence[] evs)
    { var o = new SemanticOverlay(); foreach (var e in evs) o.UpsertExplode(e); return o; }

    [Fact]
    public void ParamsGatePass_WinsDirectly()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("H1", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string>(), new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, true, null));
        Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.EvaluateCombined(o, "H1", o.NativeEntities[0], null));
    }

    [Fact]
    public void ExplodeGate_PassesWhenParamsMissing()
        => Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.EvaluateCombined(Ov(E("H2")), "H2", null, E("H2")));

    [Fact]
    public void NoExplodeRow_ExplodeNotRead()
        => Assert.Equal(NativeGateVerdict.ExplodeNotRead,
            NativeGateRefinement.EvaluateCombined(new SemanticOverlay(), "H3", null, E("H3")));

    [Fact]
    public void NotFully_NotFullyExplodable()
        => Assert.Equal(NativeGateVerdict.NotFullyExplodable,
            NativeGateRefinement.EvaluateCombined(Ov(E("H4", full: false)), "H4", null, E("H4", full: false)));

    [Fact]
    public void RereadNull_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H5")), "H5", null, null));

    [Fact]
    public void BboxDrift_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H6", b: 0)), "H6", null, E("H6", b: 5)));

    [Fact]
    public void RxDrift_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H7")), "H7", null, E("H7", rx: "TDbEquipment")));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter NativeGateRefinementExplodeTests`
Expected: 编译失败

- [ ] **Step 3: 实现**（按 Interfaces；bbox 容差 1e-3 逐分量）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Data.Tests --filter NativeGateRefinementExplodeTests`
Expected: PASS（7/7）

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Data/Semantic/NativeGateRefinement.cs tests/CadAgent.Data.Tests
git commit -m "feat(data): 准入炸开门——EvaluateCombined(参数门∨炸开门)+ExplodeNotRead/NotFully/Stale三拒因"
```

---

### Task 8: CLI `semantic scan --explode` 接线

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（SemanticScan 区，`--native` 块之后）
- Test: `tests/CadAgent.Cli.Tests/SemanticScanExplodeTests.cs`

**Interfaces:**
- Consumes: `ICadRuntime.ReadExplodeEvidence`、`TarchDerive.Enrich`、R8 直构模式（`RuntimeFactory` 优先；否则 `ResolveRuntimeName()=="acad"` 直构；否则警示跳过）。
- Produces：`semantic scan --explode`；stdout/审计加性 `explodeRead/explodeFully/explodePartial/explodeFailed/explodeSymbolCandidates`（候选数组 {handle,blockName,kind,classHint}；无旗标恒 0/空）。

- [ ] **Step 1: 写失败测试**（FakeRuntime 增 `ReadExplodeEvidence` 返回两条：21E00 Fully=true 两平行线；321A False 零产物）

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Cli.Tests;

public class SemanticScanExplodeTests
{
    private sealed class FakeExplodeRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath) => new[]
        {
            new ExplodeEvidence("21E00", "TDbWall", "ImpCurve", "0dt",
                new[] { 0.0, 0, 0, 100, 300, 3000 }, true, 2, 0,
                new List<ExplodePrimitive>
                {
                    new("line", new[] { 0.0, 0, 0, 100.0, 0, 0 }, null, null),
                    new("line", new[] { 0.0, 100, 0, 100.0, 100, 0 }, null, null),
                },
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
            new ExplodeEvidence("321A", "TDbSymbArrow", "ImpEntity", "C公用-符号",
                null, false, 0, 0, Array.Empty<ExplodePrimitive>(),
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
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
    public void ScanExplode_SeedsEvidence_AndReportsCounts()
    {
        // 夹具镜像既有 SemanticScan CLI 测试（临时项目+DXF fixture+stdout 捕获+RuntimeFactory 注入/还原）。
        // 断言：overlay.ExplodeEvidence=2 行；21E00 CenterlineJson 非空；Annotations 含 NativeExplode(0.9)；
        // stdout explodeRead=2/explodeFully=1/explodePartial=0/explodeFailed=1；五键存在；旧键逐字在。
        // 第二用例：RuntimeFactory 抛 → explode-read-failed 审计 + exit 0（理解侧降级）。
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter SemanticScanExplodeTests`
Expected: 失败（--explode 未实现）

- [ ] **Step 3: 实现**（SemanticScan `--native` 块之后）

```csharp
        int explodeRead = 0, explodeFully = 0, explodePartial = 0, explodeFailed = 0;
        IReadOnlyList<SymbolCandidate> explodeCandidates = Array.Empty<SymbolCandidate>();
        if (args.Contains("--explode"))
        {
            ICadRuntime? ert = RuntimeFactory != null ? RuntimeFactory()
                : ResolveRuntimeName() == "acad" ? CreateRuntimeInstance("acad") : null;
            if (ert == null)
                stderr.WriteLine("--explode: 无可用 AutoCAD 运行时（--explode 需 CADAGENT_RUNTIME=acad），跳过深度读");
            else
            {
                try
                {
                    using (ert)
                    {
                        var evs = ert.ReadExplodeEvidence(drawingPath);
                        explodeRead = evs.Count;
                        explodeFully = evs.Count(e => e.FullyExplodable);
                        explodePartial = evs.Count(e => !e.FullyExplodable && e.Primitives.Count + e.NestedTdbCount > 0);
                        explodeFailed = evs.Count(e => !e.FullyExplodable && e.Primitives.Count + e.NestedTdbCount == 0);
                        var derive = TarchDerive.Enrich(result.Overlay, evs);
                        explodeCandidates = derive.SymbolCandidates;
                        result.Overlay.Save(overlayPath);
                    }
                }
                catch (Exception ex)
                {
                    audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                        session, "semantic", "explode-read-failed", drawingPath,
                        JsonSerializer.Serialize(new { Error = ex.Message }), "", "", "", ""));
                    stderr.WriteLine($"--explode: 深度读失败（{ex.Message}），理解侧降级");
                }
            }
        }
```
stdout/审计 params 各加五键；候选序列化 `{handle, blockName, kind, classHint}`；需 `using CadAgent.Data.Semantic;`（TarchDerive/SymbolCandidate）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter SemanticScanExplodeTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Cli tests/CadAgent.Cli.Tests
git commit -m "feat(cli): semantic scan --explode——深度读落overlay+推导+五键加性披露+失败审计降级"
```

---

### Task 9: NativeLive 集成（explode-native 准入与执行）

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.NativeLive.cs`
- Test: `tests/CadAgent.Cli.Tests/NativeLiveExplodeTests.cs`

**Interfaces:**
- Consumes: `EvaluateCombined`（Task 7）、`ReadExplodeEvidence`+`ExplodeReplaceByHandle`（Task 4/5）、既有 `RunSession`/`ReplaceStepHandles`/`AggregateReadback`/零写入收尾（切片 A）。
- Produces：白名单扩 `explode-native`；会话开路序 = `ReadExplodeEvidence(path) → ReadNativeParams(path) → Open`；执行循环按计划顺序：`explode-native` → `rt.ExplodeReplaceByHandle(hex, drawingPath + ".live.dwg")` + 审计 `native-explode-step{hex,newCount,savedPath,bboxMatch}`；handle 类 kind → 单步 `ComReplayService`；读回：handle 类沿用聚合（排除 explode-native），explode-native = `rt.ReadNativeParams()`(null) 不含 hex 且 `NewCount>0 && BboxMatch`。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Cli.Tests;

public class NativeLiveExplodeTests
{
    // FakeRuntime：ReadExplodeEvidence(path) → 21E00 Fully=true bbox 固定；ReadNativeParams(path) → 同句柄参数记录
    // （bbox 与炸开一致）；ExplodeReplaceByHandle → 记录调用、NewCount=4、BboxMatch=true，并令后续
    // ReadNativeParams()(null) 不再含 21E00（模拟替换）；SaveAs 写 .live.dwg。
    [Fact]
    public void ExplodeNative_AllPass_Executes_AndAudits()
    { /* exit 0；审计 native-explode-step；.live.dwg 存在；源件 SHA 不变 */ }

    [Fact]
    public void ExplodeNative_MixedWithChangeLayer_OrderPreserved()
    { /* 计划 [explode-native 21E00, change-layer 21F00→L] → 审计顺序 native-explode-step → native-live-step */ }

    [Fact]
    public void ExplodeNative_NotFully_Refused()
    { /* Fully=false → native-gate-refused 含 NotFullyExplodable；无 ops；exit 1 */ }

    [Fact]
    public void ExplodeNative_RereadGoneFail_ReadbackFails()
    { /* Replace 成功但 (null) 读回仍含原句柄 → native-live-failed；exit 1 */ }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter NativeLiveExplodeTests`
Expected: 失败（explode-native 不在白名单 → run-rejected）

- [ ] **Step 3: 实现**

1. `NormalizeKind`：`"explode-native" => "explode-native"`；白名单判定 `is not ("change-layer" or "move" or "delete" or "explode-native")`。
2. `RunSession` 开路序：`var rereadExplode = rt.ReadExplodeEvidence(drawingPath);` → `var rereadParams = rt.ReadNativeParams(drawingPath);` → `rt.Open(drawingPath);`。准入逐 step：`NativeGateRefinement.EvaluateCombined(nativeOverlay, hex, rereadParams.FirstOrDefault(n=>n.Handle==hex), rereadExplode.FirstOrDefault(e=>e.Handle==hex))`；任一非 Pass → `native-gate-refused{verdicts}` + exit 1。
3. 执行循环（替换一次性 Replay）：
```csharp
            var explodeResults = new List<(string Hex, ExplodeReplaceResult Rep)>();
            for (int si = 0; si < plan.Steps.Count; si++)
            {
                var step = plan.Steps[si];
                string kind = NormalizeKind(step.Kind);
                string hex = matched[si];
                if (kind == "explode-native")
                {
                    var rep = rt.ExplodeReplaceByHandle(hex, drawingPath + ".live.dwg");
                    Audit("native-explode-step", hex, new { hex, rep.NewCount, rep.SavedPath, rep.BboxMatch });
                    explodeResults.Add((hex, rep));
                }
                else
                {
                    var single = new StepPlan(plan.PlanId, new[] { ReplacedStep(step, hex) });
                    // packages：镜像 LiveReplay 既有来源 `LivePackages(args)`（handle 类不消费包，可传空表；实现者取既有可及变量即可）
                    var rr = new ComReplayService(rt, packages).Replay(single, drawing);
                    if (!rr[0].Ok) { Audit("native-live-failed", hex, new { kind, rr[0].Error }); return 1; }
                    Audit("native-live-step", hex, new { kind, hex });
                }
            }
```
4. 读回：`AggregateReadback` 输入排除 explode-native 步；随后 `var afterParams = rt.ReadNativeParams();` 对每个 explode 结果断言句柄消失且 `NewCount>0 && BboxMatch`，否则 `native-live-failed{readback:"explode-native"}` + return 1。
5. 零写入/finally 收尾/Hash 复核原样复用。

- [ ] **Step 4: 跑测试确认通过 + 切片 A 回归**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter NativeLiveExplodeTests` → 4/4
Run: `dotnet test tests/CadAgent.Cli.Tests --filter "NativeLiveAdmissionTests|SemanticScanNativeTests"` → 全绿

- [ ] **Step 5: Commit**

```bash
git add src/CadAgent.Cli tests/CadAgent.Cli.Tests
git commit -m "feat(cli): explode-native准入执行——炸开门准入+会话开路序+逐step顺序执行+读回校验+审计"
```

---

### Task 10: 条件真机测试（TRUSTEDPATHS 守卫 + READ/递归/EXPLODE_REPLACE）

**Files:**
- Create: `tests/CadAgent.Adapters.Acad.Tests/AcadBridgeLiveTests.cs`
- Modify: `src/CadAgent.Adapters.Acad/AcadRuntime.cs`（`internal string GetTrustedPaths() => Run(() => (string)EnsureApp().GetSystemVariable("TRUSTEDPATHS"));`）

**Interfaces:**
- Consumes: `ReadExplodeEvidence`/`ExplodeReplaceByHandle`、`GetTrustedPaths`、`AcadProbe.SkipReason`、`AcadRuntime.BridgeDllOverride`、标本 `outputs/tarch-elec/16#1116.dwg`。
- Produces: 三条 SkippableFact + 守卫：桥 DLL 缺失→Skip；TRUSTEDPATHS 不含桥目录→Skip（原因含配置指引）；标本缺失→Skip。

- [ ] **Step 1: 写测试**（骨架镜像切片 A `AcadNativeLiveTests`：Trait/Collection/SkippableFact/副本/金丝雀/自家清理）

```csharp
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadBridgeLiveTests : IDisposable
{
    // 守卫：BridgeDll 存在；GetTrustedPaths() 含桥目录（否则 Skip 附指引）；标本副本可用。
    // T1 READ 基准：ReadExplodeEvidence(副本)→343 条；21E00 bbox=(127749.996,120300,0)..(127849.996,130100,3000)
    //   容差 1e-2；TDbSymbArrow 至少 1 条 FAIL（reason 含 eInvalidInput）；XData 含 HYDRANT_RADIUS=28。
    // T2 递归：若有 TDbSpace 证据→NestedTdbCount>0 断言；标本无 TDbSpace→Skip("标本无 TDbSpace 样本")。
    // T3 EXPLODE_REPLACE：rt.Open(副本)→ExplodeReplaceByHandle("21E00", 副本+".live.dwg")→
    //   NewCount>0/BboxMatch=true；rt.ReadNativeParams()(null) 不含 21E00；Close；Dispose；
    //   .live.dwg 存在；源标本/副本 SHA 不变；CUIX 不变；tasklist 前后 0。
}
```

- [ ] **Step 2: 跑真机测试**

Run: `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter AcadBridgeLiveTests`
Expected: 真机绿；未配置 TRUSTEDPATHS 时显式 Skip（原因如实）——**不伪造**

- [ ] **Step 3: Commit**

```bash
git add tests/CadAgent.Adapters.Acad.Tests src/CadAgent.Adapters.Acad
git commit -m "test(acad): 桥真机证据——TRUSTEDPATHS守卫+READ基准/递归/EXPLODE_REPLACE+金丝雀"
```

---

### Task 11: spec §12 闭环注记 + 全量门禁

**Files:**
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md`（§12 尾部追加切片 B 块）
- Modify: `docs/superpowers/specs/2026-11-25-slice-b-tarch-deep-read-design.md`（实际偏差则追加更正注记）

- [ ] **Step 1: 追加 spec §12 切片 B 闭环块**

内容：NETLOAD 桥通道闭环（TRUSTEDPATHS 机制+3600s 看门狗+误拒归因）、READ 基准数字（343/21E00 extents/XData HYDRANT_RADIUS）、explode-native 真机证据（NewCount/BboxMatch/源件 SHA）、诚实边界复核（TDbSymbArrow FAIL/弧墙 null/符号块仅候选/替换降级为普通图元/census Yellow 不变）、验证边界（误拒路径未自动化；TRUSTEDPATHS 为环境前置）。

- [ ] **Step 2: 全量门禁**

Run: `dotnet test CadAgent.sln`（UTF-8 重定向；跑后 `tasklist | grep -i acad` 为空）
Expected: 全绿（桥真机按环境配置执行或显式 Skip，均计入）

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs
git commit -m "docs: 切片B spec§12闭环注记（桥通道/explode-native证据/诚实边界/验证边界）"
```

---

## 执行记录（SDD 填写）

（每任务完成状态/fix round/裁决由 controller 执行时追加。）
