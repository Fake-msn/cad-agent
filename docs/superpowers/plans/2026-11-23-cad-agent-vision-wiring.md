# 视觉管道生产接线（Vision Wiring）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把已交付的视觉组件（SheetTiler/VisionCrossChecker/Scanner vision 参）接上生产：离屏渲染 provider + VL reader + View headless 入口 + CLI relay，含出境同意与审计，真实标本端到端闭环。

**Architecture:** Cli 不 using Avalonia（计划4 裁定：view 经独立进程拉起）→ 视觉入口放 **View 进程 headless 模式**（`CadAgent.View.dll --vision-scan`，Avalonia.Headless 平台离屏 Skia）；CLI `semantic scan --vision` relay 该进程（ViewLaunch 同模式）。VisionReader 在 Data（Core ILlmClient 注入，fake 可测）；RenderSnapshotProvider 在 View 实现 Data IRenderSnapshot（无环保持）。闸口：Red 拒（含只读视觉）、Yellow 允许只读视觉审查；出境经 EgressPolicy + `--confirm-egress` 明确确认（headless 无交互）。

**Tech Stack:** .NET 8 / Avalonia 11.3.2 + Avalonia.Headless 11.3.2（View 新增包，HeadlessTestApp 同模式 UseHeadlessDrawing=false+UseSkia）/ ACadSharp 3.8.35 / Core ILlmClient（ImageUrl）/ xUnit + SkippableFact

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §8.5 出境同意 / §9 审计 / §12 剩余边界②（视觉生产接线）

## Global Constraints

- 分支 `feat/cad-agent-sheet-aware` 追加（head 811031e；同一 PR 交付；不 push 不 PR 保留用户）。
- Cli 代码不 using Avalonia（relay 仅进程拉起）；Data 不引用 View（IRenderSnapshot seam 保持）。
- 密钥硬规则：DPAPI/env 解析，永不入 stdout/审计/日志/PNG 元数据；llm-call 审计仅 model/usage/attempt。
- 出境：首次云端发送须明确确认（headless=`--confirm-egress` flag；缺失→拒绝+指引，不静默发送）；local-only profile→拒绝。
- 闸口：Red→拒绝（视觉亦拒）；Yellow→只读视觉审查允许；Green 允许。拒绝留痕（run-rejected 既有 kind 或 vision-rejected 加性）。
- 行为保持：既有 1017 测试全绿；无 --vision 的 semantic scan 逐字不变；view 窗口模式不变。
- 中文注释；EOF 换行；git 署名仅环境变量 Fake-msn（绝不改 config）。
- Windows GBK：dotnet/CLI 输出重定向 UTF-8 文件再读；单项目或全 sln（MSB1008）。
- TDD 每任务红→绿→提交；真实标本只读（outputs 副本）； DashScope profile 已配置（outputs/view-closure/.cadagent，DPAPI 同用户可复制复用）。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Create `src/CadAgent.Data/Vision/VisionReader.cs` | ILlmClient ImageUrl 读瓦片→claims 解析+脱敏 | 1 |
| Create `src/CadAgent.View/Rendering/RenderSnapshotProvider.cs` | IRenderSnapshot 离屏实现 | 2 |
| Modify `src/CadAgent.View/CadAgent.View.csproj` | += Avalonia.Headless 11.3.2 | 2 |
| Create `src/CadAgent.View/VisionScanMode.cs` | headless 入口编排（闸/出境/tiles/reader/cross/overlay/审计/stdout） | 3 |
| Modify `src/CadAgent.View/Program.cs` | --vision-scan 分支 | 3 |
| Modify `src/CadAgent.Cli/CommandRouter.cs`（SemanticScan） | --vision relay View 进程 | 3 |
| Test 各任务 | fake client/headless PNG/入口闸与出境/relay seam | 各 |
| Modify spec §12 + 门禁 + 证据 | 收尾 | 4 |

---

### Task 1: VisionReader（Data，fake 可测）

**Files:**
- Create: `src/CadAgent.Data/Vision/VisionReader.cs`
- Test: `tests/CadAgent.Data.Tests/VisionReaderTests.cs`（新）

**Interfaces:**
- Consumes: Core `ILlmClient.ChatAsync(ChatRequest, CancellationToken)`；`ChatRequest(Messages, MaxTokens, Temperature=0.2, ImageUrl=null)`；`ChatResponse(Content, Usage, Model)`；`VisionClaim(Text, Region, Confidence)`/`VisionReadResult(Claims)`（Data.Vision 既有）
- Produces:
  - `public sealed record VisionTileRead(IReadOnlyList<VisionClaim> Claims, TokenUsage Usage, string? ParseError);`
  - `public sealed class VisionReader { public VisionReader(ILlmClient client, string model); public Task<VisionTileRead> ReadTileAsync(byte[] png, string sheetId, (double MinX,double MinY,double MaxX,double MaxY) rect, CancellationToken ct = default); }`
  - prompt 常量：结构化读图指令（只输出 JSON `{"claims":[{"text","region","confidence"}]}`；不可读→低 confidence；不编造；region=瓦片内相对位置描述）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Data.Vision;

namespace CadAgent.Data.Tests;

internal sealed class FakeVisionClient : ILlmClient
{
    private readonly string _content;
    public string? LastImageUrl;
    public FakeVisionClient(string content) => _content = content;
    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    { LastImageUrl = request.ImageUrl; return Task.FromResult(new ChatResponse(_content, new TokenUsage(100, 50), "fake-vl")); }
    // ILlmClient 其余成员读 Core/Llm/ILlmClient.cs 补全（固定返回）
}

public class VisionReaderTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public async Task ReadTile_ValidJson_ClaimsParsed_ImageUrlSent()
    {
        var client = new FakeVisionClient("{\"claims\":[{\"text\":\"深照型灯\",\"region\":\"左上\",\"confidence\":0.9}]}");
        var reader = new VisionReader(client, "fake-vl");
        var read = await reader.ReadTileAsync(Png, "S1", (0, 0, 100, 100));
        Assert.Single(read.Claims);
        Assert.Equal("深照型灯", read.Claims[0].Text);
        Assert.Null(read.ParseError);
        Assert.StartsWith("data:image/png;base64,", client.LastImageUrl);
        Assert.Equal(150, read.Usage.PromptTokens + read.Usage.CompletionTokens);
    }

    [Fact]
    public async Task ReadTile_JsonEmbeddedInProse_Extracted()
    {
        var client = new FakeVisionClient("读图如下：{\"claims\":[{\"text\":\"AL1\",\"region\":\"中\",\"confidence\":0.8}]} 以上。");
        var read = await new VisionReader(client, "m").ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims);
        Assert.Null(read.ParseError);
    }

    [Fact]
    public async Task ReadTile_Malformed_ParseErrorEmptyClaims()
    {
        var client = new FakeVisionClient("我无法读取该图。");
        var read = await new VisionReader(client, "m").ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Empty(read.Claims);
        Assert.NotNull(read.ParseError);
    }

    [Fact]
    public async Task ReadTile_ClaimMissingFields_SkippedNotThrow()
    {
        var client = new FakeVisionClient("{\"claims\":[{\"text\":\"X\"},{\"text\":\"Y\",\"region\":\"r\",\"confidence\":0.7}]}");
        var read = await new VisionReader(client, "m").ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims); // 缺 confidence/region 的 claim 跳过（诚实：不完整不种子）
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter VisionReader`
Expected: 编译失败（VisionReader 不存在）

- [ ] **Step 3: 实现 VisionReader.cs**

```csharp
using System.Text.Json;
using CadAgent.Core;

namespace CadAgent.Data.Vision;

public sealed record VisionTileRead(IReadOnlyList<VisionClaim> Claims, TokenUsage Usage, string? ParseError);

/// <summary>
/// 视觉读瓦片（生产接线 T1）：PNG→data-url→ILlmClient ImageUrl→结构化 claims。
/// 解析失败→ParseError+空 claims（诚实降级，不编造）；缺字段 claim 跳过。
/// 密钥不经本类（client 由调用方注入）；usage 透传供 llm-call 审计。
/// </summary>
public sealed class VisionReader
{
    public const string Prompt =
        "你是建筑电气施工图读图引擎。这张图是 CAD 渲染瓦片（深色背景为渲染底色非图纸内容）。" +
        "只输出一个 JSON 对象：{\"claims\":[{\"text\":\"可见文字或符号名称\",\"region\":\"瓦片内相对位置\",\"confidence\":0.0~1.0}]}。" +
        "不可读/模糊区域给低 confidence；看不到的不要编造；不要输出 JSON 以外文字。";

    private readonly ILlmClient _client;
    private readonly string _model;
    public VisionReader(ILlmClient client, string model) { _client = client; _model = model; }

    public async Task<VisionTileRead> ReadTileAsync(byte[] png, string sheetId,
        (double MinX, double MinY, double MaxX, double MaxY) rect, CancellationToken ct = default)
    {
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        var resp = await _client.ChatAsync(new ChatRequest(
            new[] { new ChatMessage("user", Prompt) }, 2048, ImageUrl: dataUrl), ct);
        var (claims, error) = ParseClaims(resp.Content);
        return new VisionTileRead(claims, resp.Usage, error);
    }

    internal static (IReadOnlyList<VisionClaim> Claims, string? Error) ParseClaims(string content)
    {
        string? json = TryExtractJson(content);
        if (json == null) return (Array.Empty<VisionClaim>(), "视觉输出非 JSON: " + Truncate(content));
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claims", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (Array.Empty<VisionClaim>(), "视觉 JSON 缺 claims 数组");
            var list = new List<VisionClaim>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("region", out var r) || r.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("confidence", out var c) || c.ValueKind != JsonValueKind.Number) continue;
                list.Add(new VisionClaim(t.GetString()!, r.GetString()!, c.GetDouble()));
            }
            return (list, null);
        }
        catch (JsonException ex)
        {
            return (Array.Empty<VisionClaim>(), "视觉 JSON 解析失败: " + ex.Message);
        }
    }

    private static string? TryExtractJson(string content)
    {
        string s = content.Trim();
        if (s.StartsWith("{")) return s;
        int i = s.IndexOf('{'), j = s.LastIndexOf('}');
        return i >= 0 && j > i ? s[i..(j + 1)] : null;
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
```

- [ ] **Step 4: 运行确认绿 + Data 全量**

Run: `dotnet test tests/CadAgent.Data.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Data/Vision/VisionReader.cs tests/CadAgent.Data.Tests/VisionReaderTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: VisionReader (tile PNG -> VL claims, honest parse degradation)"
```

---

### Task 2: RenderSnapshotProvider（View 离屏实现）

**Files:**
- Create: `src/CadAgent.View/Rendering/RenderSnapshotProvider.cs`
- Modify: `src/CadAgent.View/CadAgent.View.csproj`（+= Avalonia.Headless 11.3.2）
- Test: `tests/CadAgent.View.Tests/RenderSnapshotProviderTests.cs`（新）

**Interfaces:**
- Consumes: Data `IRenderSnapshot.RenderPng(sheetId, rect, w, h)`；`DrawingSession.Sheets`（SheetRecord.FrameBounds/LayoutName/TitleAttributes["viewportHandle"]）；RenderModelBuilder.Build/CropToFrame/BuildViewport（读现状可见性，必要时 internal→public 加性）；CanvasViewModel.Fit(rect,w,h)；DrawingCanvas(CanvasViewModel)
- Produces: `public sealed class RenderSnapshotProvider : IRenderSnapshot { public RenderSnapshotProvider(DrawingSession session); public byte[] RenderPng(string sheetId, (double,double,double,double) rect, int w, int h); }`；静态平台初始化守卫（Avalonia.Headless UseHeadlessDrawing=false+UseSkia，SetupWithoutStarting，一次）

- [ ] **Step 1: 写失败测试**

```csharp
using Avalonia.Headless.XUnit;
using CadAgent.Data;
using CadAgent.View.Rendering;

namespace CadAgent.View.Tests;

public class RenderSnapshotProviderTests
{
    [AvaloniaFact]
    public void RenderPng_ModelSheetTile_NonEmptyPngWithSignature()
    {
        // 夹具：双幅面 doc（镜像 SheetRegistryTests 夹具）+ 框内一条线；session ctor 直建
        var session = ...;
        var provider = new RenderSnapshotProvider(session);
        var sheet = session.Sheets[0];
        byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 400, 300);
        Assert.True(png.Length > 100);
        Assert.Equal(0x89, png[0]); Assert.Equal(0x50, png[1]); // PNG signature
    }

    [AvaloniaFact]
    public void RenderPng_TileOutsideContent_DarkPixels()
    {
        // 瓦片矩形远离线内容→渲染 PNG 解码后中心像素≈背景深色（#1E1E1E）
        // 解码用 WriteableBitmap/Bitmap 读像素（Avalonia.Media.Imaging）
    }

    [AvaloniaFact]
    public void RenderPng_UnknownSheet_Throws()
    {
        var provider = new RenderSnapshotProvider(SessionWithOneSheet());
        Assert.ThrowsAny<ArgumentException>(() => provider.RenderPng("NOPE", (0, 0, 1, 1), 10, 10));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter RenderSnapshotProvider`
Expected: 编译失败

- [ ] **Step 3: 实现**

csproj 加 `<PackageReference Include="Avalonia.Headless" Version="11.3.2" />`。

```csharp
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using CadAgent.Data;
using CadAgent.Data.Vision;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Rendering;

/// <summary>
/// 离屏渲染 provider（生产接线 T2）：幅面瓦片矩形→PNG。Avalonia.Headless 平台
/// （UseHeadlessDrawing=false+UseSkia，HeadlessTestApp 同模式）一次初始化；
/// 模型幅面=CropToFrame 裁剪；布局幅面=BuildViewport 纸面坐标再裁剪。
/// </summary>
public sealed class RenderSnapshotProvider : IRenderSnapshot
{
    private static bool _platformReady;
    private static readonly object _initLock = new();
    private readonly DrawingSession _session;
    private readonly IReadOnlyList<RenderItem> _modelItems;

    public RenderSnapshotProvider(DrawingSession session)
    {
        _session = session;
        _modelItems = RenderModelBuilder.Build(session);
    }

    private sealed class SnapshotApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    private static void EnsurePlatform()
    {
        if (_platformReady) return;
        lock (_initLock)
        {
            if (_platformReady) return;
            AppBuilder.Configure<SnapshotApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            _platformReady = true;
        }
    }

    public byte[] RenderPng(string sheetId, (double MinX, double MinY, double MaxX, double MaxY) rect, int w, int h)
    {
        var sheet = _session.Sheets.FirstOrDefault(s => s.SheetId == sheetId)
            ?? throw new ArgumentException($"未知幅面: {sheetId}", nameof(sheetId));
        EnsurePlatform();
        IReadOnlyList<RenderItem> items;
        if (sheet.LayoutName == null)
        {
            items = RenderModelBuilder.CropToFrame(_modelItems, rect);
        }
        else
        {
            string? vpHandle = sheet.TitleAttributes.GetValueOrDefault("viewportHandle");
            var vp = _session.Doc.Layouts.Select(l => l.AssociatedBlock)
                .Where(b => b != null).SelectMany(b => b!.Entities)
                .OfType<ACadSharp.Entities.Viewport>()
                .FirstOrDefault(v => v.Handle.ToString() == vpHandle)
                ?? throw new ArgumentException($"布局幅面缺视口: {sheetId}", nameof(sheetId));
            items = RenderModelBuilder.CropToFrame(RenderModelBuilder.BuildViewport(_session.Doc, vp), rect);
        }
        var vm = new CanvasViewModel(items);
        vm.Fit(rect, w, h);
        var control = new DrawingCanvas(vm);
        control.Measure(new Size(w, h));
        control.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(new PixelSize(w, h));
        rtb.Render(control);
        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }
}
```

（CropToFrame/BuildViewport 可见性读现状：若 internal 改 public 加性；CropToFrame 签名 (items, rect) 读现状核对；Fit 在 Measure 前调用保证 VM scale 就绪。）

- [ ] **Step 4: 运行确认绿 + View 全量**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS（窗口模式不受 headless 平台守卫影响——守卫仅 provider 调用时初始化；若与窗口测试平台冲突→守卫检测已初始化平台跳过）

- [ ] **Step 5: 提交** `feat: RenderSnapshotProvider (offscreen Skia tile PNG, IRenderSnapshot production impl)`

---

### Task 3: View --vision-scan headless 入口 + CLI relay

**Files:**
- Create: `src/CadAgent.View/VisionScanMode.cs`
- Modify: `src/CadAgent.View/Program.cs`（--vision-scan 分支先于窗口 AppBuilder）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（SemanticScan --vision relay）
- Test: `tests/CadAgent.View.Tests/VisionScanModeTests.cs`（新）、`tests/CadAgent.Cli.Tests/SemanticScanVisionRelayTests.cs`（新）

**Interfaces:**
- Consumes: T1 VisionReader；T2 RenderSnapshotProvider；SheetTiler.Tiles（读现状签名）；VisionCrossChecker.Check(vision, overlay, walked, sheetId)；SemanticScanner.Scan(session, existing, aliases, vision)；UnparsedCensusStore；GateDetector；EgressPolicy(projectDir).Evaluate(profile, drawingData:true)/ConfirmCloudConsent；LlmConfigStore.Load/DpapiKeyProtector.ResolveKey（Agent.cs 同模式）；BlockWalker.Walk
- Produces: `internal static class VisionScanMode { public static int Run(string[] args, TextWriter stdout, TextWriter stderr); }`（View 内 internal+IVT View.Tests 已有？读 csproj IVT 现状补）；CLI `semantic scan --vision [--confirm-egress] [--name <profile>]` relay；stdout JSON `{ok, level, tiles, claims, seeded, ambiguous, usage, parseErrors}`；审计 kind：llm-call（镜像 AgentOrchestrator.AuditLlmCall payload 形状读源码）/egress-consent/egress-blocked/vision-scan（加性）/run-rejected（Red 拒绝）

- [ ] **Step 1: 写失败测试**

View（headless，fake client seam）：
```csharp
[AvaloniaFact]
public async Task VisionScan_YellowSpecimen_FakeClient_SeedsAndAudits()
{
    // sidecar census Yellow + fake ILlmClient（claims 吻合 GT 文字）+ --confirm-egress + local? profile CloudAllowed
    // → exit 0 + stdout seeded>0 + overlay semantic.json 含 LlmInference 标注 + audit llm-call/egress-consent/vision-scan
}
[AvaloniaFact]
public async Task VisionScan_NoConsentFlag_RefusesEgress()
{
    // NeedsFirstCloudConsent + 无 --confirm-egress → exit 1 + stderr 指引 + 零 llm-call + egress-blocked 留痕
}
[AvaloniaFact]
public async Task VisionScan_RedSpecimen_Refused()
{
    // proxy-only Red → exit 1 + run-rejected + 零渲染零 llm
}
```
（fake client 注入 seam：VisionScanMode.Run 加可选参 ILlmClient? clientOverride=null / ProviderProfile? profileOverride=null，测试注入；生产路径 LlmConfigStore 解析。）
CLI relay：
```csharp
[Fact]
public void SemanticScan_Vision_RelaySpawnSeam_ArgsForwarded()
{
    // CommandRouter 加 internal static Func<string[], int>? VisionScanSpawn seam（测试注入捕获 args 返回 0）
    // semantic scan --vision --drawing X --project P --confirm-egress → seam 收到 --vision-scan/--drawing/--project/--confirm-egress
}
[Fact]
public void SemanticScan_NoVision_LegacyInProcess()
{
    // 无 --vision → 不走 seam（seam 置抛异常标志）→ legacy scan stdout 逐字
}
```

- [ ] **Step 2: 运行确认失败**

Run: View filter VisionScanMode + Cli filter SemanticScanVisionRelay
Expected: 编译失败

- [ ] **Step 3: 实现 VisionScanMode + Program 分支 + CLI relay**

VisionScanMode.Run 编排（顺序钉住）：
1. drawing/project 必需（缺失 exit 1 用法）；--confirm-egress/--name 解析
2. session=DrawingSession.Load(drawing, UnparsedCensusStore.Load(project, drawing))；census>0→Store.Save
3. gate=GateDetector.Inspect(session.Summarize(), new EmptySupportMatrix())；**Red→run-rejected 留痕+stderr guidance+exit 1**（Yellow/Green 继续）
4. profile=override ?? LlmConfigStore.Load(project/.cadagent/llm-profiles.json) 活动/‑‑name；key=override? fake : DpapiKeyProtector.ResolveKey；缺→stderr 指引 exit 1
5. egress.Evaluate(profile, true)：BlockedLocalOnly→egress-blocked 留痕+exit 1；NeedsFirstCloudConsent：无 --confirm-egress→egress-blocked 留痕+stderr"需 --confirm-egress 明确确认首次云端出境"+exit 1；有→ConfirmCloudConsent()+egress-consent 留痕
6. walked=BlockWalker.Walk(session.Doc, session.Sheets).ToList()；tiles=SheetTiler.Tiles(...)（读签名）；provider=new RenderSnapshotProvider(session)；reader=new VisionReader(clientOverride ?? new OpenAiClient(profile, key), profile.Model)
7. 逐 tile：png=provider.RenderPng；read=await reader.ReadTileAsync；**llm-call 留痕每 tile**（镜像 AuditLlmCall payload）；parseErrors 收集；claims 按 sheet 聚合
8. 逐 sheet：cross=VisionCrossChecker.Check(new VisionReadResult(sheetClaims), overlay, walked, sheetId)；seeded→overlay.UpsertAnnotation；ambiguous 聚合
9. overlay.Save(project/.cadagent/semantic.json)（仅有 seeded 时写？无 seeded 不写避免空覆盖——读现状 Save 语义：Version++ 原子写；无 seeded 跳过 Save）
10. vision-scan 留痕（params tiles/claims/seeded/ambiguousCount/usage 合计）；stdout JSON；exit 0
异常兜底：catch→stderr+exit 1（不崩进程栈）。

Program.cs：`if (args.Contains("--vision-scan")) return VisionScanMode.Run(args, Console.Out, Console.Error);` 先于 --drawing 窗口分支。
CLI SemanticScan：`if (args.Contains("--vision")) { spawn seam ?? 默认 Process dotnet ViewDll --vision-scan + 转发 --drawing/--project/--confirm-egress/--name; relay stdout/exit; }`（ViewLaunch 进程模式读现状镜像：ProcessStartInfo dotnet + dll 路径 + UseShellExecute=false + RedirectStandardOutput/Error + WaitForExit + 转发）。

- [ ] **Step 4: 运行确认绿 + View/Cli 全量**

Run: `dotnet test tests/CadAgent.View.Tests` + `tests/CadAgent.Cli.Tests`
Expected: PASS

- [ ] **Step 5: 提交** `feat: vision-scan headless entry (gate/egress/tiles/reader/cross-check/audit) + CLI relay`

---

### Task 4: 真实标本端到端 + spec 闭环 + 门禁

**Files:**
- 证据: `outputs/vision-wiring/`（stdout/audit/overlay/日志）
- Modify: spec §12（边界②闭合注记）+ docs/verification/sheet-aware-protocol.md（视觉接线实战节）
- Test: skip-gated 真实端到端（Cli 或 View 测试，DashScope profile 复制复用）

- [x] **Step 1: skip-gated 真实端到端测试**

```csharp
[SkippableFact]
public async Task RealSpecimen_VisionScan_CloudChain_SeedsOrAmbiguous()
{
    // profile+keys 从 outputs/view-closure/.cadagent 复制到临时项目（DPAPI 同用户）；标本 outputs/tarch-native2 副本
    // semantic scan --vision --confirm-egress（relay 真实 View 进程）→ exit 0 + stdout claims/seeded/ambiguous + audit llm-call/egress-consent + overlay 落盘
    // 断言：llm-call>=1 + usage>0 + （seeded>0 或 ambiguous>0）+ 密钥零泄漏（stdout/audit grep sk-）
}
```

- [x] **Step 2: 手动实战循环**（bash 证据落 outputs/vision-wiring/）：1#配套用房 Yellow --vision --confirm-egress 全链；无 flag 拒绝对照；local-only profile 拒绝对照（合成 profile）；audit verify 绿； stdout/overlay 人工抽查 claims 与 GT 对照（幻觉拦截实证）。
- [x] **Step 3: spec §12 边界②闭合注记**（视觉生产接线交付：provider/reader/入口/relay/出境/审计；剩余：视图交互按钮路径 deferred、Region 瓦片校验 deferred）+ 协议追加。
- [x] **Step 4: 门禁连绿两次** `dotnet test CadAgent.sln`（ZWCAD 真机前后 tasklist；日志 outputs/vision-wiring/gate{1,2}.log；精确数字）。
- [x] **Step 5: 提交（docs/test:）；不 push 不 PR**；report+集成菜单保留用户。

---

## Self-Review 记录

- **Spec 覆盖**：§8.5 出境明确确认（--confirm-egress headless；交互按钮 deferred 注记）；§9 llm-call/egress/vision-scan/拒绝留痕；§12 边界②闭合（T4）；⑥ Red 拒视觉/Yellow 只读视觉。
- **占位符扫描**：夹具"镜像既有"指向具体文件；ILlmClient 其余成员/CropToFrame 签名/AuditLlmCall payload/SheetTiler.Tiles 签名均"读现状核定"确定性指令；无 TBD。
- **类型一致性**：VisionTileRead/VisionReader（T1）→provider（T2）→VisionScanMode 消费（T3）→真实链（T4）；IRenderSnapshot 签名逐字；VisionClaim 三参逐字。
- **已知取舍**：headless 平台守卫与窗口模式共存（守卫仅 provider 触发；若冲突检测已初始化跳过）；无 seeded 不写 overlay（防空覆盖）；relay 进程模式保持 Cli-no-Avalonia；视图交互按钮 deferred。
---

## 执行记录（2026-11-23，子代理驱动）

- 分支 feat/cad-agent-sheet-aware 追加，8 提交（e70aeb4..d185977），4 任务全完成 + 最终审查修复波（R11）。
- 门禁：dotnet test CadAgent.sln 连绿两次 **1069 通过 / 0 失败 / 0 跳过**（Llm 30/Core 216/Data 309/View 291/Cli 210/Zwcad 真机 13；真实 DashScope E2E ≤6 调用/跑）。
- 任务内修复环：T1 R5 TryGetDouble+[0,1] 范围闸（net8.0 实测 1e999 返回∞非抛，闸兜两形态）。
- 最终审查修复波：StepEngine TryGetUInt64+非法值审计拒绝（§9）；VisionScanMode catch 终态失败审计+嵌套守卫；using var rtb（19.6MB/瓦内存面）；裸 --skill 拒绝；--session relay 转发；_model 死字段删。
- 裁决 R1-R11 摘要：R1 平台守卫已初始化跳过；R3 测试 --confirm-egress=行使显式确认通道（生产人工 flag 协议注记）；R4 relay 进程 parity；R5 解析范围闸；R6 Intersect 瓦片裁剪（锚点幅面级保持）；R7 两阶段线程序（预渲染同步→reader async）；R8 egress agentDir 镜像+Red-only 视觉闸；R9 裸 --skill 拒绝；R10 --max-tiles 成本闸出处=控制器 dispatch；R11 修复波 scope。
- 真实端到端：1#配套用房 Yellow --vision --confirm-egress → tiles 6/claims 14/seeded 0/ambiguous 14/usage 15960+598；4 条幻觉/误读全拦截（GT 引证）；无 flag/local-only 拒绝对照零 llm-call；审计验链绿；密钥四通道 grep 零命中。
- **诚实缺口**：seeded=0（图签 claims 无语义类，设备类种子未实证）；5/6 空瓦（模型诚实拒绝，覆盖率待优化）；生产 profile timeout 60s 偏紧；真实 Red 标本缺；真实 🟢 待用户 T3 转换。
- **剩余 deferred（post-merge）**：multi-sheet suppression parity/verified-empty census marker/L-sheet handler 显式拒绝/ModelToPaper+AnchorOf consolidate/Region 瓦片校验/bulge 弧保守限/IssuesPanel 布局就绪等待/timeout 默认值/属性表可注入/sidecar 多源键控/Count cap/其余见三计划执行记录。
