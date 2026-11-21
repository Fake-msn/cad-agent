# CAD 智能体终端 · Avalonia 视图 实现计划（计划 4）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 轻量可视化工作台：图纸渲染（pan/zoom/图层）、问题清单（条款引用+效力徽章）、审计时间线回放+前后对比、审查签认（角色门禁）、图层纠偏、规则裁定、基础手动编辑（全部经 StepEngine 审计管线）——CLI `cadagent view` 拉起。

**Architecture:** 新项目 CadAgent.View（Avalonia 11.x，code-driven UI + MVVM）；ViewModel 纯逻辑可单测；渲染管线 RenderModelBuilder（DrawingSession→RenderItem 列表，Insert 展开）；自定义 Canvas Control（DrawingContext）；Cli `view` 命令进程拉起 View exe。视图只消费既有 Core/Data 服务，**不另辟修改旁路**——手动编辑构造 StepPlan 经 StepEngine。

**Tech Stack:** .NET 8、Avalonia 11.3.2 + Avalonia.Desktop + Avalonia.Themes.Fluent + Avalonia.Fonts.Inter、Avalonia.Headless.XUnit（headless UI 测试）、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §7 交互形态、§3 视图定位（基础编辑+审计可视化）、§4.1⑥ 签认、§4.4 纠偏、§6.2 裁定。

**工作区:** worktree `E:\随机任务 ·Qoder·\.worktrees\cad-agent-view`（分支 feat/cad-agent-view，基于 f22eb7c + .gitattributes LF）。**所有命令在此 worktree 执行，不回主树**（另一会话活跃）。standards-src 底稿已复制（gitignored）。

## Global Constraints

- View 引用 Core+Data；**不引用厂商库**；Cli 引用 View（仅进程拉起，不引用 Avalonia 类型——Cli 保持无 UI 依赖：view 命令 Process.Start View exe 路径）。
- 手动编辑/签认/纠偏/裁定全部经既有服务（StepEngine/AdjudicationSet/SemanticScanner+overlay/RoleChecker），审计管线无旁路；新审计 kind：`review-signed`（签认）、`view-edit`（手动编辑请求，tx 审计既有）。
- 签认角色门禁：Review 及以上（RoleChecker.Ensure）；绘制者不能自审（op.Role>=Review 且 tx 责任人≠签认人——第一 slice：角色门禁+责任人不同校验）。
- ViewModel 无 Avalonia 依赖（纯 C#，可单测）；Control 层薄（渲染+输入→ViewModel）。
- 渲染第一 slice：Line/Circle/Arc/LwPolyline/TextEntity/MText/Insert（展开块内实体，变换 point/scale/rotation）；颜色=图层色（ACadSharp Color→RGB 近似）；字高按渲染比例；**不渲染**：尺寸标注实体/填充/线型虚实（留后续，注记）。
- 342 既有测试持续绿；新文件 EOF 换行（.gitattributes LF 已声明）；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）。
- TDD；pristine；feat:/fix: 前缀；Windows GBK 不回显中文；源文件 UTF-8。
- UI 验证：headless 测试（Avalonia.Headless.XUnit）覆盖控件构建；真机视觉验证（启动+截图）在 T10 手动协议——**不能自动化的如实标注**。

## 文件结构

```
CadAgent.sln                 + src/CadAgent.View + tests/CadAgent.View.Tests
src/CadAgent.View/
  Program.cs                 入口（args 解析 --drawing/--project，AppBuilder）
  App.axaml.cs / MainWindow.cs  code-driven 壳（Dock 布局：左面板/中画布/右审计）
  Rendering/RenderModel.cs   RenderItem/RenderKind/RenderModelBuilder
  Controls/DrawingCanvas.cs  自定义 Control（渲染+pan/zoom+命中）
  ViewModels/                MainViewModel/CanvasViewModel/LayersViewModel/IssuesViewModel/
                             AuditViewModel/SignoffViewModel/SemanticViewModel/AdjudicationViewModel/EditViewModel
  Services/ViewServices.cs   服务装配（DrawingSession/IAuditStore/RuleEngine/StepEngine/overlay/adjudications）
  Services/ReviewSignoffStore.cs  签认持久化（.cadagent/signoffs.json 原子写）
src/CadAgent.Cli/
  CommandRouter.cs           + case "view"（Process.Start）
tests/CadAgent.View.Tests/   ViewModel/Builder/headless 测试
```

---

### Task 1: View 项目骨架 + CLI view 命令

**Files:**
- Create: `src/CadAgent.View/`（csproj/Program.cs/MainWindow.cs/ViewModels/MainViewModel.cs）、`tests/CadAgent.View.Tests/`
- Modify: `CadAgent.sln`、`src/CadAgent.Cli/CommandRouter.cs`（view case）
- Test: `tests/CadAgent.View.Tests/MainViewModelTests.cs`、`tests/CadAgent.Cli.Tests/ViewCommandTests.cs`

**Interfaces:**
- Produces:
  - CadAgent.View csproj：OutputType WinExe，net8.0，Avalonia 11.3.2 四包，引用 Core+Data
  - `sealed class MainViewModel`：ctor(string drawingPath, string projectDir)——加载 DrawingSession（失败→ErrorMessage）；`string Title`（文件名）；`string? ErrorMessage`；`DrawingSession Session`；`string ProjectDir`
  - Program：args 解析 --drawing（必需）/--project（默认 cwd）；无 --drawing→stderr+exit 1；AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args)
  - MainWindow：code-driven（无 XAML）——Window{Title=VM.Title, Content=TextBlock 占位（T3 换 Canvas）}；DataContext=MainViewModel
  - CLI `view --drawing <file> [--project dir]`：定位 View exe（AppContext.BaseDirectory 同目录 CadAgent.View.exe 或 `dotnet CadAgent.View.dll`——裁定：Process.Start("dotnet", $"\"{viewDll}\" --drawing ...")，viewDll=Path.Combine(AppContext.BaseDirectory,"CadAgent.View.dll")；Cli 输出目录需有 View dll——**裁定：Cli csproj 引用 View 项目但仅复制输出（ReferenceOutputAssembly=true 默认；Cli 代码不 using Avalonia**）；文件不存在→stderr exit 1；启动成功 exit 0（不等待进程）
- [ ] **Step 1: 骨架命令**

```bash
dotnet new classlib -n CadAgent.View -o src/CadAgent.View -f net8.0
# csproj 改 OutputType WinExe + Avalonia 包
dotnet new xunit -n CadAgent.View.Tests -o tests/CadAgent.View.Tests -f net8.0
dotnet sln add src/CadAgent.View tests/CadAgent.View.Tests
dotnet add src/CadAgent.View reference src/CadAgent.Core src/CadAgent.Data
dotnet add src/CadAgent.View package Avalonia --version 11.3.2
dotnet add src/CadAgent.View package Avalonia.Desktop --version 11.3.2
dotnet add src/CadAgent.View package Avalonia.Themes.Fluent --version 11.3.2
dotnet add src/CadAgent.View package Avalonia.Fonts.Inter --version 11.3.2
dotnet add tests/CadAgent.View.Tests reference src/CadAgent.View
dotnet add src/CadAgent.Cli reference src/CadAgent.View
dotnet add tests/CadAgent.View.Tests package Avalonia.Headless.XUnit --version 11.3.2
```
（Avalonia 11.3.2 若 restore 失败→用最新 11.x 稳定版，记录版本。）

- [ ] **Step 2: 写失败测试**

```csharp
// MainViewModelTests.cs
using CadAgent.Data.Tests;  // 需引用 Data.Tests（DxfFixture）——csproj 加
namespace CadAgent.View.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_ValidDrawing_TitleAndSession()
    {
        var vm = new ViewModels.MainViewModel(_path, Path.GetTempPath());
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(Path.GetFileName(_path), vm.Title);
        Assert.Single(vm.Session.Summarize().Entities);
    }

    [Fact]
    public void Load_MissingFile_ErrorMessage()
    {
        var vm = new ViewModels.MainViewModel("no-such.dxf", Path.GetTempPath());
        Assert.NotNull(vm.ErrorMessage);
    }
}
```
```csharp
// ViewCommandTests.cs（Cli.Tests）
namespace CadAgent.Cli.Tests;
public class ViewCommandTests
{
    [Fact]
    public void View_MissingDrawing_ExitOne()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "view", "--drawing", "no-such.dxf" }, outW, errW);
        Assert.Equal(1, code);
        Assert.NotEmpty(errW.ToString());
    }
}
```
（View.Tests 引用 Data.Tests 取 DxfFixture；headless 包 T3 用。）

- [ ] **Step 3: RED**。
- [ ] **Step 4: 实现**（Program/MainWindow code-driven/MainViewModel/CLI view case）。
- [ ] **Step 5: GREEN + 全套件**。
- [ ] **Step 6: Commit** — `feat: Avalonia View scaffold + CLI view command (process launch)`

---

### Task 2: 渲染模型构建器（View/Rendering）

**Files:**
- Create: `src/CadAgent.View/Rendering/RenderModel.cs`
- Test: `tests/CadAgent.View.Tests/RenderModelTests.cs`

**Interfaces:**
- Produces:
  - `enum RenderKind { Line, Circle, Arc, Polyline, Text }`
  - `sealed record RenderItem(RenderKind Kind, IReadOnlyList<(double X, double Y)> Points, string Layer, uint ColorRgb, string? Text, double TextHeight, double RotationDeg)`——Line:2点；Circle:圆心+半径存 Points[1].X（约定：Points[0]=center, Points[1]=(radius,0)）；Arc:圆心+半径+起止角（Points[1]=(radius,startDeg), Points[2]=(endDeg,0)）；Polyline:顶点序列；Text:插入点+Text+TextHeight
  - `static class RenderModelBuilder`：`IReadOnlyList<RenderItem> Build(DrawingSession session)`——遍历 Doc.Entities：Line→2点；Circle→center+radius；Arc→center+radius+角度（弧度→度）；LwPolyline/Polyline2d→顶点（IsClosed→首尾闭合）；TextEntity/MText→插入点+值+字高；**Insert→展开 BlockRecord 内实体**（变换：scale(x,y)+rotation+insertPoint 平移；块内 Insert 递归一层防爆栈）；图层色 `e.Layer.Color`（ACadSharp Color→RGB：索引色查表近似/真彩直接）；ByLayer 实体取图层色；未知实体跳过（计数注记）
  - `static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<RenderItem> items)`（空→(0,0,1,1)）
- [ ] **Step 1: 写失败测试**（fixture：line+circle+arc+polyline+text+insert(块内line)→Build 计数/类型/坐标/insert 展开变换正确/Bounds；图层色非零）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**（ACadSharp 实体属性以 XML 文档核实：LwPolyline.Vertices、Arc.StartAngle/EndAngle 弧度、Insert.BlockRecord、Color 结构）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: render model builder with insert expansion and layer colors`

---

### Task 3: 画布控件 + CanvasViewModel（pan/zoom/fit）

**Files:**
- Create: `src/CadAgent.View/Controls/DrawingCanvas.cs`、`src/CadAgent.View/ViewModels/CanvasViewModel.cs`
- Modify: `MainWindow.cs`（Content=Canvas+面板布局骨架）
- Test: `tests/CadAgent.View.Tests/CanvasViewModelTests.cs`、`tests/CadAgent.View.Tests/CanvasHeadlessTests.cs`

**Interfaces:**
- Produces:
  - `sealed class CanvasViewModel`：`IReadOnlyList<RenderItem> Items`；`HashSet<string> HiddenLayers`；`double Zoom`（1=fit）；`(double X, double Y) Pan`；`void Fit(bounds, viewportW, viewportH)`（计算基础缩放）；`void ZoomAt(factor, screenX, screenY)`；`void PanBy(dx, dy)`；`IEnumerable<RenderItem> VisibleItems`（过滤 HiddenLayers）；`string? SelectedHandle`；`int? HitTest(double worldX, double worldY, double tolerance)`（线段距离/圆包含/文本框；返回 item 索引）；`event Action? RenderInvalidated`
  - `sealed class DrawingCanvas : Avalonia.Controls.Control`：Override Render(DrawingContext)——画 VisibleItems（Line→DrawLine(Pen)；Circle→DrawEllipse；Arc→StreamGeometry 弧；Polyline→折线；Text→FormattedText）；变换 Matrix（fit*zoom+pan，Y 翻转 CAD→屏幕）；指针事件→VM（wheel zoom/中键 pan/左键 select）；Background 深色
  - Headless 测试：[AvaloniaTest] 构建 Canvas+VM→Assert 控件可实例化/Render 不抛（headless session）
- [ ] **Step 1: 写失败测试**（VM：Fit 计算/ZoomAt 锚点/PanBy/HiddenLayers 过滤/HitTest 线段命中与容差；headless：控件构建）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: drawing canvas with pan/zoom/fit/hit-test (headless-tested)`

---

### Task 4: 图层面板 + 选择

**Files:**
- Create: `src/CadAgent.View/ViewModels/LayersViewModel.cs`
- Modify: `MainWindow.cs`（左栏 ListBox）、`CanvasViewModel`（选择高亮）
- Test: `tests/CadAgent.View.Tests/LayersViewModelTests.cs`

**Interfaces:**
- `sealed class LayerEntry`：Name/EntityCount/IsVisible/ColorBrush
- `sealed class LayersViewModel`：ctor(session, canvasVM)；`IReadOnlyList<LayerEntry> Layers`（按名序）；`void Toggle(string name)`→canvasVM.HiddenLayers 增删+RenderInvalidated；`void Isolate(string name)`（其他全隐）；`void ShowAll()`
- 选择：Canvas 左键→HitTest→SelectedHandle→高亮渲染（黄色加粗）；MainWindow 状态栏显示选中实体信息（handle/kind/layer）
- [ ] **Step 1-5: TDD**（Toggle/Isolate/ShowAll/计数；headless 面板构建）→ commit `feat: layer panel with visibility/isolate and selection highlight`

---

### Task 5: 问题清单面板（in-process check）

**Files:**
- Create: `src/CadAgent.View/ViewModels/IssuesViewModel.cs`
- Modify: `MainWindow.cs`（右栏 Issues 列表+刷新按钮）
- Test: `tests/CadAgent.View.Tests/IssuesViewModelTests.cs`

**Interfaces:**
- `sealed class IssueEntry`：RuleId/Standard/Clause/Excerpt/Force/Severity/Message/Targets/Suggestion/DetectionBasis（映射 Core Issue）
- `sealed class IssuesViewModel`：ctor(session, projectDir, skillDirs)；`Task RefreshAsync()`——BuiltinRules.Load + SkillSet.Compose（冲突→ErrorMessage）+ AdjudicationSet.Load + overlay Load（存在时）→RuleEngine.Evaluate（checkers 全注册，同 CLI StandardsEngine）→`IReadOnlyList<IssueEntry> Issues`+`int MandatoryCount`+`int RulesSkipped`+SkippedReasons；**定位**：点击 issue→canvas 高亮 Targets 句柄+居中
- 效力徽章：Mandatory 红/Recommended 黄/Informational 灰；条款引用展开显示 excerpt+DetectionBasis
- [ ] **Step 1-5: TDD**（fixture 违规→Refresh 产出 issue 引用条款/mandatory 计数/adjudicate 豁免后消失/skill 冲突 ErrorMessage）→ commit `feat: issues panel with in-process standards check and citations`

---

### Task 6: 审计时间线 + 回放 + 前后对比

**Files:**
- Create: `src/CadAgent.View/ViewModels/AuditViewModel.cs`
- Modify: `MainWindow.cs`（底部时间线+回放滑块+对比切换）
- Test: `tests/CadAgent.View.Tests/AuditViewModelTests.cs`

**Interfaces:**
- `sealed class AuditEntry`：Seq/Ts/Operator/Role/Kind/Target/PlanRef（映射 AuditRecord）
- `sealed class TxGroup`：TxId（PlanRef 关联）/Steps/StartTs/Operator/HasCommit/HasFailed
- `sealed class AuditViewModel`：ctor(auditStore, snapshotStore, session)；`IReadOnlyList<AuditEntry> Entries`；`IReadOnlyList<TxGroup> Transactions`；`void SelectTx(string txId)`→`IReadOnlyList<string> HighlightHandles`（该 tx step 的 Target）；`Task<DiffResult> LoadDiffAsync(string txId)`——快照 DXF（snapshotStore.Find）→DrawingSession.Load→RenderModelBuilder.Build→`DiffResult{BeforeItems, AfterItems(当前), AddedHandles, RemovedHandles, ChangedHandles}`（按 handle+几何哈希比对）；对比模式 canvas 叠加：before 灰/after 正常/added 绿/removed 红 strikethrough
- 回放：选择 tx→高亮+状态栏摘要；**只读回放不改图**
- [ ] **Step 1-5: TDD**（构造审计+快照→TxGroup 分组/HighlightHandles/Diff added/removed/changed 正确）→ commit `feat: audit timeline with transaction replay and before/after diff`

---

### Task 7: 审查签认（角色门禁 + 持久化）

**Files:**
- Create: `src/CadAgent.View/Services/ReviewSignoffStore.cs`、`src/CadAgent.View/ViewModels/SignoffViewModel.cs`
- Modify: `MainWindow.cs`（签认按钮+状态）
- Test: `tests/CadAgent.View.Tests/SignoffTests.cs`

**Interfaces:**
- `sealed record ReviewSignoff(string TxId, string Operator, Role Role, bool Approved, string Comment, DateTimeOffset Ts)`
- `sealed class ReviewSignoffStore`：Load/Save 原子（.cadagent/signoffs.json）；Upsert（同 TxId+Operator 替换）
- `sealed class SignoffViewModel`：ctor(session, auditStore, signoffStore, identity, roleChecker, txGroups)；`bool CanSign`（identity.Role>=Review）；`Task SignAsync(string txId, bool approved, string comment)`——Ensure(op, Review)；**责任人不同校验**：tx 责任人（tx-start Operator）==op.Account→拒绝（"绘制者不能自审"）；Upsert+审计 `review-signed`（Source="review", paramsJson={txId, approved, comment}）；`IReadOnlyList<ReviewSignoff> Signoffs`；未签认 tx 列表
- [ ] **Step 1-5: TDD**（Review 签认成功+审计/Draw 角色拒绝/自审拒绝/持久化往返）→ commit `feat: review signoff with role gate and self-review prohibition`

---

### Task 8: 图层纠偏 + 规则裁定面板

**Files:**
- Create: `src/CadAgent.View/ViewModels/SemanticViewModel.cs`、`src/CadAgent.View/ViewModels/AdjudicationViewModel.cs`
- Modify: `MainWindow.cs`（左栏 tab：图层/纠偏/裁定）
- Test: `tests/CadAgent.View.Tests/SemanticAdjudicationViewModelTests.cs`

**Interfaces:**
- SemanticViewModel：ctor(session, projectDir)；`Task ScanAsync(skillDirs)`→SemanticScanner.Scan→overlay Save+审计 semantic-scan（复用 CLI 路径逻辑——**抽共享服务**：Cli 的 scan/map 逻辑提取到 Data/Core 服务？裁定：ViewModel 直接调 SemanticScanner+overlay+audit（同 CLI 语义），审计 kind 一致）；`IReadOnlyList<AmbiguousLayer> Ambiguous`；`Task MapAsync(layer, SemanticClass)`→ConfirmMapping+ApplyMapping+Save+审计 semantic-map；隔离显示→canvasVM.HiddenLayers 设其他层
- AdjudicationViewModel：ctor(projectDir, skillDirs)；规则列表（builtin+skills）+当前裁定；`Task AdjudicateAsync(ruleId, force?, exempt, reason, interpretation)`→校验（互斥/reason 必填/孤儿拒绝，同 CLI）→AdjudicationSet.Upsert+Save+审计 rule-adjudicated；裁定后 IssuesViewModel.Refresh 联动
- [ ] **Step 1-5: TDD**（scan→ambiguous/map→HumanConfirmed+审计；adjudicate 升级/豁免/孤儿拒绝/互斥）→ commit `feat: semantic correction and rule adjudication panels`

---

### Task 9: 基础手动编辑（经 StepEngine）

**Files:**
- Create: `src/CadAgent.View/ViewModels/EditViewModel.cs`
- Modify: `MainWindow.cs`（工具栏）、`CanvasViewModel`（绘制预览）
- Test: `tests/CadAgent.View.Tests/EditViewModelTests.cs`

**Interfaces:**
- `enum EditTool { Select, Line, Text, Move, Delete, ChangeLayer, InsertBlock }`
- `sealed class EditViewModel`：ctor(session, auditStore, snapshotStore, identity, roleChecker, overlay?)；`EditTool ActiveTool`；`Task<EditResult> ApplyAsync(...)`——构造 StepPlan（kind 对应六处理器；planId="view-edit-{guid}"）→审计 `view-edit` 请求（Source="view", paramsJson={tool, args}）→StepEngine.Run（闸口注入若 overlay 存在）→成功→session 刷新→RenderInvalidated；失败→ErrorMessage+Reload；`Task UndoAsync(txId)`→StepEngine.Rollback+刷新；`IReadOnlyList<string> RecentTxIds`（撤销栈）
- 交互：Line 工具两点点击→ApplyAsync；Text 点击+输入框；Move 选中+目标点→dx/dy；Delete 选中→确认→delete-entity；ChangeLayer 选中+层选择；InsertBlock 块名+点
- **所有编辑经 StepEngine——无直接 Doc 修改**
- [ ] **Step 1-5: TDD**（画线→实体+1+审计 tx/撤销→恢复/角色拒绝/失败 Reload/请求审计先于 tx）→ commit `feat: manual editing via StepEngine with undo (no bypass)`

---

### Task 10: 集成 + 真机验证 + 承接记录

**Files:**
- Modify: `MainWindow.cs`（布局收尾：左 tab/中画布/右 issues/底审计+工具栏+状态栏）、spec §12
- Create: `docs/verification/plan4-ui-protocol.md`（手动验证协议+截图）

**Interfaces:**
- 真机验证协议（**必须执行，截图存证**）：
  1. `cadagent view --drawing <fixture.dxf>` 启动→截图（渲染正确：线/圆/弧/文字/块）
  2. pan/zoom/fit→截图
  3. 图层隔离→截图
  4. 问题清单刷新→条款引用可见→截图
  5. 手动画线+撤销→实体计数变化+审计链绿
  6. 审计时间线选择 tx→高亮+前后对比→截图
  7. 签认（Review 角色配置）→成功；Draw 角色→拒绝
  8. 纠偏 map+裁定升级→issues 联动
  - 用 computer-use 技能或手动截图；**无法自动化的项如实标注"手动验证"**
- spec §12 追加计划 4 完成记录：视图落地（渲染/面板/签认/纠偏/裁定/手动编辑全经审计管线）；渲染 deferred（尺寸标注/填充/线型虚实/颜色精确表）；headless 覆盖范围；真机验证结果；计划 3（side chat）承接状态注记
- [ ] **Step 1: 布局收尾**
- [ ] **Step 2: 全套件绿**
- [ ] **Step 3: 真机验证协议执行+截图**
- [ ] **Step 4: spec §12 + 验证文档**
- [ ] **Step 5: Commit** — `feat: view integration + verified UI protocol; docs: plan-4 completion`

---

## 自审记录

- 无旁路：手动编辑/签认/纠偏/裁定全经既有服务+审计；新 kind review-signed/view-edit。
- ViewModel 纯逻辑单测；控件 headless 测试；真机视觉 T10 手动协议（诚实标注）。
- 渲染第一 slice 实体覆盖+deferred 清单明确；Insert 展开变换有测试。
- Cli 不 using Avalonia（进程拉起）；View exe 输出经项目引用复制到 Cli 目录。
- 342 既有测试保护；worktree 隔离（不回主树）；.gitattributes LF。
- 签认自审禁止（责任人≠签认人）——spec §5.3 职责分离落地。