# CAD 智能体终端 · 理解层 实现计划（计划 2a/4）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让内核"读懂图纸"：语义叠加层（置信度+来源）、图层约定推断、几何启发式（墙/轴网/门弧）、不明确图层人工纠偏 CLI、操作语义闸口——全部确定性、可审计、可复用。

**Architecture:** Core 新增语义模型与闸口接口（厂商无关）；Data 新增扫描器（约定库+几何启发式，ACadSharp）；Cli 新增 semantic 命令族并给 run 接语义闸口。LLM 推断留计划 3（Provenance 枚举已预留 LlmInference）。

**Tech Stack:** .NET 8、ACadSharp 3.8.35（CSMath/XYZ）、System.Text.Json、xUnit（沿用计划 1 骨架）。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §4.3 语义重建层、§4.4 图层纠偏、§4.1②③、§5 审计；承接 §12 遗留（Yellow 语义级拦截本计划落地操作级第一slice）。

## Global Constraints

- Core 不得引用 ACadSharp/厂商库；Data 引用 ACadSharp 3.8.35（CSMath 命名空间、XYZ 点、MText : Entity 非 TextEntity、Doc.Header.Version）。
- 语义标注必带 **置信度 + 来源**（Provenance）；人工确认=1.0 最高级；推断≠原生，证据字符串必填。
- 一切语义操作（scan/map/gate 拒绝）必经审计管线；新审计 kind：`semantic-scan`、`semantic-map`、`semantic-gate-refused`。
- 叠加层随项目持久化（`.cadagent/semantic.json`）、版本化（每次写入 Version+1）、一次确认后续复用。
- 未映射图层保持禁触语义依赖操作；几何级操作（不声明语义需求的处理器）不受闸口影响。
- 操作语义检查第一 slice 为**操作级**（所需语义类在叠加层中存在且置信度≥阈值）；实体级区域覆盖检查留计划 3/4（显式记录，不假装已做）。
- 约定库＝天正标准图层名+常见别名（事实来源：调查）；**不冒充 GB 条文权威**，不引用未经核实的条款号。
- 启发式阈值集中 configurable（WallThicknessRange 等），默认值写明依据（mm，1:1 绘图单位）。
- TDD；测试输出 pristine；feat:/fix: 前缀提交；git 署名用环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（**不改 git config**）。
- CLI UTF-8；Windows GBK 终端不在 shell 回显中文；源文件 UTF-8。
- 计划 1 契约不得破坏：IDrawingSession、AuditEntry/HashChain（黄金向量）、StepEngine 现有公共签名（本计划仅**加性**扩展）、现有 77 测试必须持续全绿。

## 文件结构

```
src/CadAgent.Core/
  SemanticModel.cs        SemanticClass / Provenance / SemanticAnnotation / LayerMapping / AmbiguousLayer
  SemanticOverlay.cs      叠加层：Annotations/Mappings/Version + Load/Save(JSON) + ConfirmMapping/ApplyMapping
  SemanticGate.cs         ISemanticAware / ISemanticGate / GateDecision
  StepEngine.cs           加性：ctor 可选 ISemanticGate? 参数 + 步前闸口检查
src/CadAgent.Data/
  Semantic/ConventionLibrary.cs   天正/常见图层名→语义类映射（含别名）
  Semantic/SemanticScanner.cs     组合扫描：图层推断+几何启发式+不明确清单
  Semantic/WallPairDetector.cs    平行线对→墙
  Semantic/AxisDetector.cs        轴号圆圈+数字文本→轴网
  Semantic/DoorArcDetector.cs     门宽半径+~90°弧→门
  Semantic/SemanticGate.cs        SemanticGate 实现（操作级检查）
  Handlers/*                    五个处理器加 ISemanticAware（add-line/add-text 无需求；change-layer 无需求；delete/move 无需求——第一slice全部无需求，闸口机制由测试用 FakeAwareHandler 验证；真实语义需求随计划3布灯处理器引入）
src/CadAgent.Cli/
  CommandRouter.cs        加性：semantic scan/show/map 命令 + run 接闸口
tests/...                   各组件测试 + 端到端
```

**处理器语义需求说明（诚实边界）**：计划 1 的五个处理器都是几何级操作，不声明语义需求——闸口机制本计划建成并用测试处理器验证，真实需求声明随计划 3 的布灯等语义级处理器引入。这不是缺口而是分期：spec §4.3"操作声明语义需求"的机制先行。

---

### Task 1: 语义模型与叠加层持久化（Core）

**Files:**
- Create: `src/CadAgent.Core/SemanticModel.cs`、`src/CadAgent.Core/SemanticOverlay.cs`
- Test: `tests/CadAgent.Core.Tests/SemanticOverlayTests.cs`

**Interfaces:**
- Consumes: 无（纯 BCL + System.Text.Json）
- Produces:
  - `enum SemanticClass { Unknown, Wall, Door, Window, Column, Axis, Annotation, Room, Auxiliary }`
  - `enum Provenance { NativeObject, LayerInference, GeometryInference, LlmInference, HumanConfirmed }`
  - `sealed record SemanticAnnotation(string TargetHandle, SemanticClass Class, Provenance Provenance, double Confidence, string Evidence)`
  - `sealed record LayerMapping(string LayerName, SemanticClass Class, string ConfirmedBy, DateTimeOffset Ts)`（Provenance 恒 HumanConfirmed，不存字段）
  - `sealed record AmbiguousLayer(string LayerName, int EntityCount, IReadOnlyList<string> SampleHandles, string NameHint)`
  - `sealed class SemanticOverlay`：`List<SemanticAnnotation> Annotations`、`List<LayerMapping> Mappings`、`int Version`；`static SemanticOverlay Load(string path)`（不存在→空 Version 0）；`void Save(string path)`（Version+1，JSON 缩进）；`void UpsertAnnotation(SemanticAnnotation a)`（同 TargetHandle+Provenance 替换）；`void ConfirmMapping(LayerMapping m)`（同 LayerName 替换）；`void ApplyMappingToAnnotations(string layerName, SemanticClass cls, IEnumerable<string> handlesOnLayer)`（把该层实体标注升级为 HumanConfirmed/1.0，证据"人工确认图层映射"）

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class SemanticOverlayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sem-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyVersionZero()
    {
        var o = SemanticOverlay.Load(_path);
        Assert.Equal(0, o.Version);
        Assert.Empty(o.Annotations);
        Assert.Empty(o.Mappings);
    }

    [Fact]
    public void Save_IncrementsVersion_AndRoundTrips()
    {
        var o = SemanticOverlay.Load(_path);
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.6, "平行线对 d=200"));
        o.Save(_path);
        var loaded = SemanticOverlay.Load(_path);
        Assert.Equal(1, loaded.Version);
        Assert.Single(loaded.Annotations);
        Assert.Equal("平行线对 d=200", loaded.Annotations[0].Evidence);
        loaded.Save(_path);
        Assert.Equal(2, SemanticOverlay.Load(_path).Version);
    }

    [Fact]
    public void Upsert_SameHandleAndProvenance_Replaces()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.6, "e1"));
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.7, "e2"));
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.LayerInference, 0.8, "e3"));
        Assert.Equal(2, o.Annotations.Count);
    }

    [Fact]
    public void ConfirmMapping_ReplacesSameLayer()
    {
        var o = new SemanticOverlay();
        o.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Wall, "u1", DateTimeOffset.UnixEpoch));
        o.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Column, "u2", DateTimeOffset.UnixEpoch));
        Assert.Single(o.Mappings);
        Assert.Equal(SemanticClass.Column, o.Mappings[0].Class);
    }

    [Fact]
    public void ApplyMapping_UpgradesAnnotationsToHumanConfirmed()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Unknown, Provenance.LayerInference, 0.3, "未识别"));
        o.ApplyMappingToAnnotations("W-新墙", SemanticClass.Wall, new[] { "h1" });
        var a = o.Annotations.Single(x => x.TargetHandle == "h1");
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
        Assert.Equal(1.0, a.Confidence);
    }
}
```

- [ ] **Step 2: 运行验证失败** — `dotnet test --filter FullyQualifiedName~SemanticOverlayTests`，Expected: 编译失败。

- [ ] **Step 3: 实现**

`SemanticModel.cs`：上述 enum/record 原样（namespace CadAgent.Core）。

`SemanticOverlay.cs`：
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadAgent.Core;

public sealed class SemanticOverlay
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<SemanticAnnotation> Annotations { get; set; } = new();
    public List<LayerMapping> Mappings { get; set; } = new();
    public int Version { get; set; }

    public static SemanticOverlay Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<SemanticOverlay>(File.ReadAllText(path), JsonOpts) ?? new SemanticOverlay()
            : new SemanticOverlay();

    public void Save(string path)
    {
        Version++;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public void UpsertAnnotation(SemanticAnnotation a)
    {
        int i = Annotations.FindIndex(x => x.TargetHandle == a.TargetHandle && x.Provenance == a.Provenance);
        if (i >= 0) Annotations[i] = a; else Annotations.Add(a);
    }

    public void ConfirmMapping(LayerMapping m)
    {
        int i = Mappings.FindIndex(x => string.Equals(x.LayerName, m.LayerName, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Mappings[i] = m; else Mappings.Add(m);
    }

    public void ApplyMappingToAnnotations(string layerName, SemanticClass cls, IEnumerable<string> handlesOnLayer)
    {
        foreach (string h in handlesOnLayer)
            UpsertAnnotation(new SemanticAnnotation(h, cls, Provenance.HumanConfirmed, 1.0, $"人工确认图层映射: {layerName}"));
    }
}
```

- [ ] **Step 4: 运行验证通过** — 5 条 PASS。
- [ ] **Step 5: Commit** — `feat: semantic overlay model with provenance, confidence, versioned JSON persistence`

---

### Task 2: 约定库与图层推断（Data）

**Files:**
- Create: `src/CadAgent.Data/Semantic/ConventionLibrary.cs`
- Test: `tests/CadAgent.Data.Tests/ConventionLibraryTests.cs`

**Interfaces:**
- Consumes: `SemanticClass`（Task 1）、`DrawingSession`（计划 1）
- Produces:
  - `static class ConventionLibrary`：`SemanticClass ClassForLayer(string layerName)`（命中→类，未命中→Unknown）；`IReadOnlyDictionary<string, SemanticClass> KnownLayers`（天正标准名+常见别名，大小写不敏感）；`string HintForLayer(string layerName)`（名称线索：含 wall/墙→"疑似墙体"等，无→""）
  - `static class LayerInference`：`void Annotate(DrawingSession session, SemanticOverlay overlay)`——对每个实体：图层命中约定库或已有人工映射→UpsertAnnotation(LayerInference, 0.8, "图层约定: {layer}")；人工映射优先（跳过已有 HumanConfirmed 的实体）；未命中图层不标注（进不明确清单由 Scanner 负责）

约定库内容（事实：天正标准图层名，调查来源；别名常见自定义）：
```
WALL→Wall, WALL_NEW→Wall, 墙→Wall, 墙体→Wall, W-墙→Wall
WINDOW→Window, 窗→Window, DOOR→Door, 门→Door
COLUMN→Column, 柱→Column, AXIS→Axis, 轴网→Axis, AXIS_TEXT→Axis
PUB_DIM→Annotation, PUB_TEXT→Annotation, DIM→Annotation, 标注→Annotation
STAIR→Auxiliary, 楼梯→Auxiliary, FURNITURE→Auxiliary, 家具→Auxiliary
```

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class ConventionLibraryTests : IDisposable
{
    private readonly string _path;
    public ConventionLibraryTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var wallLayer = new ACadSharp.Tables.Layer("WALL");
        doc.Layers.Add(wallLayer);
        var customLayer = new ACadSharp.Tables.Layer("W-新墙");
        doc.Layers.Add(customLayer);
        doc.Entities.Add(new ACadSharp.Entities.Line { StartPoint = (ACadSharp.CSMath.XYZ)new ACadSharp.CSMath.XY(0, 0), EndPoint = (ACadSharp.CSMath.XYZ)new ACadSharp.CSMath.XY(100, 0), Layer = wallLayer });
        doc.Entities.Add(new ACadSharp.Entities.Line { StartPoint = (ACadSharp.CSMath.XYZ)new ACadSharp.CSMath.XY(0, 200), EndPoint = (ACadSharp.CSMath.XYZ)new ACadSharp.CSMath.XY(100, 200), Layer = customLayer });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void ClassForLayer_KnownNames_CaseInsensitive()
    {
        Assert.Equal(SemanticClass.Wall, ConventionLibrary.ClassForLayer("wall"));
        Assert.Equal(SemanticClass.Axis, ConventionLibrary.ClassForLayer("AXIS"));
        Assert.Equal(SemanticClass.Annotation, ConventionLibrary.ClassForLayer("PUB_DIM"));
        Assert.Equal(SemanticClass.Unknown, ConventionLibrary.ClassForLayer("随便画的"));
    }

    [Fact]
    public void HintForLayer_NameClues()
    {
        Assert.Contains("墙", ConventionLibrary.HintForLayer("W-新墙"));
        Assert.Equal("", ConventionLibrary.HintForLayer("xyz123"));
    }

    [Fact]
    public void LayerInference_AnnotatesKnownLayer_SkipsUnknown()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        LayerInference.Annotate(session, overlay);
        var wallEntity = session.Summarize().Entities.First(e => e.Layer == "WALL");
        var a = overlay.Annotations.Single(x => x.TargetHandle == wallEntity.Handle);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.LayerInference, a.Provenance);
        Assert.Equal(0.8, a.Confidence);
        Assert.DoesNotContain(overlay.Annotations, x => x.Evidence.Contains("W-新墙"));
    }

    [Fact]
    public void LayerInference_HumanMappingTakesPrecedence()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        overlay.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        LayerInference.Annotate(session, overlay);
        var custom = session.Summarize().Entities.First(e => e.Layer == "W-新墙");
        var a = overlay.Annotations.Single(x => x.TargetHandle == custom.Handle);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
        Assert.Equal(1.0, a.Confidence);
    }
}
```
（CSMath 类型引用以实际包为准：计划 1 已确立 `using CSMath;` + `(XYZ)new XY(...)` 模式，测试可改用 using 简化。）

- [ ] **Step 2: 运行验证失败** — 编译失败。
- [ ] **Step 3: 实现** — ConventionLibrary（OrdinalIgnoreCase 字典 + HintForLayer 关键词表 wall/墙/door/门/window/窗/axis/轴/dim/标注）；LayerInference 遍历 `session.Doc.Entities`，先查 overlay.Mappings（人工映射→HumanConfirmed 1.0），再查约定库（LayerInference 0.8），已有人工标注的实体跳过；句柄 `e.Handle.ToString()`，图层 `e.Layer?.Name ?? "0"`。
- [ ] **Step 4: 运行验证通过** — 4 条 PASS。
- [ ] **Step 5: Commit** — `feat: TArch layer convention library + layer inference with human-mapping precedence`

---

### Task 3: 墙体平行线对启发式（Data）

**Files:**
- Create: `src/CadAgent.Data/Semantic/WallPairDetector.cs`
- Test: `tests/CadAgent.Data.Tests/WallPairDetectorTests.cs`

**Interfaces:**
- Consumes: `DrawingSession`、`SemanticOverlay`、`SemanticAnnotation`
- Produces: `static class WallPairDetector`：`void Detect(DrawingSession session, SemanticOverlay overlay, double minThickness = 100, double maxThickness = 400, double minOverlap = 500)`——同向（角度差 mod 180 ≤ 1°）线对，垂直距 d∈[min,max] 且投影重叠≥minOverlap→两线各 UpsertAnnotation(Wall, GeometryInference, 0.6, $"平行线对 d={d:F0} overlap={ov:F0}")。已有更高置信度标注（HumanConfirmed/LayerInference≥0.8）不覆盖。

算法（实现者按此写，O(n²) 第一 slice，空间索引留后续）：
1. 收集 Line，方向向量归一化，角度 = Atan2(dy,dx) 规整到 [0,180)。
2. 按角度分桶（1° 容差：排序后相邻合并）。
3. 桶内两两：d = |cross(unitDir, p2start - p1start)|；重叠 = 两线在方向轴上投影区间交集长度。
4. 命中→标注两线（跳过已有置信度 > 0.6 的同实体标注）。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class WallPairDetectorTests : IDisposable
{
    private static Line L(double x1, double y1, double x2, double y2) =>
        new() { StartPoint = (XYZ)new XY(x1, y1), EndPoint = (XYZ)new XY(x2, y2) };

    private readonly string _path;
    public WallPairDetectorTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(L(0, 0, 2000, 0));       // 墙线1
        doc.Entities.Add(L(0, 200, 2000, 200));   // 墙线2（d=200，重叠2000）
        doc.Entities.Add(L(0, 5000, 300, 5000));  // 短线（重叠300<500，不命中）
        doc.Entities.Add(L(0, 5100, 300, 5100));
        doc.Entities.Add(L(0, 0, 0, 2000));       // 垂直线（无同向对）
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void ParallelPair_WallThickness_AnnotatesBoth()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        WallPairDetector.Detect(session, overlay);
        var handles = session.Summarize().Entities.Select(e => e.Handle).ToList();
        var wallAnnotations = overlay.Annotations.Where(a => a.Class == SemanticClass.Wall).ToList();
        Assert.Equal(2, wallAnnotations.Count);
        Assert.All(wallAnnotations, a =>
        {
            Assert.Equal(Provenance.GeometryInference, a.Provenance);
            Assert.Equal(0.6, a.Confidence);
            Assert.Contains("d=200", a.Evidence);
        });
    }

    [Fact]
    public void ShortOverlap_NotAnnotated()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        WallPairDetector.Detect(session, overlay);
        Assert.Equal(2, overlay.Annotations.Count); // 只有那对长墙线
    }

    [Fact]
    public void HigherConfidenceExisting_NotOverwritten()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        string h = session.Summarize().Entities[0].Handle;
        overlay.UpsertAnnotation(new SemanticAnnotation(h, SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        WallPairDetector.Detect(session, overlay);
        Assert.Equal(1.0, overlay.Annotations.Single(a => a.TargetHandle == h && a.Provenance == Provenance.HumanConfirmed).Confidence);
    }
}
```

- [ ] **Step 2: 运行验证失败** — 编译失败。
- [ ] **Step 3: 实现** — 按上述算法；角度容差比较用 `Math.Abs(a1-a2) <= 1.0 || Math.Abs(a1-a2) >= 179.0`（mod 180 环绕）；cross/投影用双精度；证据字符串格式与测试断言一致（`d={d:F0}`）。
- [ ] **Step 4: 运行验证通过** — 3 条 PASS。
- [ ] **Step 5: Commit** — `feat: wall parallel-pair heuristic with configurable thickness/overlap thresholds`

---

### Task 4: 轴网与门弧启发式（Data）

**Files:**
- Create: `src/CadAgent.Data/Semantic/AxisDetector.cs`、`src/CadAgent.Data/Semantic/DoorArcDetector.cs`
- Test: `tests/CadAgent.Data.Tests/AxisDoorDetectorTests.cs`

**Interfaces:**
- Consumes: 同 Task 3
- Produces:
  - `static class AxisDetector`：`void Detect(DrawingSession session, SemanticOverlay overlay, double minRadius = 100, double maxRadius = 1000)`——Circle 半径∈[min,max] 且存在 TextEntity 内容为纯数字（1-2 位）且插入点距圆心≤半径×1.5→圆与文本各标注 Axis（GeometryInference, 0.6, "轴号圆圈 r={r:F0} 编号={text}"）。
  - `static class DoorArcDetector`：`void Detect(DrawingSession session, SemanticOverlay overlay, double minRadius = 500, double maxRadius = 1500, double sweepToleranceDeg = 15)`——Arc 半径∈[min,max] 且扫掠角≈90°（±容差）→标注 Door（GeometryInference, 0.5, "门弧 r={r:F0} sweep={s:F0}°"）。ACadSharp Arc 角度单位以包 XML 文档核实（StartAngle/EndAngle；扫掠角=(End-Start) 规整到 [0,360)），测试用构造弧钉死行为。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class AxisDoorDetectorTests : IDisposable
{
    private readonly string _path;
    public AxisDoorDetectorTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Circle { Center = (XYZ)new XY(0, 0), Radius = 400 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "1", Height = 300 });
        doc.Entities.Add(new Circle { Center = (XYZ)new XY(5000, 0), Radius = 400 }); // 无数字文本→不命中
        doc.Entities.Add(new Arc { Center = (XYZ)new XY(1000, 0), Radius = 900, StartAngle = 0, EndAngle = Math.PI / 2 }); // 90°门弧（角度单位按实现核实调整）
        doc.Entities.Add(new Arc { Center = (XYZ)new XY(2000, 0), Radius = 900, StartAngle = 0, EndAngle = Math.PI });     // 180°→不命中
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void AxisCircle_WithNumberText_AnnotatesBoth()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        AxisDetector.Detect(session, overlay);
        var axis = overlay.Annotations.Where(a => a.Class == SemanticClass.Axis).ToList();
        Assert.Equal(2, axis.Count); // 圆+文本
        Assert.All(axis, a => Assert.Contains("编号=1", a.Evidence));
    }

    [Fact]
    public void DoorArc_QuarterCircle_Annotates()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        DoorArcDetector.Detect(session, overlay);
        var doors = overlay.Annotations.Where(a => a.Class == SemanticClass.Door).ToList();
        Assert.Single(doors);
        Assert.Equal(0.5, doors[0].Confidence);
    }
}
```
（若 ACadSharp Arc 角度为度而非弧度， fixture 的 EndAngle 改为 90/180——以 XML 文档为准，测试意图不变。）

- [ ] **Step 2: 运行验证失败** — 编译失败。
- [ ] **Step 3: 实现** — 按契约；数字判定 `text.Value.Trim()` 匹配 `^\d{1,2}$`；扫掠角规整处理跨 0 情况；高置信度不覆盖规则同 Task 3。
- [ ] **Step 4: 运行验证通过** — 2 条 PASS。
- [ ] **Step 5: Commit** — `feat: axis-grid (circle+number) and door-arc heuristics`

---

### Task 5: 扫描器组合与不明确清单（Data）

**Files:**
- Create: `src/CadAgent.Data/Semantic/SemanticScanner.cs`
- Test: `tests/CadAgent.Data.Tests/SemanticScannerTests.cs`

**Interfaces:**
- Consumes: Task 2-4 全部
- Produces:
  - `sealed record ScanResult(SemanticOverlay Overlay, IReadOnlyList<AmbiguousLayer> Ambiguous)`
  - `static class SemanticScanner`：`ScanResult Scan(DrawingSession session, SemanticOverlay? existing = null)`——
    1. overlay = existing ?? new（保留人工映射与既有标注）
    2. LayerInference.Annotate（人工映射优先）
    3. WallPairDetector / AxisDetector / DoorArcDetector 依次 Detect
    4. 不明确清单：图层未命中约定库且无人工映射→AmbiguousLayer（EntityCount、SampleHandles 前 5 个、NameHint=ConventionLibrary.HintForLayer）；按实体数降序

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class SemanticScannerTests : IDisposable
{
    private readonly string _path;
    public SemanticScannerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("WALL"));
        doc.Layers.Add(new Layer("W-新墙"));
        doc.Layers.Add(new Layer("随便"));
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = doc.Layers.First(l => l.Name == "WALL") });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 200), EndPoint = (XYZ)new XY(2000, 200), Layer = doc.Layers.First(l => l.Name == "W-新墙") });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(10, 10), Layer = doc.Layers.First(l => l.Name == "随便") });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Scan_ProducesAnnotations_AndAmbiguousList()
    {
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.LayerInference);
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.GeometryInference);
        var names = result.Ambiguous.Select(a => a.LayerName).ToList();
        Assert.Contains("W-新墙", names);
        Assert.Contains("随便", names);
        Assert.DoesNotContain("WALL", names);
        Assert.Equal(1, result.Ambiguous.First(a => a.LayerName == "随便").EntityCount);
    }

    [Fact]
    public void Scan_WithConfirmedMapping_NoLongerAmbiguous()
    {
        var existing = new SemanticOverlay();
        existing.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        var result = SemanticScanner.Scan(DrawingSession.Load(_path), existing);
        Assert.DoesNotContain("W-新墙", result.Ambiguous.Select(a => a.LayerName));
        Assert.Contains(result.Overlay.Annotations, a => a.Provenance == Provenance.HumanConfirmed);
    }
}
```

- [ ] **Step 2: 运行验证失败** — 编译失败。
- [ ] **Step 3: 实现** — 按契约组合；注意 LayerInference 已处理人工映射标注，Scanner 的不明确判定与之一致（查 Mappings + KnownLayers）。
- [ ] **Step 4: 运行验证通过** — 2 条 PASS。
- [ ] **Step 5: Commit** — `feat: composed semantic scanner with ambiguous-layer evidence list`

---

### Task 6: CLI semantic 命令族（Cli）

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（加性：semantic 分支 + 三个子命令）
- Test: `tests/CadAgent.Cli.Tests/SemanticCommandTests.cs`

**Interfaces:**
- Consumes: Task 1-5 全部 + 计划 1 审计/身份
- Produces:
  - `semantic scan --drawing <file> [--project dir]`：Load→Scan(existing overlay)→Save overlay→审计 `semantic-scan`（paramsJson={annotationCount, ambiguousCount, version}）→stdout JSON {version, annotations, ambiguous}→exit 0；Load 失败→审计 `intake-rejected` 复用计划 1 模式+exit 1
  - `semantic show [--project dir]`：读 overlay→stdout JSON 摘要→exit 0（无 overlay→空摘要 exit 0）
  - `semantic map --layer <name> --class <类名> [--project dir]`：类名 Enum.Parse<SemanticClass>（非法→stderr+exit 1）→ConfirmMapping(ConfirmedBy=当前账号)→若 overlay 有该层实体标注则 ApplyMapping（实体句柄从最近 scan 的 overlay 标注取；无 overlay→提示先 scan，exit 1）→Save→审计 `semantic-map`（paramsJson={layer, class, operator}）→exit 0
  - 项目约定沿用：`.cadagent/semantic.json`

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class SemanticCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"semcli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public SemanticCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-自定义"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (ACadSharp.CSMath.XYZ)new ACadSharp.CSMath.XY(0, 0),
                EndPoint = (ACadSharp.CSMath.XYZ)new ACadSharp.CSMath.XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "W-自定义")
            });
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void Scan_WritesOverlay_Audits_ListsAmbiguous()
    {
        var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("W-自定义", stdout);
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Map_ConfirmsLayer_RemovesAmbiguous_Audits()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        var (code, _, _) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(0, code);
        var (_, showOut, _) = Run("semantic", "show");
        Assert.Contains("HumanConfirmed", showOut);
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Map_InvalidClass_ExitOne()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        var (code, _, stderr) = Run("semantic", "map", "--layer", "W-自定义", "--class", "NotAClass", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Map_WithoutScan_ExitOne()
    {
        var (code, _, stderr) = Run("semantic", "map", "--layer", "X", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.Contains("scan", stderr);
    }
}
```

- [ ] **Step 2: 运行验证失败** — 编译失败/命令未识别。
- [ ] **Step 3: 实现** — CommandRouter 加 `case "semantic"` 分支（子命令 positional[1]，非法子命令→stderr+exit 1，顺带落实计划 1 遗留的子命令校验仅对 semantic 族）；审计经 OpenAudit+Identity（沿用计划 1 模式）；map 的实体句柄来源：overlay.Annotations 中 Evidence 含该图层或重新 Load 图纸取该层实体（采用后者：Load drawing 取 Summarize 该层句柄，map 命令也需 --drawing？**裁定：map 需要 --drawing 选项**以取实体句柄，测试相应补 `--drawing _drawing`——实现者按此调整测试 Step 1 的 map 调用）。
- [ ] **Step 4: 运行验证通过** — 4 条 PASS + 计划 1 全部测试持续绿。
- [ ] **Step 5: Commit** — `feat: CLI semantic scan/show/map with audit and human-confirmed mapping`

---

### Task 7: 操作语义闸口（Core 加性 + Data + Cli 接线）

**Files:**
- Create: `src/CadAgent.Core/SemanticGate.cs`、`src/CadAgent.Data/Semantic/SemanticGate.cs`
- Modify: `src/CadAgent.Core/StepEngine.cs`（加性：ctor 尾部可选参数 `ISemanticGate? semanticGate = null`；步前检查）
- Test: `tests/CadAgent.Core.Tests/SemanticGateTests.cs`、`tests/CadAgent.Cli.Tests/RunGateTests.cs`

**Interfaces:**
- Consumes: SemanticOverlay（Task 1）、StepEngine（计划 1）
- Produces:
  - `interface ISemanticAware { IReadOnlyList<SemanticClass> RequiredSemantics { get; } }`（处理器可选实现）
  - `sealed record GateDecision(bool Allowed, string Reason)`
  - `interface ISemanticGate { GateDecision Check(IReadOnlyList<SemanticClass> required); }`
  - `CadAgent.Data.Semantic.SemanticGate : ISemanticGate`：ctor `(SemanticOverlay overlay, double minConfidence = 0.8)`——每个所需类：overlay.Annotations 存在该类且 Confidence≥minConfidence→允许；否则拒绝 Reason=$"缺少语义 {cls}（需置信度≥{minConfidence}，请先 semantic scan/map 确认）"
  - StepEngine.Run：每步执行前，若 `_semanticGate != null && handler is ISemanticAware aware && aware.RequiredSemantics.Count > 0`→Check；拒绝→审计 `semantic-gate-refused`（target=step.Kind, paramsJson={required, reason}）+返回 TxResult(false, done, reason)，**不执行该步、不 SaveWorking**
  - CLI run：overlay 文件存在→加载并注入 SemanticGate；不存在→不注入（几何级流程不受影响，诚实降级）

- [ ] **Step 1: 写失败测试**

Core 测试（完整代码；FakeSession/FixedIdentity 为本文件内最小实现）：
```csharp
using System.Text.Json;

namespace CadAgent.Core.Tests;

public class SemanticGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"semgate-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly FakeSession _session;

    public SemanticGateTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        _snapshots = new SnapshotStore(Path.Combine(_dir, "snapshots"));
        _session = new FakeSession(Path.Combine(_dir, "drawing.dxf"));
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
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

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private sealed class AwareHandler : IStepHandler, ISemanticAware
    {
        public string Kind => "need-wall";
        public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Wall };
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private sealed class PlainHandler : IStepHandler
    {
        public string Kind => "plain";
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private sealed class OverlayGate : ISemanticGate
    {
        private readonly SemanticOverlay _o;
        private readonly double _min;
        public OverlayGate(SemanticOverlay o, double min = 0.8) { _o = o; _min = min; }
        public GateDecision Check(IReadOnlyList<SemanticClass> required)
        {
            foreach (var c in required)
                if (!_o.Annotations.Any(a => a.Class == c && a.Confidence >= _min))
                    return new GateDecision(false, $"缺少语义 {c}");
            return new GateDecision(true, "");
        }
    }

    private static StepPlan Plan(string kind) =>
        new("p1", new[] { new StepSpec(kind, JsonDocument.Parse("{}").RootElement) });

    private StepEngine Engine(ISemanticGate? gate, params IStepHandler[] handlers) =>
        new(_audit, _snapshots, new FixedIdentity(new OperatorIdentity("TEST\\u", Role.Draw)),
            new RoleChecker(), handlers, gate);

    [Fact]
    public void Gate_Refuses_WhenSemanticsMissing_AuditsAndHalts()
    {
        var result = Engine(new OverlayGate(new SemanticOverlay()), new AwareHandler())
            .Run(Plan("need-wall"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Contains("Wall", result.Error);
        Assert.Equal("semantic-gate-refused", _audit.ReadAll()[^1].Entry.Kind);
        Assert.Equal(0, _session.SaveCount);
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Gate_Allows_WithHumanConfirmed()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        var result = Engine(new OverlayGate(o), new AwareHandler()).Run(Plan("need-wall"), _session, "s1");
        Assert.True(result.Ok);
    }

    [Fact]
    public void Gate_Refuses_LowConfidence()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.6, "推断"));
        var result = Engine(new OverlayGate(o), new AwareHandler()).Run(Plan("need-wall"), _session, "s1");
        Assert.False(result.Ok);
    }

    [Fact]
    public void NullGate_Additive_DoesNotBlock()
    {
        var result = Engine(null, new AwareHandler()).Run(Plan("need-wall"), _session, "s1");
        Assert.True(result.Ok);
    }

    [Fact]
    public void NonAwareHandler_UnaffectedByGate()
    {
        var result = Engine(new OverlayGate(new SemanticOverlay()), new PlainHandler())
            .Run(Plan("plain"), _session, "s1");
        Assert.True(result.Ok);
    }
}
```

Data 真实闸口测试（`tests/CadAgent.Data.Tests/SemanticGateImplTests.cs`）：
```csharp
using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class SemanticGateImplTests
{
    [Fact]
    public void RealGate_AllowsHumanConfirmed_RefusesMissing()
    {
        var o = new SemanticOverlay();
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.False(gate.Check(new[] { SemanticClass.Wall }).Allowed);
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        Assert.True(gate.Check(new[] { SemanticClass.Wall }).Allowed);
        Assert.Contains("Wall", gate.Check(new[] { SemanticClass.Door }).Reason);
    }
}
```

Cli 测试（`tests/CadAgent.Cli.Tests/RunGateTests.cs`）：
```csharp
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class RunGateTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"rungate-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public RunGateTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    [Fact]
    public void Run_WithOverlayPresent_GeometricPlanStillSucceeds()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        CommandRouter.Run(new[] { "semantic", "scan", "--drawing", _drawing, "--project", _project }, outW, errW);
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var outW2 = new StringWriter(); var errW2 = new StringWriter();
        int code = CommandRouter.Run(new[] { "run", planPath, "--drawing", _drawing, "--project", _project }, outW2, errW2);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", outW2.ToString().Replace(" ", ""));
    }
}
```
（计划 1 处理器均无 RequiredSemantics——真实语义需求处理器的端到端留计划 3 布灯。）

- [ ] **Step 2: 运行验证失败** — 编译失败。
- [ ] **Step 3: 实现** — 按契约；StepEngine ctor 加可选参数（现有调用零改动）；Run 步前检查插在 handler 查找之后、Apply 之前；拒绝审计 Source="plan"、Kind="semantic-gate-refused"。
- [ ] **Step 4: 运行验证通过** — 新测试 PASS + 全套件绿（77+新增）。
- [ ] **Step 5: Commit** — `feat: operation-level semantic gate (additive engine hook, audit refusal, CLI wiring)`

---

### Task 8: 端到端与承接记录

**Files:**
- Test: `tests/CadAgent.Cli.Tests/SemanticE2ETests.cs`
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md`（§12 追加 2a 完成记录与遗留）

**Interfaces:**
- Consumes: 全部前序
- Produces: 完整闭环测试：构造图（WALL 层两平行线 d=200 + 自定义层线 + 轴号圆+数字）→ scan（断言：Wall 推断×2 来源、Axis×2、自定义层进不明确清单）→ map 自定义层为 Wall（断言 HumanConfirmed 1.0、不明确清单移除）→ 再 scan 复用映射（断言版本递增、映射保留）→ audit verify 绿 → run 几何计划成功（闸口零影响）

- [ ] **Step 1: 写端到端测试**

```csharp
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

public class SemanticE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"seme2e-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public SemanticE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("WALL"));
            doc.Layers.Add(new Layer("W-自定义"));
            var wall = doc.Layers.First(l => l.Name == "WALL");
            var custom = doc.Layers.First(l => l.Name == "W-自定义");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 200), EndPoint = (XYZ)new XY(2000, 200), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 3000), EndPoint = (XYZ)new XY(500, 3000), Layer = custom });
            doc.Entities.Add(new Circle { Center = (XYZ)new XY(5000, 0), Radius = 400 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(5000, 0), Value = "1", Height = 300 });
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void FullLoop_Scan_Map_Rescan_Run_AuditGreen()
    {
        var (scanCode, scanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        Assert.Contains("Wall", scanOut);
        Assert.Contains("Axis", scanOut);
        Assert.Contains("W-自定义", scanOut);

        var (mapCode, _, _) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(0, mapCode);

        var (_, showOut, _) = Run("semantic", "show");
        Assert.Contains("HumanConfirmed", showOut);

        var (rescanCode, rescanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, rescanCode);
        using var rescanDoc = System.Text.Json.JsonDocument.Parse(rescanOut);
        Assert.Equal(2, rescanDoc.RootElement.GetProperty("version").GetInt32());
        var ambiguous = rescanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
            .Select(a => a.GetProperty("layerName").GetString()).ToList();
        Assert.DoesNotContain("W-自定义", ambiguous);

        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);

        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, runCode);
        Assert.Contains("\"ok\":true", runOut.Replace(" ", ""));
    }
}
```
（JSON 属性名以 Task 6 实际序列化为准——camelCase；若 Task 6 用其他命名，同步调整断言，意图不变。）
- [ ] **Step 2: 运行验证失败/通过**（若前序正确应直接绿；红则修）
- [ ] **Step 3: 全套件运行** — `dotnet test` 全绿
- [ ] **Step 4: 更新 spec §12** — 追加：2a 完成（操作级语义闸口已落地；实体级区域覆盖检查留计划3/4；LLM 推断留计划3；启发式 O(n²) 空间索引留后续；约定库不冒充 GB 权威）
- [ ] **Step 5: Commit** — `test: semantic understanding end-to-end; docs: record plan-2a completion and carry-overs`

---

## 自审记录（写计划时已查）

- 契约一致性：SemanticAnnotation/LayerMapping/AmbiguousLayer 形状跨 Task 1-8 一致；ISemanticAware/ISemanticGate/GateDecision 在 Task 7 定义并被 Core/Cli 消费；StepEngine 仅加性可选参数。
- 计划 1 保护：77 测试持续绿写入 Global Constraints；黄金向量/IDrawingSession 不动。
- 诚实边界：处理器语义需求第一 slice 全空（机制先行，测试处理器验证）；操作级检查非实体级；约定库非 GB 权威；Arc 角度单位待包文档核实（测试钉死行为）。
- Task 6 map 需 --drawing（取实体句柄）——已在步骤内裁定，测试随之调整。