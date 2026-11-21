# CAD 智能体终端 · 布灯基准任务 实现计划（计划 3d）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans（Side chat 内联执行）.

**Goal:** 第二期基准场景验证：建筑底图→房间识别→AI 布灯点位→语义闸口→审计→签认。验证架构泛化（同一条管线，不换架构）。

**Architecture:** Data RoomDetector（房名文本+墙体 bbox 启发式，Room 语义标注入叠加层）+ layout-lighting 处理器（网格 Insert 确定性生成，ISemanticAware 声明 Room）；灯具 skill 包（示例院标规则）；摘要含房间信息供 LLM 规划；基准 E2E 全链。

**Tech Stack:** .NET 8、ACadSharp 3.8.35、xUnit。

**Spec:** spec §4.3（语义重建/操作语义需求）、§6（skill）、§12 3d；第二期基准"建筑底图 AI 布灯"。

## Global Constraints

- **不编造标准**：GB 50034 照明设计标准未提供——间距/照度规则用示例院标（source.standard="示例院标（非真实）"），不引用未经核实的国标条款。
- 房间识别启发式保守：房名模式列表+语义 Annotation；区域=附近墙体线 bbox（10m 半径），无墙体→默认 6m×6m 显式标注 assumed；Room 标注 GeometryInference 0.6，人工 map 升级 1.0。
- layout-lighting 确定性：网格间距参数化，点位=房间区域内网格中心偏移；块不存在→失败结果；**ISemanticAware RequiredSemantics=[Room]**——闸口无 Room 语义拒执行（操作级，null handle 退化）。
- 布灯经 StepEngine 既有管线（审计/快照/闸口），无旁路；475 既有测试持续绿。
- TDD；feat: 前缀；git 署名环境变量；EOF 换行。

## 文件结构

```
src/CadAgent.Data/
  Semantic/RoomDetector.cs     房名检测+区域启发式
  Semantic/SemanticScanner.cs  加性：Room 标注集成
  DrawingSummarizer.cs         加性：房间段
  Handlers/LightingHandler.cs  layout-lighting 第七处理器
examples/skills/lighting/      灯具 skill（示例院标规则+别名）
tests/...
```

---

### Task 1: RoomDetector + 扫描集成 + 摘要房间段（Data）

**Files:**
- Create: `src/CadAgent.Data/Semantic/RoomDetector.cs`
- Modify: `src/CadAgent.Data/Semantic/SemanticScanner.cs`（Scan 加 Room 标注）、`src/CadAgent.Data/DrawingSummarizer.cs`（房间段）
- Test: `tests/CadAgent.Data.Tests/RoomDetectorTests.cs`

**Interfaces:**
- `sealed record RoomInfo(string Name, double X, double Y, double Width, double Depth, bool Assumed)`
- `static class RoomDetector`：
  - `IReadOnlyList<RoomInfo> Detect(DrawingSession session, SemanticOverlay overlay)`——房名候选：TextEntity 值匹配模式（客厅/卧室/主卧/次卧/厨房/卫生间/餐厅/办公室/会议室/走廊/阳台/书房/储物）或 overlay Room/Annotation 人工映射层上的文本；区域=距锚点 10000mm 内 Wall 类线（overlay Wall 标注或 WALL 层）的 bbox；无墙→6000×6000 Assumed=true；同名去重（ordinal）
  - `void AnnotateRooms(DrawingSession session, SemanticOverlay overlay)`——房名文本实体 UpsertAnnotation(Room, GeometryInference, 0.6, "房名模式: {name}")；已有人工标注跳过
- SemanticScanner.Scan：Detectors 后调 AnnotateRooms（加性）
- DrawingSummarizer：房间段 `房间: {name}@({x:0},{y:0}) {w}×{d}mm[assumed]`（RoomDetector.Detect 输出，ordinal 排序）

- [ ] **Step 1: 写失败测试**

```csharp
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class RoomDetectorTests : IDisposable
{
    private readonly string _path;
    public RoomDetectorTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("WALL"));
        var wall = doc.Layers.First(l => l.Name == "WALL");
        // 房间 6000×4000 墙体（锚点 3000,2000 附近）
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(6000, 0), Layer = wall });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 4000), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(0, 4000), Layer = wall });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(6000, 0), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(3000, 2000), Value = "客厅", Height = 5 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(50000, 50000), Value = "卧室", Height = 5 }); // 无墙→assumed
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Detect_RoomNames_BboxFromWalls_AssumedFallback()
    {
        var rooms = RoomDetector.Detect(DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal(2, rooms.Count);
        var living = rooms.First(r => r.Name == "客厅");
        Assert.Equal(6000, living.Width, 0);
        Assert.Equal(4000, living.Depth, 0);
        Assert.False(living.Assumed);
        var bed = rooms.First(r => r.Name == "卧室");
        Assert.True(bed.Assumed);
        Assert.Equal(6000, bed.Width, 0);
    }

    [Fact]
    public void AnnotateRooms_RoomSemantics_GeometryInference()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        RoomDetector.AnnotateRooms(session, overlay);
        Assert.Equal(2, overlay.Annotations.Count(a => a.Class == SemanticClass.Room));
        Assert.All(overlay.Annotations.Where(a => a.Class == SemanticClass.Room),
            a => Assert.Equal(Provenance.GeometryInference, a.Provenance));
    }

    [Fact]
    public void Scan_IncludesRoomAnnotations()
    {
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Room);
    }

    [Fact]
    public void Summary_IncludesRooms()
    {
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), null);
        Assert.Contains("客厅", s);
        Assert.Contains("房间", s);
        Assert.Contains("assumed", s);
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**（既有摘要测试不受影响——房间段加性；无房名图纸无房间段）。
- [ ] **Step 5: Commit** — `feat: room detection (name patterns + wall bbox heuristic) integrated into scan + summary`

---

### Task 2: layout-lighting 处理器（Data）

**Files:**
- Create: `src/CadAgent.Data/Handlers/LightingHandler.cs`
- Modify: `src/CadAgent.Data/Handlers/HandlerRegistry.cs`（第七处理器）
- Test: `tests/CadAgent.Data.Tests/LightingHandlerTests.cs`

**Interfaces:**
- `LayoutLightingHandler : IStepHandler, ISemanticAware`：Kind="layout-lighting"；RequiredSemantics=[SemanticClass.Room]
- params {x, y, width, depth, spacing, block, layer?}：网格点位=区域内间距 spacing 的网格（从 x+spacing/2 起，步长 spacing，不超 x+width）；每点 Insert(block)（块不存在→StepResult 失败"块不存在: {block}"）；layer 可选（默认块层）；返回 TargetHandle=首个 Insert 句柄；StepResult.Ok 含数量信息？（Error 空，TargetHandle 首句柄）
- 确定性：同参数同点位（测试钉死数量与坐标）

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class LightingHandlerTests : IDisposable
{
    private readonly string _path;
    public LightingHandlerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("LUM");
        block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
        doc.BlockRecords.Add(block);
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Layout_Grid_2x3_AtSpacing1000()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, JsonDocument.Parse(
            """{"x":0,"y":0,"width":2000,"depth":3000,"spacing":1000,"block":"LUM"}""").RootElement);
        Assert.True(r.Ok);
        var inserts = s.Doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(6, inserts.Count); // 2列×3行
        Assert.Contains(inserts, i => i.InsertPoint.X == 500 && i.InsertPoint.Y == 500);
        Assert.Contains(inserts, i => i.InsertPoint.X == 1500 && i.InsertPoint.Y == 2500);
    }

    [Fact]
    public void Layout_MissingBlock_Fails()
    {
        var s = DrawingSession.Load(_path);
        var r = new LayoutLightingHandler().Apply(s, JsonDocument.Parse(
            """{"x":0,"y":0,"width":2000,"depth":3000,"spacing":1000,"block":"NOPE"}""").RootElement);
        Assert.False(r.Ok);
        Assert.Contains("块不存在", r.Error);
    }

    [Fact]
    public void Handler_DeclaresRoomSemantics()
    {
        var h = new LayoutLightingHandler();
        Assert.Equal("layout-lighting", h.Kind);
        Assert.Contains(SemanticClass.Room, h.RequiredSemantics);
    }

    [Fact]
    public void Registry_ContainsSevenKinds()
    {
        Assert.Contains("layout-lighting", HandlerRegistry.All().Select(h => h.Kind));
        Assert.Equal(7, HandlerRegistry.All().Count);
    }
}
```

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: layout-lighting handler — deterministic grid placement with Room semantic requirement`

---

### Task 3: 灯具 skill 包 + 闸口实战测试

**Files:**
- Create: `examples/skills/lighting/skill.yaml`、`rules/spacing.yaml`、`aliases.yaml`
- Test: `tests/CadAgent.Cli.Tests/LightingGateTests.cs`

**Interfaces:**
- lighting skill：name 照明布置（示例院标）, version 1.0.0, discipline electrical；规则 R-LIGHT-SPACING（check type 复用 layer-name? 不——间距检查需新检查器？**裁定：第一 slice 规则用现有检查器能表达的**：灯具图层命名规则 layer-name charset（院标灯具层命名）+ 规则文本注明间距要求为人工审查项（检查器留后续）；source 示例院标）；aliases: E-LITE→Lighting（**已核实 SemanticClass.Lighting 存在于 SemanticModel.cs:3**，计划初稿"用 Annotation 承载"作废）。
- 闸口实战测试（Cli）：底图无 Room 语义→agent fake LLM 返回 layout-lighting 计划→**BlockedByGate**（orchestrator 执行失败→重规划→Escalated？闸口拒绝→tx 失败→重规划一次→仍拒→Escalated）→审计 semantic-gate-refused；scan+map 房名层→Room 语义人工确认→agent 同计划→Executed+Insert 生成。

- [ ] **Step 1: 写 skill YAML + 失败测试**

```csharp
using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

public class LightingGateTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"lightgate-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public LightingGateTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("ROOM-NAME"));
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(3000, 2000), Value = "客厅", Height = 5,
                Layer = doc.Layers.First(l => l.Name == "ROOM-NAME") });
            var block = new BlockRecord("LUM");
            block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
            doc.BlockRecords.Add(block);
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private sealed class LightingPlanClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                "{\"steps\":[{\"kind\":\"layout-lighting\",\"params\":{\"x\":0,\"y\":0,\"width\":6000,\"depth\":4000,\"spacing\":2000,\"block\":\"LUM\"}}]}",
                new TokenUsage(10, 5), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private void SetupProfile()
    {
        Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1", "--model", "m", "--key", "sk-lightkey12345678");
        Run("llm", "use", "--name", "p");
        CommandRouter.LlmClientFactory = (p, k) => new LightingPlanClient();
    }

    [Fact]
    public void NoRoomSemantics_GateRefuses_Escalated()
    {
        SetupProfile();
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "布灯", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Escalated", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-gate-refused");
        Assert.Empty(CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void RoomConfirmed_Executes_GridInserted()
    {
        SetupProfile();
        // scan 推断 Room（0.6）+ map 人工确认升 1.0
        Run("semantic", "scan", "--drawing", _drawing);
        Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing);
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "布灯", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        var inserts = CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(6, inserts.Count); // 6000/2000=3列 × 4000/2000=2行
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }
}
```
（注意：scan 的 Room 推断 0.6 < 闸口 0.8→需 map 人工确认；map --class Room 对 ROOM-NAME 层→AnnotateRooms 已标 Room 的实体被 map 升级？map 按层句柄 ApplyMapping→Room HumanConfirmed 1.0 ✓。无 map 时 scan 0.6 闸口拒——第一测试不 scan 直接 agent→overlay 不存在→gate null？**裁定：overlay 不存在→gate null→不检查→Executed！** 第一测试需 scan（无 map）使 overlay 存在但 Room 0.6<0.8→拒。修正第一测试：先 scan 再 agent。）

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现 skill YAML + 修正测试流程**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: lighting skill package + semantic gate live-fire tests (refuse without Room, execute with confirmation)`

---

### Task 4: 布灯基准 E2E + 承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/LightingBenchmarkE2ETests.cs`
- Modify: spec §12

**Interfaces:**
- 基准 E2E（离线 fake LLM，完整闭环）：建筑底图（墙体+房名+LUM 块）→scan（Room 推断+Wall 推断）→map 确认→摘要含房间→agent 布灯（fake LLM 计划从摘要取房间参数——测试用固定计划模拟）→网格 Insert→review signoff approve（reviewer override）→audit 全链（semantic-scan/semantic-map/llm-call/tx/step/review-approved）→export 降级 DXF（RuntimeFactory null）→审计包+验锚
- spec §12 追加 3d 完成记录：房间识别启发式（房名+墙体 bbox，assumed 标注）/layout-lighting 确定性网格+Room 语义闸口实战（无确认拒/有确认行）/灯具 skill 示例院标（GB 50034 未提供不编造）/基准闭环验证架构泛化；计划3 全部完成→计划4 视图就绪；诚实边界：房间区域启发式（精确多边形留后续）/照度计算未做（设计标准缺失）/灯具符号跨文档导入仍未做（块须已在图纸内，spec §12 既有 deferred 项）

- [ ] **Step 1: E2E 测试**
- [ ] **Step 2: 运行 + 全套件**
- [ ] **Step 3: spec §12**
- [ ] **Step 4: Commit** — `test: lighting benchmark end-to-end; docs: plan-3d completion, plan-4 readiness`

---

## 自审记录

- 闸口语义：overlay 不存在→gate null→不检查（既有裁定）；测试第一场景需 scan 使 overlay 存在（0.6<0.8 拒）——自审已修正。
- 网格数学：x+spacing/2 起，< x+width（6000/2000→500,2500,4500 三列；4000→500,2500 两行=6）✓ 测试钉死。
- 不编造标准：灯具规则示例院标；间距检查器留后续（规则文本注明人工审查项）。
- SemanticClass.Lighting 已存在（SemanticModel.cs:3 核实）——aliases E-LITE→Lighting。
- 475 既有测试保护；Registry 7 处理器——HandlerTests.cs:115 Registry_ContainsAllSixKinds 断言 exact 6-kind set，T2 sanctioned 更新为 7（改名 Registry_ContainsAllSevenKinds）。
- 闸口拒绝审计 kind="semantic-gate-refused"（StepEngine.cs:135 核实）；agent 命令 overlay 不存在→gate null（CommandRouter.Agent.cs:55）——第一测试必须先 scan。