# 计划4 视图延迟项承接（View Closure）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 闭环计划4 全部延迟项：渲染（Dimension/Hatch/dash/OCS）、T9 编辑 UX 遗留、审计 parity/结构性守卫、空图层、④纠偏计划视图预览（LLM caller 已解锁）、真机手动验证，并更正 spec §12 过时记录。

**Architecture:** RenderModelBuilder 扩展（Dimension 经匿名 Block 展开——探针核定 DXF 往返保留；Hatch 经 BoundaryPath.GetPoints  tessellation + EvenOdd 填充；dash 经 LineType.Segment→Avalonia DashStyle；OCS 经任意轴算法+非默认法向圆/弧 tessellation）。AuditViewModel 增 Rebuild；Issues/Edit VM 补 check-run 审计 parity 与 run-regression（镜像 CLI 字段形状，Source 区分 view-check/view-regression）；结构性硬闸（_diffBusy/RefreshAsync in-flight）镜像既有 _editBusy 模式。计划预览复用 AgentOrchestrator 既有 confirmPlan/confirmCloudEgress 回调（Core 零改动），View 增 CadAgent.Llm 引用接线 OpenAiClient/DPAPI。

**Tech Stack:** .NET 8 / Avalonia 11.3.2 code-driven（无 XAML）/ Avalonia.Headless.XUnit 真实 Skia 帧 / ACadSharp 3.8.35（API 探针已核定：Dimension.Block 可设、Hatch.BoundaryPath.GetPoints(int)→List<XY>、LineType.Segments 可 ICollection cast Add、Entity.LineTypeScale、Circle.Normal 可设、DxfWriter.Write(fs,doc) 静态）

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §4.1④（纠偏计划：勾选问题→LLM 计划→schema+角色校验→视图预览→人工确认执行）、§5（视图面板清单含计划预览）、§9（一切操作必经审计管线无旁路；观察性检查故障也入审计）、§12（延迟项清单 L369/381；过时记录已更正 commit 5619755）

## Global Constraints

- 分支 `feat/cad-agent-view-closure`（已从 main 67b17d4 切出；§12 更正已提交 5619755）。主树直接作业。
- **无审计旁路**（spec §9）：视图一切修改经 EditViewModel/StepEngine；观察性检查（check/regression）故障也入审计；审计先行不变量。
- View code-driven UI（无 XAML）；DockPanel LastChildFill 顺序约束（计划4 回归教训：后加控件偷 LastChildFill → 画布宽 0）。
- 真机测试串行约束不变（ZwcadLive 集合 + runner.json）；本切片不触 COM 适配器（除 Move 处理器契约文本）。
- ACadSharp pinned 3.8.35；CSMath 命名空间；句柄十进制字符串口径（RenderItem.Handle/审计 Target 同源）。
- 中文注释/消息；新建文件 EOF 换行（Write 工具可能剥掉——commit 前 git diff 检查）。
- git 署名仅环境变量：`GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`，绝不改 git config。
- Windows GBK：dotnet 输出重定向 UTF-8 文件再读；dotnet test 单项目或全 sln（多项目参数 MSB1008）。
- 密钥硬规则（spec §8）：DPAPI/环境变量解析，永不入审计/日志/预览面板文本。
- TDD：每任务红→绿→提交；headless 测试镜像既有 View.Tests 模式（HeadlessTestApp/[AvaloniaFact]）。
- 诚实边界：不假装已渲染/已验证——近似（tessellation/投影/点划）须代码注释+spec 注记；真机手动验证走 computer-use 协议留证。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Modify `src/CadAgent.View/Rendering/RenderModel.cs` | OCS 映射器+Dimension/Hatch/dash 扩展；RenderItem 加可选参 DashPattern/Fill；RenderKind.Hatch；LayerColorRgb 公开 | 1/2/3/8 |
| Modify `src/CadAgent.View/Controls/DrawingCanvas.cs` | Hatch EvenOdd 填充绘制 + DashStyle pen 克隆 | 2/3 |
| Modify `src/CadAgent.Data/Handlers/EditHandlers.cs` | MoveEntityHandler 扩展六类实体 | 4 |
| Create `src/CadAgent.Data/Handlers/HandlerKindDocs.cs` | CLI KindDocs 文本提取共享（视图预览消费） | 4/10 |
| Modify `src/CadAgent.Cli/CommandRouter.Agent.cs` | KindDocs 引用共享常量 + move 契约文本更新 | 4 |
| Modify `src/CadAgent.View/ViewModels/AuditViewModel.cs` | 保留 IAuditStore + Rebuild + TimelineChanged | 5 |
| Modify `src/CadAgent.View/MainWindow.cs` | 审计 live-rebuild 接线/_diffBusy 硬闸/编辑 UI 输入/Delete 模态/计划面板/空图层接线 | 5/8/9/10 |
| Create `src/CadAgent.View/Services/ViewRuleEngine.cs` | 检查器装配共享（Issues/Edit 回归共用，镜像 CLI StandardsEngine） | 6 |
| Modify `src/CadAgent.View/ViewModels/IssuesViewModel.cs` | in-flight 守卫 + check-run 审计 parity | 6 |
| Modify `src/CadAgent.View/ViewModels/EditViewModel.cs` | run-regression 观察性检查 + Warning | 7 |
| Modify `src/CadAgent.View/ViewModels/LayersViewModel.cs` | session 图层表并集（空图层显示） | 8 |
| Create `src/CadAgent.View/Services/ConfirmDialog.cs` | Delete 确认模态（ShowDialog<bool>） | 9 |
| Create `src/CadAgent.View/ViewModels/PlanPreviewViewModel.cs` | ④纠偏计划预览（AgentOrchestrator confirmPlan 回调接线） | 10 |
| Modify `src/CadAgent.View/CadAgent.View.csproj` | += CadAgent.Llm 引用 | 10 |
| Create `docs/verification/view-closure-protocol.md` | 真机手动验证协议+证据 | 11 |
| Modify spec §12 | 延迟项闭环注记 | 11 |
| Test 文件 | 每任务对应 View.Tests/Data.Tests/Cli.Tests 新增/扩展 | 各 |

---

### Task 1: OCS 任意轴变换（渲染基础）

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`（Append 各 2D 实体 case + OcsMapper 辅助）
- Test: `tests/CadAgent.View.Tests/RenderModelTests.cs`（扩展）

**Interfaces:**
- Consumes: ACadSharp `Entity.Normal`（Circle/Arc/LwPolyline/TextEntity/MText/Insert/Hatch/Dimension 均有，XYZ；默认 (0,0,1)）；既有 `Affine` record struct
- Produces: `private static (Affine Map, bool IsIdentity) OcsMapper(XYZ normal)`（OCS→WCS XY 投影的 2D 仿射 +  identity 快路径）；`private static double OcsRotation(double rotDeg, Affine ocs)`（OCS 内旋转角→WCS XY 投影角）；非默认法向 Circle/Arc → tessellated Polyline（72 段）

- [ ] **Step 1: 写失败测试（追加到 RenderModelTests.cs）**

```csharp
[Fact]
public void Circle_MirroredNormal_TessellatesMirroredPolyline()
{
    // Normal=(0,0,-1) → OCS x 轴镜像：圆心 (5,5) 半径 3 → WCS 圆心 (-5,5)
    var doc = new CadDocument();
    doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3, Normal = new XYZ(0, 0, -1) });
    var session = DrawingSession.FromDoc(doc, "mem.dxf"); // 既有测试夹具模式——读 RenderModelTests 现有 session 构造法，逐字镜像
    var items = RenderModelBuilder.Build(session);
    var item = Assert.Single(items);
    Assert.Equal(RenderKind.Polyline, item.Kind); // 非默认法向→tessellation（真椭圆投影的诚实近似）
    Assert.True(item.Points.Count >= 72);
    double cx = item.Points.Average(p => p.X);
    double cy = item.Points.Average(p => p.Y);
    Assert.Equal(-5, cx, 3);
    Assert.Equal(5, cy, 3);
    Assert.Equal(3, item.Points.Max(p => Math.Abs(p.X - cx)), 2);
}

[Fact]
public void Circle_DefaultNormal_StillExactCircle()
{
    var doc = new CadDocument();
    doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3 });
    var session = DrawingSession.FromDoc(doc, "mem.dxf");
    var item = Assert.Single(RenderModelBuilder.Build(session));
    Assert.Equal(RenderKind.Circle, item.Kind); // 行为保持
}

[Fact]
public void LwPolyline_MirroredNormal_VerticesMirrored()
{
    var doc = new CadDocument();
    var lw = new LwPolyline { Normal = new XYZ(0, 0, -1) };
    lw.Vertices.Add(new LwPolyline.Vertex(new XY(1, 2)));
    lw.Vertices.Add(new LwPolyline.Vertex(new XY(3, 4)));
    doc.Entities.Add(lw);
    var session = DrawingSession.FromDoc(doc, "mem.dxf");
    var item = Assert.Single(RenderModelBuilder.Build(session));
    Assert.Equal((-1.0, 2.0), item.Points[0]);
    Assert.Equal((-3.0, 4.0), item.Points[1]);
}

[Fact]
public void Text_MirroredNormal_InsertionMirroredRotationProjected()
{
    var doc = new CadDocument();
    doc.Entities.Add(new TextEntity
    {
        InsertPoint = new XYZ(2, 3, 0), Value = "镜像", Height = 5,
        Rotation = Math.PI / 2, Normal = new XYZ(0, 0, -1)
    });
    var session = DrawingSession.FromDoc(doc, "mem.dxf");
    var item = Assert.Single(RenderModelBuilder.Build(session));
    Assert.Equal((-2.0, 3.0), item.Points[0]);
    // OCS 内 90° 方向 (0,1) → 镜像后 (0,1) → WCS 角仍 90°
    Assert.Equal(90, item.RotationDeg, 3);
}
```

注意：`DrawingSession.FromDoc` 是占位名——**读 RenderModelTests.cs 现有夹具构造法（可能经 DxfFixture 往返或 DrawingSession 内部构造器），逐字镜像其模式**；若现有测试全部经 DXF 文件往返，则本任务测试同样写临时 DXF（DxfWriter.Write(fs, doc) 静态，探针核定）。LwPolyline.Vertex 构造签名若不符（探针未覆盖），以编译错误为准修正为 `new LwPolyline.Vertex { Location = new XY(1,2) }` 或既有测试用法。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter MirroredNormal`
Expected: FAIL（镜像未生效——当前忽略 Normal）

- [ ] **Step 3: 实现 OcsMapper + 各 case 接线**

RenderModel.cs 加辅助（Affine 旁）：

```csharp
    /// <summary>
    /// OCS→WCS XY 投影（AutoCAD 任意轴算法）：法向接近 +Z 时 identity 快路径；
    /// 否则 Ax = (|Nx|,|Ny| 均 < 1/64 ? (0,1,0)×N : (0,0,1)×N) 归一，Ay = N×Ax 归一。
    /// 返回 2D 仿射（丢弃 WCS Z——视图为 XY 投影）。倾斜法向的圆/弧投影为椭圆，
    /// 经 tessellation 诚实近似（72 段）；文字旋转取 OCS 方向向量投影角。
    /// </summary>
    private static (Affine Map, bool IsIdentity) OcsMapper(XYZ normal)
    {
        XYZ n = Normalize(normal);
        if (Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64 && n.Z > 0)
            return (Affine.Identity, true);
        XYZ ax = Normalize(Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64
            ? Cross(new XYZ(0, 1, 0), n) : Cross(new XYZ(0, 0, 1), n));
        XYZ ay = Normalize(Cross(n, ax));
        return (new Affine(ax.X, ay.X, ax.Y, ay.Y, 0, 0), false);
    }

    private static XYZ Normalize(XYZ v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-12 ? new XYZ(0, 0, 1) : new XYZ(v.X / len, v.Y / len, v.Z / len);
    }

    private static XYZ Cross(XYZ a, XYZ b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>OCS 内旋转角（度）→ WCS XY 投影角：方向向量经 OCS 映射后 atan2。</summary>
    private static double OcsRotation(double rotDeg, Affine ocs)
    {
        double r = rotDeg * Math.PI / 180.0;
        (double x, double y) = ocs.Apply(Math.Cos(r), Math.Sin(r));
        return NormDeg(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    /// <summary>非默认法向圆/弧 → OCS 内 tessellate 后逐点映射（椭圆投影诚实近似）。</summary>
    private static void AppendTessellatedArc(
        List<RenderItem> items, string layer, uint color, string handle,
        Affine t, Affine ocs, double cx, double cy, double radius,
        double startDeg, double sweepDeg, int segments = 72)
    {
        var pts = new List<(double X, double Y)>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double a = (startDeg + sweepDeg * i / segments) * Math.PI / 180.0;
            (double ox, double oy) = ocs.Apply(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
            pts.Add(t.Apply(ox, oy));
        }
        items.Add(new RenderItem(RenderKind.Polyline, pts, layer, color, null, 0, 0, handle));
    }
```

Append 各 case 改造（行为保持：默认法向走原路径）：
- `case Line`：Line 点为 WCS（DXF 约定），**不接 OCS**（注释说明）。
- `case Arc`：`var (ocs, ocsId) = OcsMapper(arc.Normal);` ocsId → 原逻辑；否则 `AppendTessellatedArc(items, layer, color, handle, t, ocs, arc.Center.X, arc.Center.Y, arc.Radius * t.RadiusScale, ToDeg(arc.StartAngle) + rotationDeg, NormDeg(ToDeg(arc.EndAngle - arc.StartAngle)))`（sweep 为 CCW 正角差）。
- `case Circle`：同型；ocsId → 原 Circle item；否则 `AppendTessellatedArc(..., startDeg: 0, sweepDeg: 360)`。
- `case LwPolyline`：`var (ocs, ocsId) = OcsMapper(lw.Normal);` 顶点 `t.Apply(ocs.Apply(v.Location.X, v.Location.Y))`（identity 时 ocs.Apply 恒等，可统一走双 Apply 无分支）。
- `case Polyline2D`：Vertex2D.Location 为 WCS（3D 多段线）——不接 OCS（注释）。
- `case TextEntity`：`var (ocs, _) = OcsMapper(text.Normal);` 插入点 `t.Apply(ocs.Apply(...))`；旋转 `NormDeg(OcsRotation(ToDeg(text.Rotation), ocs) + rotationDeg)`。
- `case MText`：同 TextEntity（mtext.Normal/InsertPoint/Rotation）。
- `case Insert`：`var (ocs, ocsId) = OcsMapper(insert.Normal);` `Affine composed = t.Compose(InsertTransform(insert)).Compose(ocs);`（镜像法向精确；倾斜法向=投影近似，注释说明）。旋转/字高缩放逻辑不变。
- default 分支 Dimension/Hatch 在 Task 2 加入。

- [ ] **Step 4: 运行确认绿 + 全量渲染回归**

Run: `dotnet test tests/CadAgent.View.Tests --filter RenderModel`
Expected: PASS（新 4 + 既有全绿——默认法向行为保持）

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs tests/CadAgent.View.Tests/RenderModelTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): OCS arbitrary-axis transform (mirrored/tilted normals, tessellated circles)"
```

---

### Task 2: Dimension + Hatch 渲染

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`（RenderKind.Hatch + RenderItem 可选参 Fill + Dimension/Hatch case + Bounds）
- Modify: `src/CadAgent.View/Controls/DrawingCanvas.cs`（Hatch 绘制 case）
- Test: `tests/CadAgent.View.Tests/RenderModelTests.cs` + `tests/CadAgent.View.Tests/CanvasHeadlessTests.cs`（扩展）

**Interfaces:**
- Consumes: Task 1 OcsMapper；ACadSharp `Dimension.Block`（BlockRecord?，探针核定可设+DXF 往返保留匿名块 *Dnn）、`Hatch.Paths`（BoundaryPath 集合）、`Hatch.BoundaryPath.GetPoints(16)`→IEnumerable<XY>（弧段插值）、`Hatch.IsSolid`、`Hatch.Normal`
- Produces: `RenderKind.Hatch`；`RenderItem` 末尾加可选参 `bool Fill = false`（既有构造点全部兼容）；Hatch Points 约定：**各边界路径以 (NaN,NaN) 分隔**（canvas EvenOdd 填充跨路径挖岛）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Dimension_WithAnonymousBlock_ExpandsBlockEntities()
{
    var doc = new CadDocument();
    var blk = new BlockRecord("*D1");
    blk.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
    blk.Entities.Add(new TextEntity { InsertPoint = new XYZ(5, 2, 0), Value = "10", Height = 2.5 });
    doc.BlockRecords.Add(blk);
    var dim = new DimensionLinear { Block = blk, DefinitionPoint = new XYZ(0, 0, 0), InsertionPoint = new XYZ(5, 5, 0) };
    doc.Entities.Add(dim);
    // 经 DXF 往返（探针核定匿名块保留）——镜像既有夹具模式写临时文件再 Load
    var items = BuildViaDxfRoundTrip(doc); // 测试辅助：DxfWriter.Write(fs,doc)→DrawingSession.Load；读既有夹具逐字镜像
    Assert.Equal(2, items.Count);
    Assert.Contains(items, i => i.Kind == RenderKind.Line);
    Assert.Contains(items, i => i.Kind == RenderKind.Text && i.Text == "10");
    Assert.All(items, i => Assert.StartsWith(dim.Handle.ToString(), i.Handle)); // 子图元句柄后缀口径同 Insert
}

[Fact]
public void Dimension_NullBlock_SkippedAndCounted()
{
    var doc = new CadDocument();
    doc.Entities.Add(new DimensionLinear());
    var items = RenderModelBuilder.Build(LoadRoundTrip(doc), out int skipped);
    Assert.Empty(items);
    Assert.Equal(1, skipped); // 诚实计数，不假装已渲染
}

[Fact]
public void Hatch_SolidSquare_ProducesFilledHatchItem()
{
    var doc = new CadDocument();
    var h = new Hatch { IsSolid = true };
    h.Paths.Add(SquarePath(0, 0, 10, 10)); // 辅助：4 条 Line 边构造 BoundaryPath（探针核定 new Hatch.BoundaryPath.Line(new Line{...})）
    doc.Entities.Add(h);
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.Equal(RenderKind.Hatch, item.Kind);
    Assert.True(item.Fill);
    Assert.Equal(4, item.Points.Count(p => !double.IsNaN(p.X))); // 方形 4 顶点（GetPoints 可能重复闭合点——断言用 >=4 并按实际修正）
}

[Fact]
public void Hatch_TwoPaths_NaNSeparated()
{
    var doc = new CadDocument();
    var h = new Hatch { IsSolid = true };
    h.Paths.Add(SquarePath(0, 0, 10, 10));
    h.Paths.Add(SquarePath(3, 3, 7, 7)); // 岛
    doc.Entities.Add(h);
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.Contains(item.Points, p => double.IsNaN(p.X)); // 路径分隔符
}

[Fact]
public void Hatch_MirroredNormal_PointsMirrored()
{
    var doc = new CadDocument();
    var h = new Hatch { IsSolid = true, Normal = new XYZ(0, 0, -1) };
    h.Paths.Add(SquarePath(1, 1, 3, 3));
    doc.Entities.Add(h);
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.All(item.Points.Where(p => !double.IsNaN(p.X)), p => Assert.True(p.X < 0));
}
```

Canvas headless 测试（CanvasHeadlessTests.cs 追加，镜像既有帧测试模式）：

```csharp
[AvaloniaFact]
public void Hatch_Solid_RenderedFilled_DoesNotThrow()
{
    // 构造含 Hatch RenderItem 的 CanvasViewModel，渲染一帧，断言无异常+视觉树存活
    // （像素级填充断言镜像既有 headless 帧测试的断言粒度——读现有测试逐字镜像其模式）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter "Dimension_With|Hatch_Solid"`
Expected: FAIL（Dimension/Hatch 当前 skip）

- [ ] **Step 3: 实现**

RenderModel.cs：
1. `public enum RenderKind { Line, Circle, Arc, Polyline, Text, Hatch }`
2. RenderItem 记录末尾加可选参：`string Handle, IReadOnlyList<double>? DashPattern = null, bool Fill = false)`（DashPattern 为 Task 3 预置——本任务先加避免二次改记录；注释说明两参约定）。XML doc 补 Hatch Points NaN 分隔约定。
3. Append 加 case（在 Insert case 后、default 前）：

```csharp
            case Dimension dim:
            {
                // 尺寸标注经匿名块（*Dnn）展开——块内即厂商绘制的标注几何（延伸线/尺寸线/箭头/文本），
                // 探针核定 DXF 往返保留。块缺失（未生成标注几何）→诚实 skip+计数。
                if (dim.Block is null) { skipped++; break; }
                int childIndex = 0;
                foreach (Entity child in dim.Block.Entities)
                    Append(child, items, ref skipped, t, rotationDeg, heightScale, byBlockColor, depth + 1,
                        handle + ":" + childIndex++.ToString(CultureInfo.InvariantCulture));
                break;
            }

            case Hatch hatch:
            {
                var (ocs, _) = OcsMapper(hatch.Normal);
                var pts = new List<(double X, double Y)>();
                foreach (Hatch.BoundaryPath path in hatch.Paths)
                {
                    if (pts.Count > 0) pts.Add((double.NaN, double.NaN)); // 路径分隔（EvenOdd 挖岛）
                    foreach (XY p in path.GetPoints(16)) // 弧段 16 点插值（探针核定）
                        pts.Add(t.Apply(ocs.Apply(p.X, p.Y)));
                }
                if (pts.Count == 0) { skipped++; break; }
                // 图案填充（非 solid）渲染边界轮廓；图案线生成 deferred（spec 注记，不假装）
                items.Add(new RenderItem(RenderKind.Hatch, pts, layer, color, null, 0, 0, handle, Fill: hatch.IsSolid));
                break;
            }
```

4. Bounds：Hatch 与 Polyline 同处理（遍历 Points，跳过 NaN）。读现有 Bounds 的 default/Polyline 分支，把 Hatch 并入同路径并加 `if (double.IsNaN(x)) continue;` 守卫。

DrawingCanvas.cs DrawItem 加 case：

```csharp
            case RenderKind.Hatch:
            {
                if (item.Points.Count < 3) break;
                var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (StreamGeometryContext g = geo.Open())
                {
                    bool inFigure = false;
                    foreach ((double x, double y) in item.Points)
                    {
                        if (double.IsNaN(x))
                        {
                            if (inFigure) g.EndFigure(true);
                            inFigure = false;
                            continue;
                        }
                        if (!inFigure) { g.BeginFigure(ScreenPoint((x, y)), item.Fill); inFigure = true; }
                        else g.LineTo(ScreenPoint((x, y)));
                    }
                    if (inFigure) g.EndFigure(true);
                }
                context.DrawGeometry(item.Fill ? pen.Brush : null, pen, geo);
                break;
            }
```

- [ ] **Step 4: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS（新测试+既有全绿；既有 RenderItem 构造点因可选参兼容不受影响）

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs src/CadAgent.View/Controls/DrawingCanvas.cs tests/CadAgent.View.Tests/RenderModelTests.cs tests/CadAgent.View.Tests/CanvasHeadlessTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): Dimension block expansion + Hatch boundary/solid rendering (EvenOdd islands)"
```

---

### Task 3: 线型虚实（dash）

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`（ResolveDash + 各 case 传 DashPattern）
- Modify: `src/CadAgent.View/Controls/DrawingCanvas.cs`（DrawItem 入口 pen 克隆 DashStyle）
- Test: `tests/CadAgent.View.Tests/RenderModelTests.cs` + `CanvasHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 2 RenderItem.DashPattern 可选参；ACadSharp `Entity.LineType`（LineType?）、`Entity.LineTypeScale`（double，默认 1）、`LineType.Segments`（IEnumerable<LineType.Segment>，Length 正=划/负=空/0=点，IsComplex/HasShapes 标志）、`Layer.LineType`（ByLayer 回退）
- Produces: `private static IReadOnlyList<double>? ResolveDash(Entity e)`（ drawing 单位 on/off 交替序列；null=实线）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Line_DashedLineType_ProducesDashPattern()
{
    var doc = new CadDocument();
    var lt = new LineType("DASHED");
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = 5 });
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = -2 });
    doc.LineTypes.Add(lt);
    doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(20, 0, 0), LineType = lt });
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.NotNull(item.DashPattern);
    Assert.Equal(new double[] { 5, 2 }, item.DashPattern!.ToArray()); // 绝对值，on/off 交替
}

[Fact]
public void Line_Continuous_NoDash()
{
    var doc = new CadDocument();
    doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(20, 0, 0) });
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.Null(item.DashPattern);
}

[Fact]
public void Line_ByLayerLineType_ResolvesLayerDash()
{
    var doc = new CadDocument();
    var lt = new LineType("DASH2");
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = 3 });
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = -1 });
    doc.LineTypes.Add(lt);
    var layer = new Layer("DASHLAYER") { LineType = lt };
    doc.Layers.Add(layer);
    doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(20, 0, 0), Layer = layer });
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.Equal(new double[] { 3, 1 }, item.DashPattern!.ToArray());
}

[Fact]
public void Line_LineTypeScale_MultipliesPattern()
{
    var doc = new CadDocument();
    var lt = new LineType("DASH3");
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = 4 });
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = -2 });
    doc.LineTypes.Add(lt);
    doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(20, 0, 0), LineType = lt, LineTypeScale = 2 });
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.Equal(new double[] { 8, 4 }, item.DashPattern!.ToArray());
}

[Fact]
public void Line_ComplexLineType_SolidFallback()
{
    var doc = new CadDocument();
    var lt = new LineType("FENCELINE1");
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = 5 });
    ((ICollection<LineType.Segment>)lt.Segments).Add(new LineType.Segment { Length = -2 });
    lt.HasShapes = true; // 复杂线型（形/文字）→实线降级（shapes deferred，注释+spec 注记）
    doc.LineTypes.Add(lt);
    doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(20, 0, 0), LineType = lt });
    var item = Assert.Single(RenderModelBuilder.Build(LoadRoundTrip(doc)));
    Assert.Null(item.DashPattern);
}
```

（HasShapes 可设性若编译失败→改用 lt.IsComplex 或构造含 Shape 段；以 API 实际为准，测试意图不变：复杂线型实线降级。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter Dash`
Expected: FAIL（DashPattern 恒 null）

- [ ] **Step 3: 实现**

RenderModel.cs：

```csharp
    /// <summary>
    /// 线型图案解析（drawing 单位 on/off 交替）：实体 LineType 优先，ByLayer 回退图层；
    /// Continuous/ByLayer/ByBlock/空段→null（实线）。段 Length 正=划、负=空、0=点
    /// （点划近似 0.1×scale，注释说明）。复杂线型（IsComplex/HasShapes/含 Shape 段）
    /// →null 实线降级——形/文字嵌入 deferred（不假装）。LineTypeScale 乘入。
    /// 奇数段→补末段凑偶（Avalonia DashStyle 要求 on/off 对）。
    /// </summary>
    private static IReadOnlyList<double>? ResolveDash(Entity e)
    {
        LineType? lt = e.LineType;
        if (lt is null || lt.Name == "ByLayer") lt = e.Layer?.LineType;
        if (lt is null || lt.Name is "Continuous" or "ByLayer" or "ByBlock") return null;
        if (lt.IsComplex || lt.HasShapes) return null;
        var segs = lt.Segments.ToList();
        if (segs.Count == 0 || segs.Any(s => s.IsShape)) return null;
        double scale = e.LineTypeScale <= 0 ? 1 : e.LineTypeScale;
        var pattern = new List<double>();
        foreach (LineType.Segment s in segs)
            pattern.Add(s.Length == 0 ? 0.1 * scale : Math.Max(Math.Abs(s.Length) * scale, 0.01));
        if (pattern.Count % 2 == 1) pattern.Add(pattern[^1]);
        return pattern;
    }
```

Append 各产出 RenderItem 的 case（Line/Arc/Circle/LwPolyline/Polyline2D/Text/MText/Hatch）传 `DashPattern: ResolveDash(e)`——统一在 Append 入口算一次 `IReadOnlyList<double>? dash = ResolveDash(e);` 各 case 构造传入（Insert 子图元各自解析——child 传入 Append 自然生效）。tessellated Polyline（OCS 圆/弧）同样传 dash。

DrawingCanvas.cs DrawItem 入口（switch 前）：

```csharp
        if (item.DashPattern is { Count: > 0 } dp)
        {
            // drawing 单位 → 设备像素 → pen 厚度倍数（Avalonia DashStyle 口径）。
            // 亚像素图案（任一段 < 0.25px）→实线降级防摩尔纹；超长图案总量守卫防性能退化。
            double[] device = dp.Select(d => d * ViewModel.Scale).ToArray();
            if (device.All(d => d >= 0.25) && device.Sum() < 1e6)
                pen = new Pen(pen.Brush, pen.Thickness)
                {
                    DashStyle = new DashStyle(device.Select(d => d / pen.Thickness), 0)
                };
        }
```

- [ ] **Step 4: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs src/CadAgent.View/Controls/DrawingCanvas.cs tests/CadAgent.View.Tests/RenderModelTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): linetype dash patterns (ByLayer fallback, scale, complex solid degradation)"
```

---

### Task 4: Move 处理器扩展 + KindDocs 共享提取

**Files:**
- Modify: `src/CadAgent.Data/Handlers/EditHandlers.cs:22-47`（MoveEntityHandler switch 扩展）
- Create: `src/CadAgent.Data/Handlers/HandlerKindDocs.cs`（CLI KindDocs 文本逐字提取为公共常量）
- Modify: `src/CadAgent.Cli/CommandRouter.Agent.cs`（KindDocs 常量改引用 HandlerKindDocs.Default；move 行契约文本更新）
- Test: `tests/CadAgent.Data.Tests/`（MoveEntityHandler 测试扩展——读既有 handler 测试文件镜像模式）+ `tests/CadAgent.Cli.Tests/`（契约文本断言若有则更新）

**Interfaces:**
- Consumes: ACadSharp 实体点属性（Circle.Center/Arc.Center XYZ、MText.InsertPoint XYZ、Insert.InsertPoint XYZ、LwPolyline.Vertex.Location XY、Polyline2D.Vertex2D.Location XYZ）
- Produces: `public static class HandlerKindDocs { public const string Default = """..."""; }`（Task 10 视图预览消费）；move-entity 支持 Line/Text/Circle/Arc/MText/Insert/LwPolyline/Polyline2D

- [ ] **Step 1: 写失败测试（Data.Tests，镜像既有 handler 测试夹具）**

```csharp
[Theory]
[InlineData("Circle")]
[InlineData("Arc")]
[InlineData("MText")]
[InlineData("Insert")]
[InlineData("LwPolyline")]
[InlineData("Polyline2D")]
public void Move_SupportedEntity_TranslatesPreservingZ(string kind)
{
    // 夹具：按 kind 构造实体（Z=7 高程），经 DrawingSession DXF 往返加载，
    // MoveEntityHandler.Apply(session, {handle, dx:3, dy:4}) → StepResult.Ok，
    // 读回断言：首点 X+3/Y+4/Z 保持 7。各 kind 首点取法：
    // Circle/Arc→Center；MText/Insert→InsertPoint；LwPolyline→Vertices[0].Location（XY，Z 经 Elevation 保持）；
    // Polyline2D→Vertices[0].Location。Insert 需 BlockRecord（镜像既有 Insert 夹具）。
    // 具体夹具代码镜像 Data.Tests 既有 handler 测试的 session 构造法——读后逐字套用。
}

[Fact]
public void Move_UnsupportedEntity_StillHonestStepFailed()
{
    // Hatch 等仍不支持 → StepResult(false, "move 暂不支持实体类型 Hatch")——诚实失败口径不变
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter Move_Supported`
Expected: FAIL（当前仅 Line/Text，其余 step-failed）

- [ ] **Step 3: 实现 MoveEntityHandler 扩展**

switch 追加（Line/Text case 后，default 前；Z 保持注释同 Line 先例）：

```csharp
            case Circle circle:
                circle.Center = new XYZ(circle.Center.X + dx, circle.Center.Y + dy, circle.Center.Z);
                break;
            case Arc arc:
                arc.Center = new XYZ(arc.Center.X + dx, arc.Center.Y + dy, arc.Center.Z);
                break;
            case MText mtext:
                mtext.InsertPoint = new XYZ(mtext.InsertPoint.X + dx, mtext.InsertPoint.Y + dy, mtext.InsertPoint.Z);
                break;
            case Insert insert:
                insert.InsertPoint = new XYZ(insert.InsertPoint.X + dx, insert.InsertPoint.Y + dy, insert.InsertPoint.Z);
                break;
            case LwPolyline lw:
                foreach (LwPolyline.Vertex v in lw.Vertices)
                    v.Location = new XY(v.Location.X + dx, v.Location.Y + dy);
                break;
            case Polyline2D p2:
                foreach (Vertex2D v in p2.Vertices)
                    v.Location = new XYZ(v.Location.X + dx, v.Location.Y + dy, v.Location.Z);
                break;
```

（Vertex.Location 可设性以编译为准；若只读→改用 ACadSharp 实际 API（如顶点重建），测试意图不变。）

- [ ] **Step 4: HandlerKindDocs 提取**

读 `src/CadAgent.Cli/CommandRouter.Agent.cs` 的 KindDocs 常量全文（约 L15-30），逐字移入新文件：

```csharp
namespace CadAgent.Data.Handlers;

/// <summary>步骤类型参数契约文本（CLI agent 与视图计划预览共用；LLM system prompt 消费）。</summary>
public static class HandlerKindDocs
{
    public const string Default = """
        <KindDocs 原文逐字，仅 move 行改为：>
        move-entity: 移动实体 {handle: number|string, dx,dy: number}（Line/Text/Circle/Arc/MText/Insert/LwPolyline/Polyline2D）
        <其余行逐字不变>
        """;
}
```

CommandRouter.Agent.cs：原常量删除，使用点改 `HandlerKindDocs.Default`（grep KindDocs 全部引用）。CLI 既有契约测试若断言"仅 Line/Text"→更新为新清单（sanctioned：契约随处理器扩展）。

- [ ] **Step 5: 运行确认绿 + 回归**

Run: `dotnet test tests/CadAgent.Data.Tests --filter Move`
Run: `dotnet test tests/CadAgent.Cli.Tests`
Expected: PASS（Cli 全量——agent 契约文本变更不破坏既有 E2E）

- [ ] **Step 6: 提交**

```bash
git add src/CadAgent.Data/Handlers/EditHandlers.cs src/CadAgent.Data/Handlers/HandlerKindDocs.cs src/CadAgent.Cli/CommandRouter.Agent.cs tests/CadAgent.Data.Tests tests/CadAgent.Cli.Tests
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: move-entity supports 8 entity types; HandlerKindDocs shared for view preview"
```

---

### Task 5: 审计时间线 live-rebuild + _diffBusy 硬闸

**Files:**
- Modify: `src/CadAgent.View/ViewModels/AuditViewModel.cs`（保留 IAuditStore + Rebuild + TimelineChanged）
- Modify: `src/CadAgent.View/MainWindow.cs`（RefreshAfterEdit 接线 + _diffBusy 字段与入口守卫）
- Test: `tests/CadAgent.View.Tests/AuditViewModelTests.cs` + `MainWindowHeadlessTests.cs`（扩展）

**Interfaces:**
- Consumes: 既有 AuditViewModel 构造签名（IAuditStore auditStore, SnapshotStore, DrawingSession, CanvasViewModel?, SemaphoreSlim?）；MainWindow._editBusy 硬闸模式（镜像）
- Produces: `public void AuditViewModel.Rebuild()`（重读审计库重建 Entries/Transactions，保留 SelectedTxId 若仍存在）；`public event Action? TimelineChanged`；MainWindow `private bool _diffBusy`

- [ ] **Step 1: 写失败测试**

AuditViewModelTests.cs：

```csharp
[Fact]
public void Rebuild_AfterNewTx_PicksUpNewEntries()
{
    // 夹具镜像既有 AuditViewModelTests：临时 audit.db + session；构造 VM（初始 0 tx）；
    // 经 audit.Append 写入 tx-start/tx-commit 一组（PlanRef 关联，镜像既有夹具写法）；
    // VM.Rebuild() → Transactions 含新 tx；TimelineChanged 触发计数 >= 1。
}

[Fact]
public void Rebuild_SelectedTxStillExists_PreservesSelection()
{
    // 既有 tx 选中（SelectTx）→ 追加新 tx → Rebuild → SelectedTxId 不变
}

[Fact]
public void Rebuild_AuditStoreCorrupt_ErrorMessageNoThrow()
{
    // 损坏 audit.db（写垃圾字节）→ Rebuild → ErrorMessage 非 null，不抛（镜像构造期降级）
}
```

MainWindowHeadlessTests.cs（镜像既有 _editBusy 测试模式）：

```csharp
[AvaloniaFact]
public async Task DiffLoading_EditClicks_IgnoredByHardGuard()
{
    // 注入会话闸并持闸 → Diff 加载 in-flight（_diffBusy 置位）→
    // 画布工具点击/撤销/删除确认一律忽略（审计无新 view-edit）→ 释闸 → Diff 完成。
    // 镜像既有"注入会话闸持闸制造确定性 in-flight 编辑"测试的构造法。
}

[AvaloniaFact]
public async Task EditCompleted_AuditTimeline_LiveRebuilds()
{
    // headless 窗口：执行一次 DrawLineAsync → RefreshAfterEdit 链 →
    // AuditList.ItemsSource 含新 tx（无需重开窗口）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter "Rebuild_After|DiffLoading_Edit"`
Expected: FAIL（Rebuild 不存在；_diffBusy 不存在）

- [ ] **Step 3: 实现 AuditViewModel.Rebuild**

1. 字段 `private readonly IAuditStore _auditStore;` 构造期赋值。
2. 构造期分组逻辑提取为 `private static (List<AuditEntryVm> Entries, List<TxGroup> Groups) ReadAll(IAuditStore store)`（逐字移动现有 foreach 逻辑）。
3. `Entries`/`Transactions` 改 `{ get; private set; }`；构造期调 ReadAll（try/catch 降级不变）。
4. 新增：

```csharp
    /// <summary>编辑/撤销后重读审计库（live-rebuild，T9 承接遗留闭环）。
    /// 失败→ErrorMessage 降级（镜像构造期）；选中 tx 仍存在则保留。</summary>
    public void Rebuild()
    {
        try
        {
            var (entries, groups) = ReadAll(_auditStore);
            Entries = entries;
            Transactions = groups;
            if (SelectedTxId is string sel && !groups.Any(g => g.TxId == sel))
                SelectedTxId = null;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        TimelineChanged?.Invoke();
    }

    /// <summary>Rebuild 完成（成功或失败）→ UI 刷新列表。在调用方上下文触发。</summary>
    public event Action? TimelineChanged;
```

（SelectedTxId 可设性：读现有定义——若 get-only 改 private set；SelectTx 逻辑不动。ErrorMessage 同理改 private set。）

- [ ] **Step 4: MainWindow 接线 + _diffBusy**

1. RefreshAfterEdit 末尾（DiffToggle 回退后）追加：

```csharp
        // T9 承接闭环：审计时间线 live-rebuild（编辑/撤销/回滚后无需重开窗口）
        Audit?.Rebuild();
```

2. 审计面板装配处（auditList 创建后）订阅：

```csharp
        audit.TimelineChanged += () =>
        {
            if (AuditList is not null && Audit is not null)
                AuditList.ItemsSource = Audit.Transactions;
        };
```

3. `private bool _diffBusy;` 字段（_editBusy 旁，同型 XML doc：Diff 加载期间画布点击/工具切换/撤销/删除/改层一律忽略——IsEnabled 只是视觉提示，标志位才是硬闸）。
4. ApplyDiffAsync：入口 `if (_diffBusy) return;`，置位 `_diffBusy = true;`，现有 finally 块复位 `_diffBusy = false;`（读现有方法全文，保持过期结果丢弃/切换回退逻辑不动）。
5. 所有 _editBusy 守卫点镜像加 `if (_diffBusy) return;`（grep _editBusy 全部使用点：OnCanvasToolClick/OnDeleteClick/OnUndoClick/SetActiveTool/OnEditLayerSelected 等——逐点并列加）。

- [ ] **Step 5: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS

- [ ] **Step 6: 提交**

```bash
git add src/CadAgent.View/ViewModels/AuditViewModel.cs src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/AuditViewModelTests.cs tests/CadAgent.View.Tests/MainWindowHeadlessTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): audit timeline live-rebuild + _diffBusy structural hard guard"
```

---

### Task 6: Issues 审计 parity + in-flight 守卫 + ViewRuleEngine 提取

**Files:**
- Create: `src/CadAgent.View/Services/ViewRuleEngine.cs`
- Modify: `src/CadAgent.View/ViewModels/IssuesViewModel.cs`
- Modify: `src/CadAgent.View/MainWindow.cs`（Issues 构造传 writeAudit + signoffIdentity）
- Test: `tests/CadAgent.View.Tests/IssuesViewModelTests.cs`（扩展）

**Interfaces:**
- Consumes: CLI check 审计形状（CommandRouter.cs:690-700：Source="check" Kind="check-run" params{rulesRun,rulesSkipped,issueCount,mandatoryViolations,skippedReasons,overlayPresent,adjudicationHash,rulePackVersion}，前后哈希一致）；IssuesViewModel 既有构造（session, projectDir, skillDirs, canvas?, sessionGate?）
- Produces: `public static class ViewRuleEngine { public static RuleEngine Create(); }`（检查器装配+重复守卫，Issues/Edit 共用）；IssuesViewModel 构造末尾加可选参 `IAuditStore? audit = null, IIdentityProvider? identity = null`；`private readonly SemaphoreSlim _refreshGate = new(1, 1)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task RefreshAsync_WritesCheckRunAudit_MirroringCliShape()
{
    // 夹具：临时项目 + audit.db + 图纸（镜像既有 IssuesViewModelTests）；
    // VM 传 audit + identity（Draw 角色）；RefreshAsync →
    // audit.ReadAll 含 Source="view-check" Kind="check-run" 条目；
    // paramsJson 含 rulesRun/issueCount/mandatoryViolations/overlayPresent/adjudicationHash/rulePackVersion；
    // BeforeHash == AfterHash（只读检查）。
}

[Fact]
public async Task RefreshAsync_ConcurrentCalls_SecondReturnsImmediately()
{
    // 注入会话闸并持闸 → refresh1 = RefreshAsync()（阻塞在 Evaluate 的 sessionGate）；
    // refresh2 = RefreshAsync() → 立即完成（_refreshGate WaitAsync(0) 失败）；
    // 释闸 → refresh1 完成；审计 check-run 恰 1 条（第二次未评估）。
}

[Fact]
public async Task RefreshAsync_AuditWriteFails_ErrorMessageNoCrash()
{
    // 注入抛异常的 IAuditStore fake → RefreshAsync → ErrorMessage 含"审计"，
    // Issues 清空（镜像 CLI check 失败语义），不抛。
}

[Fact]
public async Task RefreshAsync_NoAuditInjected_BehaviorUnchanged()
{
    // 不传 audit（既有测试口径）→ 评估照常，无审计写入，不抛
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter "RefreshAsync_Writes|RefreshAsync_Concurrent"`
Expected: FAIL（无审计写入；无并发守卫）

- [ ] **Step 3: ViewRuleEngine 提取**

新文件（IssuesViewModel.CreateEngine 逐字移动，改 public）：

```csharp
namespace CadAgent.View.Services;

/// <summary>
/// 视图规范引擎装配（Issues 刷新与 Edit 回归共用）——逐字镜像 CLI
/// CommandRouter.StandardsEngine 注册列表与重复 CheckType 装配守卫。
/// </summary>
public static class ViewRuleEngine
{
    public static RuleEngine Create()
    {
        // <CreateEngine 原 body 逐字：checkers 数组 + 重复守卫 + return new RuleEngine(checkers)>
    }
}
```

（using 按原文件实际命名空间补全；IssuesViewModel.CreateEngine 删除改调 ViewRuleEngine.Create()。）

- [ ] **Step 4: IssuesViewModel 改造**

1. 构造加可选参 `IAuditStore? audit = null, IIdentityProvider? identity = null`，字段保存。
2. `_refreshGate` 字段；RefreshAsync 入口：

```csharp
        // in-flight 硬闸（结构性，不依赖按钮 IsEnabled UI 态）：并发刷新第二次立即返回。
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            // <现有 try/catch body>
        }
        finally { _refreshGate.Release(); }
```

3. Evaluate 改返回 `private sealed record EvalResult(CheckSummary Summary, string RulePackVersion, string AdjudicationHash, bool OverlayPresent)`——现有加载逻辑末尾组装（ruleSet.RulePackVersion / adjudications.Fingerprint() / overlay.Annotations.Count > 0）。
4. RefreshAsync 成功分支（summary 发布后）审计 parity：

```csharp
            if (_audit is not null && _identity is not null)
            {
                try
                {
                    string hash = _session.ComputeHash(); // 只读检查前后哈希一致（镜像 CLI check）
                    var op = _identity.Current;
                    _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                        "view", "view-check", "check-run", _session.SourcePath,
                        JsonSerializer.Serialize(new
                        {
                            rulesRun = eval.Summary.RulesRun,
                            rulesSkipped = eval.Summary.RulesSkipped,
                            issueCount = eval.Summary.Issues.Count,
                            mandatoryViolations = MandatoryCount,
                            skippedReasons = eval.Summary.StructuredSkips?.Select(s => new { category = s.Category, ruleId = s.RuleId, message = s.Message }).ToArray() ?? Array.Empty<object>(),
                            overlayPresent = eval.OverlayPresent,
                            adjudicationHash = eval.AdjudicationHash,
                            rulePackVersion = eval.RulePackVersion
                        }),
                        hash, hash, "", ""));
                }
                catch (Exception ex)
                {
                    // 审计先行（spec §9）：检查审计写入失败=检查失败（镜像 CLI check exit 1 语义），
                    // 清空列表+ErrorMessage；不崩 UI。
                    Issues = Array.Empty<IssueEntry>();
                    MandatoryCount = 0;
                    ErrorMessage = $"检查审计写入失败: {ex.Message}";
                }
            }
```

（AuditEntry 构造参数序镜像 EditViewModel.ApplyCore 现有用法：ts, account, role, session, source, kind, target, paramsJson, beforeHash, afterHash, snapshotRef, planRef。sessionId 用 "view" 常量——IssuesVM 无 CLI session 注入，注释说明口径。skippedReasons 字段名与 CLI StructuredSkipAuditArray 核对——读 CLI 该方法逐字镜像。）
5. MainWindow Issues 构造点传 `audit: writeAudit, identity: signoffIdentity`（核对 Issues 构造位置相对 writeAudit 声明的顺序，必要时移动声明）。

- [ ] **Step 5: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS（既有 Issues 测试不传 audit→行为不变）

- [ ] **Step 6: 提交**

```bash
git add src/CadAgent.View/Services/ViewRuleEngine.cs src/CadAgent.View/ViewModels/IssuesViewModel.cs src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/IssuesViewModelTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): issues-refresh check-run audit parity + in-flight hard guard + ViewRuleEngine"
```

---

### Task 7: 视图编辑 run-regression（镜像 CLI）

**Files:**
- Modify: `src/CadAgent.View/ViewModels/EditViewModel.cs`（ApplyCore tx.Ok 后回归检查）
- Modify: `src/CadAgent.View/MainWindow.cs`（Warning 可见性核对，必要时扩展）
- Test: `tests/CadAgent.View.Tests/EditViewModelTests.cs`（扩展）

**Interfaces:**
- Consumes: ViewRuleEngine.Create()（Task 6）；CLI RunRegressionCheck 语义（CommandRouter.cs:263-323：builtin-only、观察性不改 tx 结果、mandatory>0 警告不自动回滚、无论有无违规均审计 check-run、故障也入审计+嵌套守卫）；EditViewModel 既有字段（_audit/_identity/_session/_projectDir/_sessionId）
- Produces: `private string RunRegressionCheck()`（返回警告串，空=无警告）；EditResult.Warning 追加回归警告

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Edit_Success_MandatoryViolation_RegressionWarningAndAudit()
{
    // 夹具：图纸含 builtin 规则违规（读 Cli.Tests 既有 run-regression 测试的违规图纸构造法，
    // 如字高违规 TextEntity）+ adjudications.json 升级该规则 mandatory（镜像既有裁定夹具）；
    // EditViewModel 注入 audit/identity；DrawLineAsync 成功 →
    // EditResult.Warning 含"强制(mandatory)规范违规"与"不自动回滚"；
    // audit 含 Source="view-regression" Kind="check-run"（mandatoryViolations >= 1，前后哈希一致）；
    // tx 仍 Ok（观察性不回滚）。
}

[Fact]
public async Task Edit_Success_CleanDrawing_RegressionAuditNoWarning()
{
    // 合规图纸 → Warning 空（或仅 Yellow 警示）；审计 check-run 仍写入（无论有无违规均审计）
}

[Fact]
public async Task Edit_Success_AdjudicationsCorrupt_RegressionSkippedAuditErrorTxUnaffected()
{
    // adjudications.json 垃圾字节 → tx 仍 Ok；审计含 Error 字段的 check-run；Warning 含"回归检查未完成"
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter Regression`
Expected: FAIL（无回归逻辑）

- [ ] **Step 3: 实现**

ApplyCore 末尾 return 前插入：

```csharp
        if (tx.Ok)
        {
            string regressionWarning = RunRegressionCheck();
            if (regressionWarning.Length > 0)
                gateWarning = gateWarning.Length > 0 ? gateWarning + "\n" + regressionWarning : regressionWarning;
        }
```

新方法（逐字镜像 CLI RunRegressionCheck 语义，Source="view-regression"）：

```csharp
    /// <summary>
    /// 编辑后回归检查（镜像 CLI RunRegressionCheck，spec §9 观察性）：builtin-only 评估，
    /// mandatory 违规→警告不自动回滚（决策权归人）；无论有无违规均审计 check-run；
    /// 自身故障也入审计（嵌套守卫：审计再失败仅降级提示，绝不重抛影响已提交 tx）。
    /// </summary>
    private string RunRegressionCheck()
    {
        bool overlayPresent = false;
        try
        {
            string agentDir = Path.Combine(_projectDir, ".cadagent");
            string overlayPath = Path.Combine(agentDir, "semantic.json");
            var overlay = File.Exists(overlayPath) ? SemanticOverlay.Load(overlayPath) : new SemanticOverlay();
            overlayPresent = overlay.Annotations.Count > 0;
            var adjudications = AdjudicationSet.Load(Path.Combine(agentDir, "adjudications.json"));
            var ruleSet = SkillSet.Compose(BuiltinRules.Load(), Array.Empty<SkillPackage>());
            CheckSummary summary = ViewRuleEngine.Create().Evaluate(ruleSet.AllRules, _session, overlay, adjudications);
            int mandatoryViolations = summary.Issues.Count(i => i.Force == RuleForce.Mandatory);
            string hash = _session.ComputeHash();
            var op = _identity.Current;
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                _sessionId, "view-regression", "check-run", _session.SourcePath,
                JsonSerializer.Serialize(new
                {
                    rulesRun = summary.RulesRun,
                    rulesSkipped = summary.RulesSkipped,
                    issueCount = summary.Issues.Count,
                    mandatoryViolations,
                    skippedReasons = summary.StructuredSkips?.Select(s => new { category = s.Category, ruleId = s.RuleId, message = s.Message }).ToArray() ?? Array.Empty<object>(),
                    overlayPresent,
                    adjudicationHash = adjudications.Fingerprint(),
                    rulePackVersion = ruleSet.RulePackVersion
                }),
                hash, hash, "", ""));
            if (mandatoryViolations > 0)
            {
                string ruleIds = string.Join(", ", summary.Issues
                    .Where(i => i.Force == RuleForce.Mandatory).Select(i => i.RuleId).Distinct());
                return $"警告: 编辑后图纸存在 {mandatoryViolations} 项强制(mandatory)规范违规（规则: {ruleIds}）。事务已提交，不自动回滚——决策权归人，请在问题清单复核并人工裁定或修正。";
            }
            return "";
        }
        catch (Exception ex)
        {
            try
            {
                var op = _identity.Current;
                _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    _sessionId, "view-regression", "check-run", _session.SourcePath,
                    JsonSerializer.Serialize(new { Error = ex.Message, overlayPresent }),
                    "", "", "", ""));
            }
            catch { /* 嵌套守卫：审计再失败不重抛（镜像 CLI 最终审查裁定） */ }
            return $"回归检查未完成: {ex.Message}（事务已提交，不受影响）";
        }
    }
```

（using 补全镜像 IssuesViewModel；AdjudicationSet.Load 损坏会抛→走 catch 审计 Error 路径，与 CLI LoadOrReject 拒绝语义等价但观察性不影响 tx。警告可见性：核对 MainWindow RunEditAsync 对 EditResult.Warning 的现有处理——若已显示则无需改；若仅 Yellow 分支显示→扩展为任何非空 Warning 均入状态栏。）

- [ ] **Step 4: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/ViewModels/EditViewModel.cs src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/EditViewModelTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): post-edit run-regression check with audit parity (mirrors CLI, no auto-rollback)"
```

---

### Task 8: 空图层显示（图层表并集）

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`（LayerColorRgb 公开辅助）
- Modify: `src/CadAgent.View/ViewModels/LayersViewModel.cs`（session 可选参 + 图层表并集）
- Modify: `src/CadAgent.View/MainWindow.cs`（构造/Refresh 传 vm.Session）
- Test: `tests/CadAgent.View.Tests/LayersViewModelTests.cs`（扩展）

**Interfaces:**
- Consumes: ACadSharp `CadDocument.Layers`（Layer 表记录，含空层）、`Layer.Color`；既有 LayersViewModel(renderItems, canvasVM)
- Produces: `public static uint RenderModelBuilder.LayerColorRgb(ACadSharp.Tables.Layer? layer)`（包装既有 LayerColor/ToRgb）；LayersViewModel 构造/Refresh 加可选参 `DrawingSession? session = null`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Layers_EmptyLayerInDoc_ShownWithZeroCount()
{
    // 夹具：doc 含图层 "EMPTY"（无实体）+ "USED"（1 线）；DXF 往返加载 session；
    // renderItems = RenderModelBuilder.Build(session)；
    // new LayersViewModel(items, canvas, session) → Layers 含 EMPTY（EntityCount 0）与 USED（1）；
    // EMPTY 色样 = 图层表颜色（LayerColorRgb），非白 fallback。
}

[Fact]
public void Layers_NoSession_LegacyBehaviorUnchanged()
{
    // 不传 session（既有测试口径）→ 仅 renderItems 图层出现（行为保持）
}

[Fact]
public void Refresh_WithSession_NewEmptyLayerAppears()
{
    // 编辑新建图层（ChangeLayer 到新层）后 Refresh(items, session) → 新层出现；
    // HiddenLayers 隐藏状态原样保留（既有语义）。
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter Layers_Empty`
Expected: FAIL（空层不出现）

- [ ] **Step 3: 实现**

RenderModel.cs：`private static uint LayerColor(...)` 改公开包装：

```csharp
    /// <summary>图层表颜色→0xRRGGBB（空图层色样用；null→白 fallback）。</summary>
    public static uint LayerColorRgb(ACadSharp.Tables.Layer? layer) => LayerColor(layer);
```

LayersViewModel.cs：

```csharp
    public LayersViewModel(IReadOnlyList<RenderItem> renderItems, CanvasViewModel canvasVM, DrawingSession? session = null)
    {
        _canvas = canvasVM;
        Layers = BuildEntries(renderItems, canvasVM, session);
        foreach (LayerEntry entry in Layers) _names.Add(entry.Name);
    }

    public void Refresh(IReadOnlyList<RenderItem> renderItems, DrawingSession? session = null)
    {
        Layers = BuildEntries(renderItems, _canvas, session);
        _names.Clear();
        foreach (LayerEntry entry in Layers) _names.Add(entry.Name);
        _canvas.Invalidate();
    }

    private static IReadOnlyList<LayerEntry> BuildEntries(
        IReadOnlyList<RenderItem> renderItems, CanvasViewModel canvasVM, DrawingSession? session)
    {
        var groups = new Dictionary<string, (int Count, uint Color)>(StringComparer.OrdinalIgnoreCase);
        foreach (RenderItem item in renderItems)
        {
            groups[item.Layer] = groups.TryGetValue(item.Layer, out var g)
                ? (g.Count + 1, g.Color)
                : (1, item.ColorRgb);
        }
        // 图层表并集（T9 承接闭环）：空图层也显示（计数 0，色样取图层表颜色）。
        if (session is not null)
        {
            foreach (ACadSharp.Tables.Layer layer in session.Doc.Layers)
            {
                if (!groups.ContainsKey(layer.Name))
                    groups[layer.Name] = (0, RenderModelBuilder.LayerColorRgb(layer));
            }
        }
        return groups
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new LayerEntry(kv.Key, kv.Value.Count, kv.Value.Color, canvasVM))
            .ToList();
    }
```

MainWindow：`new LayersViewModel(items, Canvas, vm.Session)`；RefreshAfterEdit 内 `Layers.Refresh(items, vm.Session)`。

- [ ] **Step 4: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs src/CadAgent.View/ViewModels/LayersViewModel.cs src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/LayersViewModelTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): empty layers shown from doc layer table (zero count, table color)"
```

---

### Task 9: 编辑 UX 收尾（字高/缩放/旋转输入 + Delete 模态确认）

**Files:**
- Create: `src/CadAgent.View/Services/ConfirmDialog.cs`
- Modify: `src/CadAgent.View/MainWindow.cs`（三个输入框 + Delete 模态 + seam）
- Test: `tests/CadAgent.View.Tests/EditPanelHeadlessTests.cs`（扩展）

**Interfaces:**
- Consumes: EditViewModel.AddTextAsync(x,y,text,height,layer?) / InsertBlockAsync(block,x,y,scale,rotation)（VM 已支持，UI 未暴露）；MainWindow.OnDeleteClick 两段式现状；_editBusy 硬闸
- Produces: `internal sealed class ConfirmDialog : Window`（构造(string message)；`ShowDialog<bool>` 经 Close(true/false)）；MainWindow `internal Func<string, Task<bool>>? ConfirmDeleteOverride`（headless 测试 seam；null→真模态）；`internal TextBox? TextHeightInput / BlockScaleInput / BlockRotationInput`（测试可达，镜像 EditTextInput 既有模式）

- [ ] **Step 1: 写失败测试（headless，镜像既有 EditPanelHeadlessTests 模式）**

```csharp
[AvaloniaFact]
public async Task TextTool_CustomHeight_UsedInEdit()
{
    // 窗口构造 → TextHeightInput.Text = "7" → Text 工具画布点击放置 →
    // 渲染图元 TextHeight == 7（或审计 view-edit args.height == 7）
}

[AvaloniaFact]
public async Task TextTool_InvalidHeight_FallsBackDefault()
{
    // TextHeightInput.Text = "abc" → 放置 → TextHeight == 3.5（默认 fallback，不崩）
}

[AvaloniaFact]
public async Task InsertBlockTool_ScaleRotation_UsedInEdit()
{
    // BlockScaleInput.Text = "2"，BlockRotationInput.Text = "90"（度）→ 插块放置 →
    // 审计 view-edit args：scale == 2，rotation ≈ Math.PI/2（UI 度→handler 弧度契约）
}

[AvaloniaFact]
public async Task Delete_ConfirmOverrideFalse_NoTx()
{
    // ConfirmDeleteOverride = _ => Task.FromResult(false) → Delete 两段式第二击 →
    // 无 delete-entity tx（审计无 tx-start），实体仍在
}

[AvaloniaFact]
public async Task Delete_ConfirmOverrideTrue_TxCommitted()
{
    // override true → 第二击 → delete-entity tx commit，实体消失
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter "TextTool_Custom|Delete_Confirm"`
Expected: FAIL（输入框/seam 不存在）

- [ ] **Step 3: ConfirmDialog 实现**

```csharp
using Avalonia.Controls;
using Avalonia.Layout;

namespace CadAgent.View.Services;

/// <summary>
/// Delete 确认模态（T9 承接遗留闭环）：ShowDialog&lt;bool&gt; 阻塞 owner，
/// 确认删除→Close(true)，取消→Close(false)。headless 测试经 MainWindow
/// ConfirmDeleteOverride seam 注入，不依赖模态消息泵；真机模态走验证协议。
/// </summary>
internal sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string message)
    {
        Title = "确认删除";
        Width = 360;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确认删除" };
        ok.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close(false);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
```

- [ ] **Step 4: MainWindow 接线**

1. 工具栏装配（textInput/blockInput 旁）加三个输入框：

```csharp
        var heightInput = new TextBox { Watermark = "字高", Width = 60, Margin = new Thickness(2, 0), Text = "3.5" };
        TextHeightInput = heightInput;
        toolbar.Children.Add(heightInput);
        var scaleInput = new TextBox { Watermark = "缩放", Width = 60, Margin = new Thickness(2, 0), Text = "1" };
        BlockScaleInput = scaleInput;
        toolbar.Children.Add(scaleInput);
        var rotationInput = new TextBox { Watermark = "旋转°", Width = 60, Margin = new Thickness(2, 0), Text = "0" };
        BlockRotationInput = rotationInput;
        toolbar.Children.Add(rotationInput);
```

（属性声明镜像 EditTextInput 既有模式：`internal TextBox? TextHeightInput { get; }` 等。Text 工具 tooltip 改"字高取输入框（默认 3.5）"。）
2. OnCanvasToolClick Text 分支：`double height = double.TryParse(TextHeightInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h > 0 ? h : DefaultTextHeight;` 传 AddTextAsync。
3. InsertBlock 分支：scale 同型解析（>0 else 1）；rotation 解析度→弧度 `deg * Math.PI / 180.0`（失败→0）；传 InsertBlockAsync。
4. OnDeleteClick 第二击改异步确认（保持 _editBusy/_diffBusy 硬闸与两段式状态机）：

```csharp
        string msg = $"确认删除选中实体（句柄 {handle}）？删除经审计管线，可 Undo 撤回。";
        bool confirmed = ConfirmDeleteOverride is not null
            ? await ConfirmDeleteOverride(msg)
            : await new ConfirmDialog(msg).ShowDialog<bool>(this);
        if (!confirmed) return; // 取消：保持选中，不清状态
        <现有删除执行逻辑>
```

（方法签名 async void→async Task 按既有调用点适配；ShowDialog 在 headless 无 owner 消息泵时不测——seam 覆盖逻辑，真模态走 Task 11 协议。）
5. `internal Func<string, Task<bool>>? ConfirmDeleteOverride;` 字段。

- [ ] **Step 5: 运行确认绿 + 全量 View 回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Expected: PASS（既有 Delete 两段式测试若直接驱动第二击→需经 override 注入 true，sanctioned 更新：模态引入后旧路径不再无条件删除）

- [ ] **Step 6: 提交**

```bash
git add src/CadAgent.View/Services/ConfirmDialog.cs src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/EditPanelHeadlessTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): text height / block scale+rotation inputs + delete confirmation modal"
```

---

### Task 10: ④纠偏计划视图预览（LLM caller 接线）

**Files:**
- Create: `src/CadAgent.View/ViewModels/PlanPreviewViewModel.cs`
- Modify: `src/CadAgent.View/CadAgent.View.csproj`（+= CadAgent.Llm 引用）
- Modify: `src/CadAgent.View/MainWindow.cs`（计划面板 + 接线 + 构造可选参）
- Test: `tests/CadAgent.View.Tests/PlanPreviewViewModelTests.cs`（新）+ `MainWindowHeadlessTests.cs`（扩展）

**Interfaces:**
- Consumes: `AgentOrchestrator.RunAsync(intent, drawingSummary, session, sessionId, allowedKinds, kindDocs, confirmPlan, confirmCloudEgress?, postSummaryProvider?, ct)`（Core.Llm 既有，confirmPlan/confirmCloudEgress 回调即预览钩子——Core 零改动）；`LlmConfigStore.Load(path)` / `DpapiKeyProtector.ResolveKey(protector, profileName)` / `OpenAiClient(profile, key)`（CadAgent.Llm）；`DrawingSummarizer.Summarize(session, overlay?, maxChars=8000)`（Data）；`HandlerKindDocs.Default`（Task 4）；`EgressPolicy(agentDir)` / `UsageMeter(profile.Limits)`（Core.Llm）；EditViewModel 的 StepEngine 装配模式（audit/snapshots/identity/roles/HandlerRegistry.All()/semanticGate）
- Produces: `PlanPreviewViewModel`（构造见下）；MainWindow 构造加可选参 `PlanPreviewViewModel? planPreview = null`（headless 注入 fake client VM；null→生产装配）

- [ ] **Step 1: 写失败测试（PlanPreviewViewModelTests.cs，fake ILlmClient）**

```csharp
using CadAgent.Core.Llm;

namespace CadAgent.View.Tests;

/// <summary>fake LLM 客户端：返回固定计划 JSON（不触网）。</summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly string _content;
    public FakeLlmClient(string content) => _content = content;
    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
        Task.FromResult(new ChatResponse(_content, new TokenUsage(10, 20)));
    // ILlmClient 其余成员按接口实际补全（读 Core/Llm/ILlmClient.cs——若有 ModelsAsync 等返回固定值）
}

public class PlanPreviewViewModelTests
{
    // 夹具镜像 EditViewModelTests：临时项目 + audit.db + roles + session + identity。
    // profileOverride：new ProviderProfile(...)（读 Core/Llm/ProviderProfile 记录签名构造——
    // 本地端点 http://localhost:11434/v1 + DataPolicy 本地/云端两态）。

    [Fact]
    public async Task Generate_ValidPlan_PreviewPopulated_NotExecuted()
    {
        // fake 返回 {"steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":10,"y2":0}}]}
        // GenerateAsync(intent) → PreviewSteps 1 步 kind=add-line；IsPreviewVisible=true；
        // 审计无 tx-start（预览不执行）；llm-call 审计存在（orchestrator 留痕）。
    }

    [Fact]
    public async Task ConfirmPlan_Executes_TxCommitted()
    {
        // GenerateAsync 启动（Task 不 await 完成）→ 等 PreviewReady → ConfirmPlan(true) →
        // await 完成 → Status=Executed，TxId 非空；审计 tx-commit；PlanExecuted 事件触发。
    }

    [Fact]
    public async Task RejectPlan_RejectedByHuman_NoTx()
    {
        // ConfirmPlan(false) → Status=RejectedByHuman；审计 plan-rejected；无 tx-start。
    }

    [Fact]
    public async Task NoProfile_ErrorMessageGuidance_NoCrash()
    {
        // 不传 profileOverride 且项目无 llm-profiles.json → GenerateAsync →
        // ErrorMessage 含"llm"（引导 CLI llm add/use），不抛。
    }

    [Fact]
    public async Task LocalOnlyProfile_CloudEndpoint_BlockedEgress()
    {
        // profileOverride DataPolicy=LocalOnly + 云端端点 → Status=BlockedEgress；
        // 审计 egress-blocked（orchestrator 既有路径）。
    }

    [Fact]
    public async Task InvalidPlanJson_Failed_ErrorMessage()
    {
        // fake 返回非计划文本 → Status=Failed；ErrorMessage 非空；无 tx。
    }
}
```

（ProviderProfile/EgressPolicy/DataPolicy 实际签名读 Core/Llm 源文件——测试构造以编译为准；ChatResponse/TokenUsage 构造签名读 ILlmClient.cs。这些是既有类型，读后逐字套用，不得自造。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter PlanPreview`
Expected: 编译失败（PlanPreviewViewModel 不存在）

- [ ] **Step 3: PlanPreviewViewModel 实现**

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Core.Llm;
using CadAgent.Core.Rules;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Llm;

namespace CadAgent.View.ViewModels;

/// <summary>
/// ④纠偏计划视图预览（spec §4.1④/§5，计划3 LLM caller 解锁后承接）：
/// 意图→AgentOrchestrator（出境闸/预算/llm-call 审计/schema+kind 校验全在 Core）→
/// confirmPlan 回调 marshal 到预览面板→人工确认→执行（StepEngine 无旁路）→读回验证。
/// 密钥经 DPAPI/环境变量解析（spec §8），永不入预览文本/审计。
/// allowedKinds = HandlerRegistry.All()（insert-symbol 视图侧 deferred——CLI 有，
/// 跨文档符号导入的视图接线留后续切片；LLM 若提议该 kind 会被 parser 拒绝，诚实失败）。
/// 会话闸粗粒度持有整个 RunAsync（编排内含摘要/执行/读回多段 session 访问）。
/// </summary>
public sealed class PlanPreviewViewModel
{
    private readonly DrawingSession _session;
    private readonly string _projectDir;
    private readonly IAuditStore _audit;
    private readonly IIdentityProvider _identity;
    private readonly RoleChecker _roles;
    private readonly SemaphoreSlim _sessionGate;
    private readonly ILlmClient? _clientOverride;
    private readonly ProviderProfile? _profileOverride;
    private TaskCompletionSource<bool>? _planTcs;
    private TaskCompletionSource<bool>? _egressTcs;
    private CancellationTokenSource? _cts;

    public PlanPreviewViewModel(DrawingSession session, string projectDir,
        IAuditStore audit, IIdentityProvider identity, RoleChecker roles,
        SemaphoreSlim sessionGate, ILlmClient? clientOverride = null, ProviderProfile? profileOverride = null)
    {
        _session = session;
        _projectDir = projectDir;
        _audit = audit;
        _identity = identity;
        _roles = roles;
        _sessionGate = sessionGate;
        _clientOverride = clientOverride;
        _profileOverride = profileOverride;
    }

    public string Intent { get; set; } = "";
    public IReadOnlyList<StepSpec> PreviewSteps { get; private set; } = Array.Empty<StepSpec>();
    public bool IsPreviewVisible { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsEgressConsentVisible { get; private set; }
    public string EgressPrompt { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string? ErrorMessage { get; private set; }
    public string? LastTxId { get; private set; }

    /// <summary>预览就绪（步骤已填充）→ UI 显示面板。调用方上下文需 marshal（MainWindow Dispatcher）。</summary>
    public event Action? PreviewReady;
    /// <summary>首次云端出境需确认→ UI 显示同意提示。</summary>
    public event Action? EgressConsentRequired;
    /// <summary>编排结束（任何状态）→ UI 刷新。</summary>
    public event Action? RunCompleted;
    /// <summary>执行成功→ MainWindow 全视图刷新（渲染/图层/问题/审计）。</summary>
    public event Action? PlanExecuted;

    public async Task GenerateAsync()
    {
        if (IsBusy) return;
        ErrorMessage = null;
        Status = "";
        LastTxId = null;
        string agentDir = Path.Combine(_projectDir, ".cadagent");
        ProviderProfile? profile = _profileOverride;
        ILlmClient? client = _clientOverride;
        if (profile is null)
        {
            var store = LlmConfigStore.Load(Path.Combine(agentDir, "llm-profiles.json"));
            string? name = store.ActiveProfileName;
            profile = name is null ? null : store.Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        if (profile is null)
        {
            ErrorMessage = "无活动 LLM profile——请先在 CLI 执行 llm add / llm use 配置（spec §8）。";
            RunCompleted?.Invoke();
            return;
        }
        if (client is null)
        {
            var protector = new DpapiKeyProtector(Path.Combine(agentDir, "keys"));
            string? key = DpapiKeyProtector.ResolveKey(protector, profile.Name);
            if (string.IsNullOrEmpty(key))
            {
                ErrorMessage = "无可用密钥（CLI llm add --key 或 CADAGENT_LLM_API_KEY 环境变量）。";
                RunCompleted?.Invoke();
                return;
            }
            client = new OpenAiClient(profile, key);
        }

        string overlayPath = Path.Combine(agentDir, "semantic.json");
        var overlay = File.Exists(overlayPath) ? SemanticOverlay.Load(overlayPath) : null;
        ISemanticGate? gate = overlay is not null ? new SemanticGate(overlay) : null;
        var engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(agentDir, "snapshots")),
            _identity, _roles, HandlerRegistry.All(), gate);
        var orch = new AgentOrchestrator(client, new UsageMeter(profile.Limits), new EgressPolicy(agentDir),
            engine, _audit, _identity, profile.Model, profile);
        var allowedKinds = HandlerRegistry.All().Select(h => h.Kind).ToHashSet(StringComparer.Ordinal);
        string summary = DrawingSummarizer.Summarize(_session, overlay);

        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await _sessionGate.WaitAsync();
            try
            {
                var result = await orch.RunAsync(Intent, summary, _session, "view-plan",
                    allowedKinds, HandlerKindDocs.Default,
                    confirmPlan: plan =>
                    {
                        PreviewSteps = plan.Steps;
                        IsPreviewVisible = true;
                        _planTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        PreviewReady?.Invoke();
                        return _planTcs.Task.GetAwaiter().GetResult();
                    },
                    confirmCloudEgress: () =>
                    {
                        EgressPrompt = $"首次向云端端点 {profile.Endpoint} 发送图纸数据，确认?";
                        IsEgressConsentVisible = true;
                        _egressTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        EgressConsentRequired?.Invoke();
                        return _egressTcs.Task.GetAwaiter().GetResult();
                    },
                    ct: _cts.Token);
                Status = result.Status.ToString();
                LastTxId = result.TxId;
                ErrorMessage = result.Error;
                if (result.Status == OrchestrationStatus.Executed)
                {
                    IsPreviewVisible = false;
                    PlanExecuted?.Invoke();
                }
            }
            finally { _sessionGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Failed";
        }
        finally
        {
            IsBusy = false;
            IsPreviewVisible = false;
            IsEgressConsentVisible = false;
            RunCompleted?.Invoke();
        }
    }

    /// <summary>预览面板"确认执行"按钮。</summary>
    public void ConfirmPlan(bool accept) => _planTcs?.TrySetResult(accept);

    /// <summary>出境同意提示"确认/拒绝"按钮。</summary>
    public void ConfirmEgress(bool accept)
    {
        IsEgressConsentVisible = false;
        _egressTcs?.TrySetResult(accept);
    }

    /// <summary>取消在途编排（预览拒绝+token 取消）。</summary>
    public void Cancel()
    {
        _planTcs?.TrySetResult(false);
        _egressTcs?.TrySetResult(false);
        _cts?.Cancel();
    }
}
```

（StepPlan.Steps 类型 IReadOnlyList<StepSpec> 核对 Core 定义；ProviderProfile.Endpoint/Model/Limits/Name/DataPolicy 字段名以 Core/Llm 源文件为准；LlmConfigStore.ActiveProfileName/Profiles 以 CLI Agent 接线逐字镜像。编译错误即 API 事实修正点——不得改 Core 类型。）

- [ ] **Step 4: View csproj + MainWindow 面板**

csproj 加：

```xml
    <ProjectReference Include="..\CadAgent.Llm\CadAgent.Llm.csproj" />
```

MainWindow：
1. 构造加可选参 `PlanPreviewViewModel? planPreview = null`；生产装配（writeAudit/signoffIdentity/sessionGate 上下文）：

```csharp
        PlanPreview = planPreview ?? new PlanPreviewViewModel(vm.Session, vm.ProjectDir,
            writeAudit, signoffIdentity, new RoleChecker(), sessionGate);
```

2. 计划面板（新 TabItem "计划"，镜像既有面板装配模式；**DockPanel LastChildFill 顺序教训：面板加入既有 TabControl，不动主 DockPanel 子序**）：
   - Intent TextBox（Watermark "纠偏意图（可先从问题清单填充）"）+ "从问题填充" Button（点击：Issues.Issues 中 mandatory 项拼接 ruleId+建议文本入 Intent；无 Issues→提示）
   - "生成计划" Button → `async void` 包装 `await PlanPreview.GenerateAsync()`（_editBusy/_diffBusy 硬闸并列守卫；IsBusy 期间按钮禁用）
   - 预览 ItemsControl（PreviewSteps：kind + params JSON 文本）+ "确认执行"/"拒绝" Button → ConfirmPlan(true/false)
   - 出境同意 TextBlock（EgressPrompt）+ "确认发送"/"拒绝" Button → ConfirmEgress(true/false)
   - 状态 TextBlock（Status/ErrorMessage/LastTxId）
   - 事件 marshal：`PlanPreview.PreviewReady += () => Dispatcher.UIThread.Post(() => <面板刷新>);` RunCompleted/EgressConsentRequired 同型；PlanExecuted += () => Dispatcher.UIThread.Post(() => { RefreshAfterEdit(vm); _ = Issues?.RefreshAsync(); Audit?.Rebuild(); });
3. `internal PlanPreviewViewModel? PlanPreview { get; }` 属性（测试可达）。

- [ ] **Step 5: headless 面板测试**

```csharp
[AvaloniaFact]
public async Task PlanPanel_FakeClient_PreviewThenConfirm_ExecutesAndRefreshes()
{
    // MainWindow 注入 PlanPreviewViewModel（fake client 返回 add-line 计划 + profileOverride 本地端点）；
    // 驱动 GenerateAsync → 预览面板可见步骤文本 → ConfirmPlan(true) →
    // 状态 Executed + 画布渲染图元 +1 + 审计时间线含新 tx（live-rebuild 联动）。
}
```

- [ ] **Step 6: 运行确认绿 + 全量回归**

Run: `dotnet test tests/CadAgent.View.Tests`
Run: `dotnet test tests/CadAgent.Cli.Tests`
Expected: PASS（View→Llm 引用不破坏 CLI；既有 headless 全绿）

- [ ] **Step 7: 提交**

```bash
git add src/CadAgent.View/ViewModels/PlanPreviewViewModel.cs src/CadAgent.View/CadAgent.View.csproj src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/PlanPreviewViewModelTests.cs tests/CadAgent.View.Tests/MainWindowHeadlessTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat(view): correction-plan preview panel (AgentOrchestrator confirmPlan hook, egress consent, audited execution)"
```

---

### Task 11: 真机手动验证 + spec §12 闭环 + 全量门禁 + PR

**Files:**
- Create: `docs/verification/view-closure-protocol.md`
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md`（§12 延迟项闭环注记）
- 证据: `docs/verification/view-closure-*.png` + `outputs/view-closure/`（性能数字/日志）

**Interfaces:**
- Consumes: 全部前序任务交付；computer-use skill（计划4 真机验证先例：22 截图模式）
- Produces: 验证协议+证据；spec §12 闭环；PR（base main，用户批准后推送）

- [ ] **Step 1: 写验证协议 `docs/verification/view-closure-protocol.md`**

```markdown
# 视图收尾切片真机验证协议

> headless 已覆盖逻辑（View.Tests）；本协议覆盖真机手感/视觉/性能——计划4 诚实标注的待执行项。
> 执行工具：computer-use SDK（计划4 先例）；每项截图存证 docs/verification/view-closure-*.png。

## 前置
1. dotnet build CadAgent.sln 0 错误；CLI view 拉起测试图纸（含 Dimension/Hatch/dash/镜像 OCS 夹具——
   用 RenderModelTests 夹具生成法产一份综合验证图纸 outputs/view-closure/verify.dxf）。
2. 验证期间无并行 ZWCAD 自动化（本切片不触 COM，但环境约束保持）。

## 验证项（计划4 §12 待执行清单）
1. **中键拖拽平移手感**：middle-drag 注入（计划4 受限项——若 computer-use 仍不支持中键拖拽，
   诚实记录限制，headless 逻辑覆盖为准；尝试 SDK 实际能力后再定论）。
2. **五工具真机手感**：Text（自定义字高 7）/Move（圆移动——Task 4 扩展）/Delete（模态确认
   取消+确认两路）/ChangeLayer/InsertBlock（缩放 2 旋转 90°）——逐项截图+审计 verify 绿。
3. **渲染视觉**：Dimension 标注几何/Hatch 实心填充+岛挖空/dash 虚实/镜像 OCS 圆——截图核对
   与夹具几何一致（不假装：图案填充轮廓/复杂线型实线/倾斜 OCS 投影近似为明示边界）。
4. **计划预览**：本地 Ollama profile（若可用）或 fake 端点——生成→预览→确认→执行→刷新全链；
   无 profile 时引导文案核对。云端出境同意路径用云端 profile 首启验证（密钥不入截图）。
5. **高 DPI/多显示器**：Windows 显示设置 150% 缩放截图（文字/画布不糊不错位）；多显示器若
   硬件不可得→诚实记录环境限制。
6. **大图性能**：生成 10k 实体 DXF（脚本产 outputs/view-closure/big.dxf）→ view 打开→
   渲染/平移/缩放流畅度主观+RefreshAfterEdit 耗时数字（状态栏/日志）；记录硬件基线。
7. **审计链**：全部验证项目 CLI audit verify 绿。

## 证据归档
截图+性能数字+限制注记→docs/verification/（PNG）+outputs/view-closure/（日志/夹具）。
诚实边界：注入受限项/硬件不可得项显式标注，不以 headless 绿冒充真机验证。
```

- [ ] **Step 2: 执行验证（computer-use skill）**

按协议逐项执行；每项截图+结果记录；受限项诚实标注。验证发现的缺陷→TDD 修复（红→绿→提交，镜像计划4 先例：真机发现 2 缺陷 TDD 修复）。

- [ ] **Step 3: spec §12 闭环注记**

§12 末尾追加（L381 延迟 minors 清单对应更新）：

```markdown
- **计划4 延迟项承接闭环（2026-11-22，feat/cad-agent-view-closure）**：渲染 Dimension（匿名块展开，块缺失诚实计数）/Hatch（边界+实心 EvenOdd 填充；图案线生成 deferred）/dash（LineType 段→DashStyle，ByLayer 回退+LineTypeScale；复杂线型实线降级 deferred shapes）/OCS（任意轴算法，镜像精确；倾斜法向圆/弧 72 段 tessellation 投影近似、Insert 投影近似——明示边界）；ACI 全表已关闭（9ce52ba，前更正）。T9 遗留全闭环：审计 live-rebuild（Rebuild+TimelineChanged）/Text 字高输入/Move 八类实体（处理器契约扩展+KindDocs 共享提取）/InsertBlock 缩放旋转输入（度→弧度）/Delete 模态确认（ConfirmDialog+headless seam）。minors 闭环：issues-refresh 审计 parity（view-check check-run 镜像 CLI 字段）/view run-regression（view-regression，观察性不回滚+嵌套审计守卫）/_diffBusy 硬闸/RefreshAsync in-flight 硬闸/空图层（图层表并集）。④纠偏计划预览落地（AgentOrchestrator confirmPlan/confirmCloudEgress 回调接线，Core 零改动；View→Llm 引用；insert-symbol 视图侧 deferred）。真机手动验证按 docs/verification/view-closure-protocol.md 执行（证据+限制注记）。
```

（L381 原"仍延迟的 minors"句标注已闭环指向本注记；L369 渲染 deferred 句更新剩余边界：图案线/复杂线型 shapes/倾斜 OCS 近似/无块 Dimension。）

- [ ] **Step 4: 全量门禁连绿两次**

Run: `dotnet test CadAgent.sln`（顺序两跑；ZWCAD 真机前后 tasklist 核查；日志 outputs/view-closure/gate{1,2}.log UTF-8）
Expected: 全绿+AutoCAD 4 Skipped（main 基线无 AutoCAD 适配器——本分支基于 main，Acad.Tests 不存在；以实际项目数为准记录精确数字）。

- [ ] **Step 5: 提交 + 推送 + PR（推送/PR 须用户批准）**

```bash
git add docs/verification/ docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "docs: view-closure real-machine verification evidence + spec §12 closure notes"
```

推送+PR 保留给用户批准（finishing-a-development-branch 菜单）。PR body：闭环清单/诚实边界（deferred 剩余项）/门禁数字/验证证据路径/与 PR#18 的 §12 合并注记（不同区域，预期零冲突）。

---

## Self-Review 记录

- **Spec 覆盖**：§4.1④ 计划预览（Task 10）；§5 视图面板清单含计划预览（Task 10）；§9 审计无旁路（Task 6/7 parity+嵌套守卫；Task 10 经 orchestrator/StepEngine）；§12 L369 渲染 deferred（Task 1/2/3）+L381 minors（Task 5/6/7/8/10）+T9 遗留（Task 4/5/9）+真机手动验证（Task 11）；过时记录更正已先行提交（5619755）。
- **诚实边界**：Hatch 图案线/复杂线型 shapes/倾斜 OCS 投影/无块 Dimension/insert-symbol 视图侧——明示 deferred，不假装；真机注入受限项协议要求诚实标注。
- **占位符扫描**：测试夹具中"镜像既有模式"指令均指向具体既有文件（实现者读后逐字套用）——非 TBD；API 不确定点（Vertex.Location 可设性/HasShapes 可设性/ILlmClient 成员）均给出编译错误即修正的确定性路径与不变意图。
- **类型一致性**：RenderItem 可选参 DashPattern/Fill Task 2 定义 Task 3 消费；ViewRuleEngine Task 6 定义 Task 7 消费；HandlerKindDocs Task 4 定义 Task 10 消费；AuditViewModel.Rebuild Task 5 定义 Task 10 PlanExecuted 联动消费；ConfirmDeleteOverride Task 9 内自洽。
- **已知取舍**：IssuesVM sessionId="view" 常量（无 CLI session 注入）；计划预览会话闸粗粒度持有整个 RunAsync；Delete 模态 headless 经 seam（真模态走真机协议）；Move 扩展后 Hatch 等仍诚实 step-failed。
---

## 执行记录（2026-11-22，子代理驱动）

- 分支 feat/cad-agent-view-closure，20 提交（5619755..66ecbce），11 任务全完成 + 最终审查修复波。
- 门禁：dotnet test CadAgent.sln 连绿两次 **908 通过 / 0 失败 / 0 跳过**（6 项目；ZWCAD 真机 13；本分支基于 main 无 Acad.Tests）。
- 任务内修复环：T1 Insert OCS 复合序+整圆闭合；T2 Dimension 深度守卫（自指环 StackOverflow 复现）；T5 无库首 tx live；T6 哈希闸内+RoomNameChecker parity。
- 用户介入：DashScope workspace key 配置测试环境（DPAPI，qwen3-vl-plus）→ LlmProbe 三修复（16×16 探针图/部分结果保真/脱敏正则）→ 云端全链双路验证（CLI agent + 视图同意/预览/执行/拒绝，26+7 截图，密钥三通道 grep 零命中）。**key 已现于对话，建议轮换。**
- 最终审查修复波（5 Important+2 顺手修）：Move OCS 逆映射（镜像精确/倾斜投影近似）/PlanPreview 前奏 try-catch/子图元句柄剥后缀（父实体语义）/PlanExecuted 释闸后触发/IsFinite 三守卫/预览按钮轮次禁用/spec §4.1④ 部分交付+边界注记。
- **剩余 deferred（post-merge）**：§4.1④ 问题清单勾选→意图（含 recommended）后续切片；Polyline2D OCS 渲染缺口（重新定性为代码非仅措辞）；Hatch 命中/选择；子图元独立编辑；RefreshAfterEdit 刷新侧取闸（残余窄窗口 park）；SignoffTxSelector/Rebuild 标签+视觉选中/Issues refresh 后 rebuild+失败口径；egress 正向专测；LlmProbe OCE/llm test --vision 退出码；T10 窗口关闭 Cancel/_cts Dispose/OpenAiClient 缓存；KindDocs 按 allowedKinds 过滤；其余见 ledger triage。
- 真机限制（诚实标注）：中键注入受限/150% DPI 权限阻断（125%/100% 已验）/MARK 旋转自对称/副屏标题栏关闭未定论。
