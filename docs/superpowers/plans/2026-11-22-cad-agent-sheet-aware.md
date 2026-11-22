# 幅面/视口感知读图与语义作用域（Sheet-Aware Reading）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让读图逻辑识别多幅面/布局视口、分层分框不混淆、底图（bound xref）完整辨识、图框名定义匹配，并把读出的语义按幅面作用域联动绘制 agent 防跑偏。

**Architecture:** 语义来源确定性优先五级：①块属性（天正电气构件/图框元数据，Provenance.NativeObject）→②图框几何+名称文字+属性（Sheet 登记簿）→③图层约定（含 bound-xref `$0$` 前缀归一）→④几何启发式（块遍历深入底图）→⑤视觉（分辨率门控+矢量交叉验证，LlmInference）。overlay 标注/映射加可选 SheetId；SemanticGate 校验"操作目标幅面内已确认"；布局视口经 ViewTarget/ViewHeight 变换统一登记。

**Tech Stack:** .NET 8 / ACadSharp 3.8.35（Insert.Attributes/AttributeEntity/Viewport/Layout.AssociatedBlock 探针已核定存在）/ xUnit + Xunit.SkippableFact（真实图纸 skip-gated）/ Avalonia.Headless（视图幅面选择）

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §4（语义重建/闸口）、§5（审计/责任）、§9（诚实降级）、§12（承接）

**真实标本（只读探针实证，outputs/api-probe/tarch-probe-out.txt + outputs/tarch-native/）**：
- 电施（强电）-2#厂房电力+普通照明平面图.dwg：模型空间 **9 幅面**（院标准图框_A0 属性块×9，2列×5行网格）；图框层文字=幅面名（一层~四层/屋顶层电力、一层~四层普通照明）；图框属性 9 组（图号/版本号=01/日期=2026.09/审定人/设计人/制图人/发包方…）；**天正电气块属性 3442 个**（A=深照型灯×1364、A=屏、台、箱、柜×137、A=双联单控开关×122、A=带保护极的单相暗敷插座×11、A=交流电动机×10、N=4、$TEXT$=1C/M）；底图=bound xref 单 Insert「2号厂房0917_t9」（4019 实体含 23 DimensionAligned/179 Hatch/720 嵌套块引用；层名多级 `$0$` 链）；Layouts=Model+空布局1；代理 0；intake Green
- 电气总平面图.dwg（法院归档，T3 导出态）：多幅面模型空间平铺+远端残元；TCH 类表残留；代理 0

## Global Constraints

- 分支 `feat/cad-agent-sheet-aware`（从 main 切出；PR#18/#19 待审独立，§12 追加区冲突后合者 union 解）。
- 无审计旁路（spec §9）；语义闸口置信度≥0.8 才放行（既有 SemanticGate 默认）；低置信/不吻合→人工纠偏，不静默。
- 诚实边界：真实图纸测试 skip-gated（文件不在仓库，缺失显式跳过）；合成夹具为主闸口；视口/代理无真实内容标本处明示 deferred 证据缺口。
- 行为保持：既有 908 测试全绿是回归闸口；overlay 旧 JSON（无 SheetId）加载兼容（null=全局 legacy）。
- ACadSharp pinned 3.8.35；句柄十进制字符串口径；嵌套实体句柄后缀 "{parent}:{i}" 与渲染器同 convention。
- 中文注释/消息；EOF 换行；git 署名仅环境变量 Fake-msn（绝不改 config）。
- Windows GBK：dotnet 输出重定向 UTF-8 文件再读；单项目或全 sln（MSB1008）。
- TDD 每任务红→绿→提交；真实图纸只读（分析用 outputs 副本，intake 工作副本副作用不写用户目录）。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Create `src/CadAgent.Core/SheetModel.cs` | SheetRecord/SheetRegistry 模型 + ViewportTransform 数学 | 1/5 |
| Create `src/CadAgent.Data/Sheets/SheetRegistryBuilder.cs` | 图框检测+属性解析+名称文字关联+实体归属 | 1 |
| Modify `src/CadAgent.Data/DrawingSession.cs` | Sheets 懒加载暴露 | 1 |
| Create `src/CadAgent.Core/CanonicalLayer.cs` | `$0$` 链归一 | 2 |
| Create `src/CadAgent.Data/Semantic/BlockWalker.cs` | 循环守卫块遍历（嵌套实体+后缀句柄+世界坐标） | 2 |
| Modify `src/CadAgent.Data/Semantic/LayerInference.cs` + detectors | 归一层名+块遍历接入 | 2 |
| Create `src/CadAgent.Data/Semantic/BlockAttributeSeeder.cs` | 块属性→NativeObject 标注 | 3 |
| Modify `src/CadAgent.Core/SemanticModel.cs` | SemanticAnnotation/LayerMapping +SheetId 可选参 | 4 |
| Modify `src/CadAgent.Core/SemanticGate.cs` + `src/CadAgent.Data/Semantic/SemanticGate.cs` + `src/CadAgent.Core/StepEngine.cs:116-127` | 三参 CheckContext+sheetId 传递 | 4 |
| Modify `src/CadAgent.View/Rendering/RenderModel.cs` + `CanvasViewModel` + `MainWindow` | BuildViewport+幅面选择 UI | 5 |
| Create `src/CadAgent.Data/Vision/SheetTiler.cs` + `VisionCrossChecker.cs` | 幅面驱动瓦片+矢量交叉验证 | 6 |
| Modify `src/CadAgent.Data/Semantic/SemanticScanner.cs` | 视觉管道接入 scan 可选参 | 6 |
| Test 各任务对应 | 合成夹具+skip-gated 真实 | 各 |

---

### Task 1: Sheet 登记簿（图框检测+属性+名称+归属）

**Files:**
- Create: `src/CadAgent.Core/SheetModel.cs`
- Create: `src/CadAgent.Data/Sheets/SheetRegistryBuilder.cs`
- Modify: `src/CadAgent.Data/DrawingSession.cs`（加 `public IReadOnlyList<SheetRecord> Sheets => _sheets ??= SheetRegistryBuilder.Build(Doc);` 字段）
- Test: `tests/CadAgent.Data.Tests/SheetRegistryTests.cs`（新）+ csproj 加 Xunit.SkippableFact 1.4.13

**Interfaces:**
- Consumes: ACadSharp `Insert.Block.Name`/`Insert.Attributes`（AttributeEntity.Tag/Value，探针核定）/`Insert.InsertPoint`/`BlockRecord.Entities`；TextEntity.Layer/Value/InsertPoint
- Produces:
  - `public sealed record SheetRecord(string SheetId, string Name, string? Number, string? Scale, string? Discipline, string? Date, (double MinX, double MinY, double MaxX, double MaxY) FrameBounds, string? LayoutName, IReadOnlyDictionary<string, string> TitleAttributes);`
  - `public static class SheetRegistryBuilder { public static IReadOnlyList<SheetRecord> Build(CadDocument doc); public static string? SheetIdForPoint(IReadOnlyList<SheetRecord> sheets, double x, double y); }`
  - SheetId = "S" + 序号（图框 Insert 句柄十进制，稳定）；无图框图纸 → 单默认幅面 SheetId="S0" Name="(单幅面)" FrameBounds=全模型 bounds

- [ ] **Step 1: 写失败测试**

```csharp
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Tests;

public class SheetRegistryTests
{
    // 合成双幅面夹具：两个图框块（含属性 图号/图纸名称/比例）+ 幅面名文字 + 框内实体
    private static CadDocument TwoSheetDoc()
    {
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        for (int i = 0; i < 2; i++)
        {
            var ins = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
            ins.Attributes.Add(new AttributeEntity { Tag = "比例", Value = "1:100" });
            ins.Attributes.Add(new AttributeEntity { Tag = "日期", Value = "2026.09" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(i * 2000 + 500, 650, 0), Value = i == 0 ? "一层电力平面图" : "二层电力平面图", Height = 30, Layer = new Layer("图框") });
            doc.Entities.Add(new Line { StartPoint = new XYZ(i * 2000 + 100, 100, 0), EndPoint = new XYZ(i * 2000 + 200, 100, 0), Layer = new Layer("E-WIRE") });
        }
        return doc;
    }
    // 注意：Insert.Attributes 可加性/AttributeEntity 构造以编译为准（探针核定属性存在可读）；
    // Layer 构造 new Layer(name) 既有夹具先例（RenderModelTests）；doc.Entities.Add 自动挂层表若否先 doc.Layers.Add。

    [Fact]
    public void Build_TwoFrames_TwoSheetsWithAttributesAndNames()
    {
        var sheets = SheetRegistryBuilder.Build(TwoSheetDoc());
        Assert.Equal(2, sheets.Count);
        Assert.Equal("一层电力平面图", sheets[0].Name);
        Assert.Equal("Z-E01", sheets[0].Number);
        Assert.Equal("1:100", sheets[0].Scale);
        Assert.Equal("2026.09", sheets[0].Date);
        Assert.Equal("二层电力平面图", sheets[1].Name);
    }

    [Fact]
    public void SheetIdForPoint_AssignsEntitiesToCorrectSheet()
    {
        var sheets = SheetRegistryBuilder.Build(TwoSheetDoc());
        Assert.Equal(sheets[0].SheetId, SheetRegistryBuilder.SheetIdForPoint(sheets, 150, 100));
        Assert.Equal(sheets[1].SheetId, SheetRegistryBuilder.SheetIdForPoint(sheets, 2150, 100));
        Assert.Null(SheetRegistryBuilder.SheetIdForPoint(sheets, 5000, 5000)); // 框外（远端残元）→null
    }

    [Fact]
    public void Build_NoFrame_SingleDefaultSheet()
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) });
        var sheets = SheetRegistryBuilder.Build(doc);
        Assert.Single(sheets);
        Assert.Equal("S0", sheets[0].SheetId);
    }

    [SkippableFact]
    public void RealDrawing_NineSheets_NamesAndAttributes()
    {
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native\电施（强电）-2#厂房电力+普通照明平面图.dwg";
        Skip.If(!File.Exists(path), "真实图纸不在本机（仓库不携带），跳过");
        using var fs = File.OpenRead(path);
        var doc = new ACadSharp.IO.DwgReader(fs).Read();
        var sheets = SheetRegistryBuilder.Build(doc);
        Assert.Equal(9, sheets.Count);
        Assert.Contains(sheets, s => s.Name == "一层电力平面图");
        Assert.Contains(sheets, s => s.Name == "屋顶层电力平面图");
        Assert.Contains(sheets, s => s.Name == "四层普通照明平面图");
        Assert.All(sheets, s => Assert.Equal("2026.09", s.Date));
        Assert.All(sheets, s => Assert.Equal("01", s.TitleAttributes["版本号"]));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter SheetRegistry`
Expected: 编译失败（SheetRecord/SheetRegistryBuilder 不存在）

- [ ] **Step 3: 实现 SheetModel.cs**

```csharp
namespace CadAgent.Core;

/// <summary>幅面登记（模型空间图框幅面或布局视口幅面统一模型）。</summary>
public sealed record SheetRecord(
    string SheetId, string Name, string? Number, string? Scale, string? Discipline, string? Date,
    (double MinX, double MinY, double MaxX, double MaxY) FrameBounds,
    string? LayoutName, IReadOnlyDictionary<string, string> TitleAttributes);

/// <summary>视口模型→纸空间变换数学（布局视口幅面用，Task 5 消费）。</summary>
public static class ViewportTransform
{
    /// <summary>模型点→纸空间点：以 ViewTarget 为中心、ViewHeight 对应纸面视口高度线性缩放。</summary>
    public static (double X, double Y) ModelToPaper(
        (double X, double Y) model, (double X, double Y) viewTarget, double viewHeight,
        (double X, double Y) paperCenter, double paperHeight)
    {
        double s = viewHeight > 1e-12 ? paperHeight / viewHeight : 1;
        return (paperCenter.X + (model.X - viewTarget.X) * s, paperCenter.Y + (model.Y - viewTarget.Y) * s);
    }
}
```

- [ ] **Step 4: 实现 SheetRegistryBuilder.cs**

```csharp
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Sheets;

/// <summary>
/// 幅面登记簿：图框块引用检测（块名含 图框/图签/BORD 或携带 图号 属性）→
/// 框 bounds（块几何经 InsertTransform 展开）+ 标题栏属性 + 框内/近邻幅面名文字关联。
/// 无图框→单默认幅面。实体归属经 SheetIdForPoint（框外=null，远端残元不混入）。
/// </summary>
public static class SheetRegistryBuilder
{
    private static readonly string[] FrameMarkers = { "图框", "图签", "BORD" };

    public static IReadOnlyList<SheetRecord> Build(CadDocument doc)
    {
        var frames = doc.Entities.OfType<Insert>()
            .Where(i => i.Block != null &&
                (FrameMarkers.Any(m => i.Block.Name.Contains(m, StringComparison.OrdinalIgnoreCase))
                 || i.Attributes.Any(a => a.Tag == "图号")))
            .ToList();
        if (frames.Count == 0)
        {
            var b = ModelBounds(doc);
            return new[] { new SheetRecord("S0", "(单幅面)", null, null, null, null, b, null,
                new Dictionary<string, string>()) };
        }
        var nameTexts = doc.Entities.OfType<TextEntity>()
            .Where(t => t.Layer?.Name is "图框" or "图名" or "TITLE" || FrameMarkers.Any(m => (t.Layer?.Name ?? "").Contains(m)))
            .ToList();
        var sheets = new List<SheetRecord>();
        foreach (var f in frames.OrderBy(f => f.InsertPoint.Y).ThenBy(f => f.InsertPoint.X))
        {
            var bounds = FrameBounds(f);
            var attrs = f.Attributes.ToDictionary(a => a.Tag, a => a.Value);
            // 幅面名：框内文字优先，其次框上缘近邻（y 距框顶 <5% 框高、x 在框内）
            string? name = nameTexts.Where(t => Inside(bounds, t.InsertPoint.X, t.InsertPoint.Y))
                .Select(t => t.Value).FirstOrDefault()
                ?? nameTexts.Where(t => t.InsertPoint.X >= bounds.MinX && t.InsertPoint.X <= bounds.MaxX
                    && t.InsertPoint.Y > bounds.MaxY && t.InsertPoint.Y - bounds.MaxY < (bounds.MaxY - bounds.MinY) * 0.05)
                    .Select(t => t.Value).FirstOrDefault();
            attrs.TryGetValue("图号", out string? number);
            attrs.TryGetValue("比例", out string? scale);
            attrs.TryGetValue("图别", out string? discipline);
            attrs.TryGetValue("日期", out string? date);
            sheets.Add(new SheetRecord(f.Handle.ToString(), name ?? attrs.GetValueOrDefault("图纸名称") ?? $"幅面{sheets.Count + 1}",
                number, scale, discipline, date, bounds, null, attrs));
        }
        return sheets;
    }

    public static string? SheetIdForPoint(IReadOnlyList<SheetRecord> sheets, double x, double y)
        => sheets.FirstOrDefault(s => Inside(s.FrameBounds, x, y))?.SheetId;

    private static bool Inside((double MinX, double MinY, double MaxX, double MaxY) b, double x, double y)
        => x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY;

    /// <summary>图框块几何经插入变换（缩放/旋转/基点）展开求 bounds。</summary>
    private static (double, double, double, double) FrameBounds(Insert f)
    {
        double cos = Math.Cos(f.Rotation), sin = Math.Sin(f.Rotation);
        double a = cos * f.XScale, b = -sin * f.YScale, c = sin * f.XScale, d = cos * f.YScale;
        var bp = f.Block!.BlockEntity?.BasePoint;
        double bx = bp?.X ?? 0, by = bp?.Y ?? 0;
        double tx = f.InsertPoint.X - (a * bx + b * by), ty = f.InsertPoint.Y - (c * bx + d * by);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var e in f.Block.Entities)
        {
            foreach (var (x, y) in PointsOf(e))
            {
                double wx = a * x + b * y + tx, wy = c * x + d * y + ty;
                if (wx < minX) minX = wx; if (wx > maxX) maxX = wx;
                if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
            }
        }
        return (minX, minY, maxX, maxY);
    }

    private static IEnumerable<(double X, double Y)> PointsOf(Entity e) => e switch
    {
        Line l => new[] { (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y) },
        LwPolyline lw => lw.Vertices.Select(v => (v.Location.X, v.Location.Y)),
        TextEntity t => new[] { (t.InsertPoint.X, t.InsertPoint.Y) },
        Circle c => new[] { (c.Center.X - c.Radius, c.Center.Y - c.Radius), (c.Center.X + c.Radius, c.Center.Y + c.Radius) },
        Insert ins => new[] { (ins.InsertPoint.X, ins.InsertPoint.Y) },
        _ => Array.Empty<(double, double)>()
    };

    private static (double, double, double, double) ModelBounds(CadDocument doc)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var e in doc.Entities)
            foreach (var (x, y) in PointsOf(e))
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        return minX > maxX ? (0, 0, 1, 1) : (minX, minY, maxX, maxY);
    }
}
```

（真实标本幅面名文字在框**内**顶部——探针坐标核实：文字 y=995075 等与框 bounds 关系以实跑修正 Inside/近邻阈值；编译错误即 API 修正点：Attributes 可加性/BasePoint 可空等。）

- [ ] **Step 5: DrawingSession 暴露 Sheets**

```csharp
    private IReadOnlyList<SheetRecord>? _sheets;
    /// <summary>幅面登记簿（懒加载；图框检测+属性+名称，spec 幅面感知读图）。</summary>
    public IReadOnlyList<SheetRecord> Sheets => _sheets ??= Sheets.SheetRegistryBuilder.Build(Doc);
```

（using CadAgent.Core；命名空间冲突 Sheets 属性 vs Sheets 命名空间→用全限定 `Data.Sheets.SheetRegistryBuilder.Build(Doc)`。）

- [ ] **Step 6: 运行确认绿 + Data 全量回归**

Run: `dotnet test tests/CadAgent.Data.Tests`
Expected: PASS（含真实 9 幅面 skip-gated 本机执行）

- [ ] **Step 7: 提交**

```bash
git add src/CadAgent.Core/SheetModel.cs src/CadAgent.Data/Sheets/ src/CadAgent.Data/DrawingSession.cs tests/CadAgent.Data.Tests/SheetRegistryTests.cs tests/CadAgent.Data.Tests/CadAgent.Data.Tests.csproj
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: sheet registry (frame blocks + title attributes + name texts + point assignment)"
```

---

### Task 2: 层名归一 + 块遍历（底图完整性）

**Files:**
- Create: `src/CadAgent.Core/CanonicalLayer.cs`
- Create: `src/CadAgent.Data/Semantic/BlockWalker.cs`
- Modify: `src/CadAgent.Data/Semantic/LayerInference.cs`、`WallPairDetector.cs`、`AxisDetector.cs`、`DoorArcDetector.cs`、`RoomDetector.cs`、`SemanticScanner.cs`（CollectAmbiguousLayers）
- Test: `tests/CadAgent.Data.Tests/BlockWalkerTests.cs`（新）

**Interfaces:**
- Consumes: Task 1 SheetRegistryBuilder.SheetIdForPoint；ACadSharp Insert.Block.Entities 嵌套
- Produces:
  - `public static class CanonicalLayer { public static string Normalize(string layerName); }`（剥 `$0$` 链取末段：`a$0$b$0$C` → `C`；无 `$0$` 原样）
  - `public sealed record WalkedEntity(Entity Entity, string HandlePath, string CanonicalLayer, double WorldX, double WorldY, string? SheetId);`
  - `public static class BlockWalker { public static IEnumerable<WalkedEntity> Walk(CadDocument doc, IReadOnlyList<SheetRecord> sheets, int maxDepth = 32); }`（循环守卫：块名访问栈；世界坐标经插入变换复合；顶层 HandlePath=句柄，嵌套="{parent}:{i}"）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Normalize_StripsBoundXrefChains()
{
    Assert.Equal("A-WALL", CanonicalLayer.Normalize("2号厂房0917_t9$0$核心筒-楼梯大样$0$A-WALL"));
    Assert.Equal("E-WIRE", CanonicalLayer.Normalize("E-WIRE"));
    Assert.Equal("PUB_TEXT", CanonicalLayer.Normalize("x$0$PUB_TEXT"));
}

[Fact]
public void Walk_NestedBlockEntities_YieldSuffixHandlesAndWorldCoords()
{
    // 夹具：块 B 含线 (0,0)-(10,0)；顶层 Insert B @ (100,200) scale 2 → 世界 (100,200)-(120,200)
    // 嵌套：块 C 含 Insert B @ (5,5)；顶层 Insert C @ (0,0) → 孙线世界 (10,10)-(30,10)
    var doc = ...; // 合成，镜像 SheetRegistryTests 夹具模式
    var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
    var deep = walked.First(w => w.HandlePath.Contains(":"));
    Assert.Equal(10, deep.WorldX, 6);
    Assert.Equal("A-WALL", deep.CanonicalLayer); // 块内实体层名带 $0$ 链时归一
}

[Fact]
public void Walk_SelfReferentialBlock_Terminates()
{
    // 块 S 含 Insert S（自指）→ maxDepth/访问栈守卫终止，不栈溢出
    var doc = ...;
    var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
    Assert.True(walked.Count < 1000);
}

[Fact]
public void Scan_BoundXrefWallLayer_ConventionHitThroughNormalization()
{
    // 夹具：块内 LwPolyline 双线对（墙）层名 "x$0$WALL"；顶层 Insert → scan 后 overlay 含 Wall 标注（GeometryInference 或 LayerInference）
    // 对照：归一前 ConventionLibrary.ClassForLayer("x$0$WALL")=Unknown
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter "Normalize_|Walk_"`
Expected: 编译失败

- [ ] **Step 3: 实现 CanonicalLayer + BlockWalker**

```csharp
namespace CadAgent.Core;

/// <summary>bound xref 层名链归一（a$0$b$0$C → C）；约定匹配用归一名，映射/歧义清单保留原名。</summary>
public static class CanonicalLayer
{
    public static string Normalize(string layerName)
    {
        int i = layerName.LastIndexOf("$0$", StringComparison.Ordinal);
        return i >= 0 ? layerName[(i + 3)..] : layerName;
    }
}
```

```csharp
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

public sealed record WalkedEntity(Entity Entity, string HandlePath, string CanonicalLayer, double WorldX, double WorldY, string? SheetId);

/// <summary>
/// 块遍历（底图完整性）：深入 bound xref/嵌套块 yield 实体（世界坐标+后缀句柄+归一层名+幅面归属）。
/// 循环守卫=块名访问栈+maxDepth；检测器/渲染消费同一遍历口径。
/// </summary>
public static class BlockWalker
{
    public static IEnumerable<WalkedEntity> Walk(CadDocument doc, IReadOnlyList<SheetRecord> sheets, int maxDepth = 32)
    {
        foreach (var e in doc.Entities)
            foreach (var w in WalkEntity(e, e.Handle.ToString(), "", 1, 0, 0, 1, 0, 0, 1,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), sheets, maxDepth))
                yield return w;
    }

    private static IEnumerable<WalkedEntity> WalkEntity(Entity e, string path, string layerChain,
        double depth, double a, double b, double c, double d, double tx, double ty,
        HashSet<string> stack, IReadOnlyList<SheetRecord> sheets, int maxDepth)
    {
        string layer = e.Layer?.Name ?? "0";
        string canon = CanonicalLayer.Normalize(layer);
        if (e is Insert ins && ins.Block != null && depth < maxDepth && stack.Add(ins.Block.Name))
        {
            double cos = Math.Cos(ins.Rotation), sin = Math.Sin(ins.Rotation);
            double ia = cos * ins.XScale, ib = -sin * ins.YScale, ic = sin * ins.XScale, id = cos * ins.YScale;
            var bp = ins.Block.BlockEntity?.BasePoint;
            double bx = bp?.X ?? 0, by = bp?.Y ?? 0;
            double itx = ins.InsertPoint.X - (ia * bx + ib * by), ity = ins.InsertPoint.Y - (ic * bx + id * by);
            // 复合：outer(inner(p))
            double na = a * ia + b * ic, nb = a * ib + b * id, nc = c * ia + d * ic, nd = c * ib + d * id;
            double ntx = a * itx + b * ity + tx, nty = c * itx + d * ity + ty;
            int i = 0;
            foreach (var child in ins.Block.Entities)
                foreach (var w in WalkEntity(child, path + ":" + i, layerChain, depth + 1, na, nb, nc, nd, ntx, nty, stack, sheets, maxDepth))
                    yield return w;
            stack.Remove(ins.Block.Name);
            yield break;
        }
        var (px, py) = AnchorOf(e);
        double wx = a * px + b * py + tx, wy = c * px + d * py + ty;
        yield return new WalkedEntity(e, path, canon, wx, wy, SheetRegistryBuilder.SheetIdForPoint(sheets, wx, wy));
    }

    private static (double X, double Y) AnchorOf(Entity e) => e switch
    {
        Line l => (l.StartPoint.X, l.StartPoint.Y),
        LwPolyline lw when lw.Vertices.Count > 0 => (lw.Vertices[0].Location.X, lw.Vertices[0].Location.Y),
        TextEntity t => (t.InsertPoint.X, t.InsertPoint.Y),
        Circle c => (c.Center.X, c.Center.Y),
        Arc ar => (ar.Center.X, ar.Center.Y),
        _ => (0, 0)
    };
}
```

（检测器接入：WallPairDetector/AxisDetector/DoorArcDetector/RoomDetector 的实体源从 session.Doc.Entities 改 BlockWalker.Walk（世界坐标口径——检测器内部几何用 WalkedEntity.WorldX/Y 与实体本地点混合处须统一：检测器改为消费 WalkedEntity 序列+实体引用，几何计算用世界坐标变换后的点；逐检测器适配，保持既有顶层测试绿（顶层 Walk 世界坐标=本地点）。LayerInference 分组键改 canon（映射/歧义保留原名）。）

- [ ] **Step 4: 运行确认绿 + 全量回归**

Run: `dotnet test tests/CadAgent.Data.Tests` + `dotnet test tests/CadAgent.Core.Tests` + `dotnet test tests/CadAgent.Cli.Tests`
Expected: PASS（既有语义测试顶层行为保持）

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Core/CanonicalLayer.cs src/CadAgent.Data/Semantic/ tests/CadAgent.Data.Tests/BlockWalkerTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: canonical layer names + cycle-guarded block walk (bound-xref base completeness)"
```

---

### Task 3: 块属性语义种子（NativeObject）

**Files:**
- Create: `src/CadAgent.Data/Semantic/BlockAttributeSeeder.cs`
- Modify: `src/CadAgent.Data/Semantic/SemanticScanner.cs`（Scan 内调用 seeder，先于 LayerInference）
- Modify: `src/CadAgent.Core/SemanticModel.cs`（SemanticClass 若缺 Equipment/Device 成员则加——读现状核定）
- Test: `tests/CadAgent.Data.Tests/BlockAttributeSeederTests.cs`（新）

**Interfaces:**
- Consumes: Task 2 BlockWalker.Walk（属性在 Insert 上，Walk yield Insert 本身？——Walk 对 Insert 递归不 yield Insert 自身；seeder 直接遍历 doc.Entities.OfType<Insert>() 顶层+嵌套经 BlockWalker 需 yield Insert：修改 Walk 对 Insert 也 yield 一条（HandlePath=自身句柄路径）供属性消费）
- Produces: `public static class BlockAttributeSeeder { public static void Seed(CadDocument doc, SemanticOverlay overlay, IReadOnlyList<SheetRecord> sheets); }`；属性→类默认表：`A=深照型灯/…灯…→Lighting`、`A=屏、台、箱、柜→Equipment`、`A=*开关*→Device`、`A=*插座*→Device`、`A=*电动机*→Equipment`（表放 Data/Semantic/AttributeSemantics.cs public static dictionary + skill 别名可覆写后续）；置信度 0.95 Provenance.NativeObject Evidence="块属性 A=深照型灯 (块 $equip$00002832)"；SheetId 经插入点归属

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Seed_EquipAttributes_NativeObjectAnnotations()
{
    // 夹具：块 E 含圆；Insert E 属性 A=深照型灯 @ (10,10)；scan 后 overlay 含该句柄 Lighting 标注 conf 0.95 NativeObject
}

[Fact]
public void Seed_UnknownAttributeValue_NoAnnotation()
{
    // A=未知构件 → 不种子（诚实：不猜）
}

[SkippableFact]
public void RealDrawing_LuminaireCount_MatchesProbe()
{
    // 真实图：Lighting NativeObject 标注数 == 1364（探针 A=深照型灯 计数）
}
```

- [ ] **Step 2-4: 红→实现→绿**（实现：遍历 Insert（含嵌套经 Walk yield Insert），属性 A 值查表→UpsertAnnotation；N/$TEXT$ 存 Evidence 不单独成类）
- [ ] **Step 5: 提交** `feat: block-attribute semantic seeding (NativeObject, TArch equip semantics)`

---

### Task 4: 语义按幅面作用域 + 闸口防跨幅跑偏

**Files:**
- Modify: `src/CadAgent.Core/SemanticModel.cs`（SemanticAnnotation/LayerMapping 末尾加 `string? SheetId = null`）
- Modify: `src/CadAgent.Core/SemanticGate.cs`（ISemanticContextGate 加 `GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle, string? sheetId);` 旧两参保留）
- Modify: `src/CadAgent.Data/Semantic/SemanticGate.cs`（三参实现：sheetId 非 null 时标注须 SheetId==sheetId 或 SheetId==null(legacy 全局)；两参委托三参 null）
- Modify: `src/CadAgent.Core/StepEngine.cs:116-127`（step params 含 "sheetId" 时传三参）
- Modify: Task 2/3 产出 stamp SheetId（LayerInference/seeder/detectors 经 WalkedEntity.SheetId）
- Test: `tests/CadAgent.Data.Tests/SheetScopeGateTests.cs`（新）

**Interfaces:**
- Consumes: Task 1 sheets/Task 2 WalkedEntity.SheetId/Task 3 seeder
- Produces: 三参 CheckContext；StepSpec params 可选 "sheetId" 契约（HandlerKindDocs 增注记）

- [ ] **Step 1: 写失败测试（跑偏红）**

```csharp
[Fact]
public void CheckContext_SheetAConfirmed_SheetBTarget_Refused()
{
    // overlay：句柄 hB（幅面 S2 内实体）无标注；句柄 hA（S1）Lighting 1.0
    // gate.CheckContext([Lighting], hB, "S2") → false（跨幅跑偏拒绝）
    // gate.CheckContext([Lighting], hA, "S1") → true
}

[Fact]
public void CheckContext_LegacyNullSheet_GlobalAnnotationPasses()
{
    // 旧 overlay 无 SheetId → 三参 sheetId 非 null 时全局标注仍放行（legacy 兼容）
}

[Fact]
public void StepEngine_PlanWithSheetId_CrossSheetRefusedAudited()
{
    // 双幅面夹具；S1 确认 Lighting；plan step layout-lighting params sheetId=S2 区域 → tx failed + 审计留痕（run-rejected/semantic-gate-refused 既有 kind）
}
```

- [ ] **Step 2-4: 红→实现→绿**（含 Cli.Tests 全量回归：既有 plan 无 sheetId → 两参路径行为保持）
- [ ] **Step 5: 提交** `feat: per-sheet semantic scope + cross-sheet drift refusal at gate`

---

### Task 5: 布局视口支持 + 视图幅面选择

**Files:**
- Modify: `src/CadAgent.Data/Sheets/SheetRegistryBuilder.cs`（布局幅面：doc.Layouts 纸空间 + Viewport 实体 → SheetRecord(LayoutName, FrameBounds=视口纸面 bounds, 名称=布局名)；模型区域经 ViewTarget/ViewHeight 记录于 TitleAttributes["viewTarget"]/["viewHeight"]）
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`（`public static IReadOnlyList<RenderItem> BuildViewport(CadDocument doc, Viewport vp, IReadOnlyList<SheetRecord> sheets)`：视口模型区域裁剪+ViewportTransform 到纸面坐标）
- Modify: `src/CadAgent.View/ViewModels/CanvasViewModel.cs` + `MainWindow.cs`（幅面 ComboBox：sheets 列表→选择→canvas 刷新为该幅面 items（模型幅面=框内裁剪；布局幅面=BuildViewport）；默认=首幅面；状态栏幅面名）
- Test: `tests/CadAgent.View.Tests/ViewportRenderTests.cs` + `tests/CadAgent.Data.Tests/SheetRegistryTests.cs` 扩展

**Interfaces:**
- Consumes: Task 1 ViewportTransform/SheetRecord；ACadSharp Viewport.Center/ViewHeight/ViewTarget（探针核定）/Layout.AssociatedBlock/IsPaperSpace
- Produces: BuildViewport；MainWindow 幅面选择（internal ComboBox? SheetSelector 测试可达）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ModelToPaper_KnownTransform()
{
    // viewTarget=(100,100) viewHeight=200 paperCenter=(500,350) paperHeight=700 → model(100,100)→(500,350)；model(100,300)→(500,1050)
    var p = ViewportTransform.ModelToPaper((100, 300), (100, 100), 200, (500, 350), 700);
    Assert.Equal(500, p.X, 6); Assert.Equal(1050, p.Y, 6);
}

[Fact]
public void BuildViewport_CropsModelRegion_AndMapsToPaper()
{
    // 夹具：模型线 (0,0)-(1000,0) 与 (5000,5000)-(6000,5000)；Viewport viewTarget=(0,0) viewHeight=200 → 仅首线入视口区域（裁剪半径=viewHeight*aspect）
}

[AvaloniaFact]
public async Task SheetSelector_SwitchesCanvasItems()
{
    // 双幅面夹具窗口 → SheetSelector 选第二幅 → canvas items 仅第二幅内实体
}

[SkippableFact]
public void RealDrawing_LayoutsRegistered_EmptyLayoutHonest()
{
    // 真实图：Layouts=2（Model+布局1 空）→ 布局幅面 0 个（空布局不登记）；模型幅面 9
}
```

- [ ] **Step 2-4: 红→实现→绿**（视口实体构造以编译为准；空布局/无视口图纸行为保持=模型幅面路径）
- [ ] **Step 5: 提交** `feat: layout viewport sheets + view sheet selector (paper-space transform)`

---

### Task 6: 视觉→overlay 管道（幅面驱动+矢量交叉验证）

**Files:**
- Create: `src/CadAgent.Data/Vision/SheetTiler.cs`（幅面 bounds→中位数锚点瓦片矩形，字高反算 24px；离屏渲染接口 seam `Func<SheetRecord, (double,double,double,double), byte[]>` 由 View  harness/视图注入——Data 不引用 View：渲染 seam 经接口 `IRenderSnapshot { byte[] RenderPng(sheet, rect, w, h); }` 注入）
- Create: `src/CadAgent.Data/Vision/VisionCrossChecker.cs`（vision 声称结构化 JSON {claims:[{text,region,confidence}]}→与矢量 GT（块属性/图框文字/顶层+块内 TextEntity 值）交叉：精确/归一匹配→LlmInference conf 0.9 evidence 含"矢量交叉验证吻合"；不吻合→conf 0.3+歧义清单；不可读→歧义清单）
- Modify: `src/CadAgent.Data/Semantic/SemanticScanner.cs`（Scan 加可选参 `VisionReadResult? vision = null` → cross-check 种子/歧义）
- Test: `tests/CadAgent.Data.Tests/VisionCrossCheckerTests.cs`（新，fake claims 不触网）

**Interfaces:**
- Consumes: Task 1 sheets/Task 2 Walk（块内文字 GT）/Task 3 属性 GT；ILlmClient（Core，ImageUrl）由调用方注入 VisionReader（CLI/视图 harness）
- Produces: `public sealed record VisionClaim(string Text, string Region, double Confidence); public sealed record VisionReadResult(IReadOnlyList<VisionClaim> Claims); public sealed record CrossCheckResult(IReadOnlyList<SemanticAnnotation> Seeded, IReadOnlyList<AmbiguousLayer> Ambiguous);`（歧义复用 AmbiguousLayer 或新 VisionAmbiguous record——取新 record VisionAmbiguous(string Claim, string Reason)）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void CrossCheck_ClaimMatchesVectorText_SeedsHighConfidence()
{
    // GT 含 "深照型灯"（属性）；claim text=深照型灯 conf 0.8 → seeded LlmInference 0.9 evidence 交叉吻合
}

[Fact]
public void CrossCheck_ClaimContradictsVector_LowConfidenceAmbiguous()
{
    // GT 日期 2018.10；claim "2023.06" → 不种子 + VisionAmbiguous（幻觉拦截，实验实证场景）
}

[Fact]
public void CrossCheck_UnreadableRegion_AmbiguousNotSeeded()
{
    // claim confidence 0.2 → 歧义清单，不种子
}
```

- [ ] **Step 2-4: 红→实现→绿**
- [ ] **Step 5: 提交** `feat: vision-to-overlay pipeline (sheet-driven tiles, vector cross-check, hallucination interception)`

---

### Task 7: agent 联动 E2E + spec §12 + 门禁 + PR

**Files:**
- Modify: `src/CadAgent.Data/Handlers/HandlerKindDocs.cs`（sheetId 契约注记）
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §12（闭环注记+剩余边界：代理 🔴 路径无真实标本/空布局视口无内容标本/视觉管道真实模型 E2E 留 harness）
- Test: `tests/CadAgent.Cli.Tests/SheetAwareAgentE2ETests.cs`（新：双幅面夹具 agent plan 带 sheetId 跑偏拒绝+正确幅面放行+审计 verify）
- Create: `docs/verification/sheet-aware-protocol.md`（真实图纸复核协议：9 幅面名/属性计数/底图墙对/跨幅拒绝/视口空布局诚实注记）

- [ ] **Step 1: E2E 红→绿**（CLI agent --intent 带幅面语义；fake LLM client seam 既有 LlmClientFactory）
- [ ] **Step 2: 全量门禁连绿两次** `dotnet test CadAgent.sln`（ZWCAD 真机前后 tasklist 核查；日志 outputs/sheet-aware/gate{1,2}.log）
- [ ] **Step 3: spec §12 + 协议文档提交**
- [ ] **Step 4: 推送+PR 保留用户批准**（finishing 菜单）

---

## Self-Review 记录

- **Spec 覆盖**：§4 语义重建三路信号→五级确定性来源（属性/图框/归一约定/几何/视觉交叉）；§4.2 闸口→幅面作用域防跑偏（T4/T7）；§9 诚实降级（skip-gated/空布局/幻觉拦截）；§12 承接注记（T7）。用户四问：视口（T5）/分层分框（T1/T2）/底图+图框名匹配（T1/T2/T3）/agent 联动（T4/T7）逐一对应。
- **占位符扫描**：夹具合成代码以编译为准的 API 修正点均给确定性路径（Attributes 可加性/Viewport 构造/Layer 挂表）；检测器世界坐标适配为明确改造指令非 TBD。
- **类型一致性**：SheetRecord/SheetIdForPoint（T1）→WalkedEntity.SheetId（T2）→seeder stamp（T3）→CheckContext 三参（T4）→BuildViewport/SheetSelector（T5）→VisionReadResult/CrossCheck（T6）→E2E（T7）链一致；Provenance 用既有 NativeObject/LlmInference 不扩枚举。
- **已知取舍**：Walk 对 Insert yield 自身一条（T3 属性消费）改变 T2 测试计数口径——T2 测试断言用 Contains/过滤而非精确总数；检测器世界坐标改造保持顶层测试绿为回归闸口；视图幅面选择默认首幅面（行为变更：原全模型渲染→默认幅面裁剪，既有 headless 测试期望连带更新 sanctioned）。
---

## 执行记录（2026-11-22，子代理驱动）

- 分支 feat/cad-agent-sheet-aware，11 提交（92c3759..3cf3e5e），7 任务全完成 + 最终审查修复波（R10/R11）。
- 门禁：dotnet test CadAgent.sln 连绿两次 **958 通过 / 0 失败 / 0 跳过**（Core 207/Data 270/Cli 175/View 263/Llm 30/Zwcad 真机 13；真实标本 skip-gated 实跑：9 幅面/1364 深照型灯）。
- 任务内修复环：T1 重复 Tag 崩溃面；T5 布局只读硬闸+selector edit-busy 闸；T6 短值守卫+命中优先级+多幅面歧义抑制。
- 最终审查修复波：handler 侧幅面裁剪（label-luminaires 限归属 Insert/layout-lighting 区域含于 FrameBounds，confinement E2E 双幅面双灯具钉住 S2 零落图）；SheetIdForPoint 跳过布局幅面（纸/模型坐标分离）；MainWindow._doc undo/rollback 同步。
- 裁决 R1-R11 见 SDD ledger（已归档于本记录摘要）：R1 数学单一源 Core Affine；R2 DIM 三参闸口；R3 Walk yield Insert；R4 视图默认首幅面 sanctioned；R5 真实图纸 skip-gated；R6 SheetId 字段前移 T3；R7 陈旧 NativeObject 清理；R8 布局只读+edit-busy 硬闸；R9 短值守卫+多幅面抑制；R10 handler 幅面裁剪（核心承诺不降级 caveat）；R11 坐标分离+_doc 同步。
- **剩余 deferred（post-merge）**：ModelToPaper 生产死代码/AnchorOf 第三份拷贝 consolidate；VisionClaim.Region 瓦片几何校验+类推断 norm；null SheetId 双群注释；AttDef 轴号噪声过滤；DoorArc 镜像扫角；嵌套旋转/互指测试；瓦片零重叠骑缝；init null 归一；多 claim 覆盖取优先级；E2E 断言强度四条；属性表可注入+env 路径覆盖；指纹 layout.Name/块内部几何；legacy payload sheetId:null WhenWritingNull；L-sheet handler 跨空间比较限定模型幅面声明；性能 dirty-flag。
- **证据缺口（诚实）**：代理 🔴 路径无真实标本；空布局视口无内容标本（视口真实 E2E 缺口）；视觉管道真实模型 E2E 留 harness（IRenderSnapshot+VL reader 未接生产 CLI）；纸面编辑 deferred。
