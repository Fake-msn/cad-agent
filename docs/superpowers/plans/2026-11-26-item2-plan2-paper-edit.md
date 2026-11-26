# 项2 Plan-2：纸面空间可交互 + 纸面/属性编辑（B）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 布局幅面的纸面空间从「可见但只读」变为可编辑：纸面实体 Move/Delete/ChangeLayer、纸面新建 Line/Text、标题栏**实例属性**与文字改值；全部经 StepEngine 审计，模型/纸面坐标严格隔离。

**Architecture:** 在 Plan-1（F+A 已合并 main b9b693c）之上新增统一目标解析 `EditTargetResolver`（模型/纸面/顶层 Insert 属性三空间，块定义留给 Plan-3），扩展既有 `move/delete/change-layer/add-line/add-text` 处理器并新增 `set-text`/`set-attribute`；View 由 `EditRouting.Route` 的 `Paper` 分支执行纸面编辑（纸坐标直用、不逆映射），`BlockChild` 在 Plan-2 仍拒绝（Plan-3 承接）。

**Tech Stack:** C#/.NET 8；ACadSharp 3.8.35；Avalonia 11.3.2 headless；xUnit。

**Spec:** `docs/superpowers/specs/2026-11-26-item2-paper-subentity-design.md` §2.2/§3.1-3.3（处理器族与 View 交互）、§4（CLI 契约）、§5.3（边界）
**前置:** Plan-1 已合并 main b9b693c（纸面渲染/属性渲染/逆映射/EditRouting/哈希全空间）

## Global Constraints

- 分支：`feat/cad-agent-paper-edit`（自 main b9b693c）；不合并、不 push（逐次授权）。
- Git 身份仅环境变量（逐命令）：`GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`；禁止 `git config`。
- 全部编辑经 StepEngine（tx/审计/整文档快照）；**不新增 View 直改 Doc 路径**。
- 空间隔离硬约束：纸面坐标永不写模型、模型坐标永不写纸面；跨空间两击拒绝（Plan-1 已实现，保持）。
- 视口对象（Viewport）本身不可 Move/Delete/ChangeLayer（布图语义保护，fail-closed 文案）。
- 块定义编辑/子图元 = Plan-3：`block` 参数与 `BlockName!=null` 路由在 Plan-2 一律显式拒绝并给出「后续切片」指引。
- 文本：非 null、≤4096 字符；TextEntity/AttributeEntity 禁 `\n`（MText 允许）；空串允许（清空是合法意图）；`AttributeDefinition` 拒绝。
- 属性只编辑**实例**（顶层 Insert.Attributes；模型/纸面）；不递归块内 Insert 的属性（属块定义语义，Plan-3）。
- 哈希：Plan-1 已把 A|/ADEF| 行（含位置/层/字高）纳入 ComputeHash——纸面/属性编辑必须体现 before≠after（不得再改哈希格式）。
- 测试夹具全部 `%TEMP%` 并清理；SQLite 清理前 `ClearAllPools()`；View 已知 audit.db teardown flake 隔离复跑留证。
- Windows GBK：dotnet 输出重定向 UTF-8 文件再读。
- 门禁基线：main b9b693c 全 sln 1897 通过 / 0 失败 / 1 设计内 Skip。

## 文件结构（本计划）

- Add `src/CadAgent.Data/Handlers/EditTargetResolver.cs`
- Modify `src/CadAgent.Data/Handlers/LineHandlers.cs`（Find→Resolver；add-line/add-text space/layout）
- Modify `src/CadAgent.Data/Handlers/EditHandlers.cs`（move/delete/change-layer 经 Resolver + 所有者删除 + Viewport 守卫）
- Add `src/CadAgent.Data/Handlers/TextHandlers.cs`（set-text/set-attribute）
- Modify `src/CadAgent.Data/Handlers/HandlerRegistry` 注册
- Modify `src/CadAgent.View/ViewModels/EditViewModel.cs`（EditTool.SetText；新方法与可选空间参数）
- Modify `src/CadAgent.View/MainWindow.cs`（Paper 路由执行、改文字工具、纸面新建、BlockChild 拒绝文案、状态）
- Tests：`tests/CadAgent.Data.Tests/EditTargetResolverTests.cs`、`PaperEditHandlersTests.cs`、`SetTextAttributeTests.cs`；`tests/CadAgent.Cli.Tests/PaperPlanE2ETests.cs`；`tests/CadAgent.View.Tests/PaperEditHeadlessTests.cs`、`EditViewModelTests` 增补
- Docs：spec §12 闭环注记（`2026-11-20-cad-agent-terminal-design.md` 追加）

---

### Task 1: EditTargetResolver + 既有处理器三空间化

**Files:**
- Add `src/CadAgent.Data/Handlers/EditTargetResolver.cs`
- Modify `src/CadAgent.Data/Handlers/LineHandlers.cs`、`EditHandlers.cs`
- Test `tests/CadAgent.Data.Tests/EditTargetResolverTests.cs`、`PaperEditHandlersTests.cs`

**Interfaces:**
```csharp
public enum EditOwnerKind { Model, Paper, InsertAttribute }
public sealed record EditTarget(
    Entity Entity, EditOwnerKind Owner, string? LayoutName = null,
    Insert? AttributeOwner = null, ulong? InsertHandle = null);
public static class EditTargetResolver
{
    // 解析失败→null；调用方统一映射 "句柄不存在（已检索：模型/纸面/顶层属性）"
    public static EditTarget? Resolve(DrawingSession s, JsonElement p);
}
```
- 默认检索序：模型顶层 → 纸面顶层（布局名 Ordinal）→ 顶层 Insert 属性（模型再纸面）。
- 限定参数（fail-closed，未命中不回退）：`space:"paper"` 必须配 `layout`；`attribute:true`。
- `block` 参数：各处理器入口先检查 → 返回 `StepResult(false, "块内编辑将在后续切片提供（Plan-3）")`；Resolver 对含 `block` 的输入返回 null（不检索块定义）。
- 同句柄多空间命中 → 拒绝（"句柄在多个空间出现，请用 space/layout/attribute 限定"）。
- Move 处理器：经 Resolver 取实体；`Viewport` → 失败"视口对象不支持编辑"；其余按既有类型 switch（AttributeEntity 走 TextEntity 分支的 OCS 平移）。
- Delete：按 owner 移除（模型 `Doc.Entities` / 纸面 `layout.AssociatedBlock.Entities` / `AttributeOwner.Attributes`）；`Viewport` 拒绝。
  - **API 实件核验**：`Insert.Attributes` 为 `SeqendCollection<AttributeEntity>`；若其无 `Remove`，按实件 API 调整（如重建集合）并在 commit 说明——不得猜接口。
- ChangeLayer：任意实体赋值；`Viewport` 拒绝。
- add-line/add-text：`space:"paper"`+`layout` → 写入该布局 `AssociatedBlock.Entities`（layout 必须存在且 `IsPaperSpace`，否则失败）；默认模型行为不变。

- [ ] **Step 1: 失败测试**（Resolver 矩阵；纸面 move/delete/change-layer；add-line/text 写纸面；Viewport 拒绝；block 参数拒绝）

关键样例：
```csharp
[Fact]
public void Resolve_PaperEntity_ByHandle_FindsLayout()
{
    var (s, layout) = PaperSession();
    var line = layout.AssociatedBlock.Entities.OfType<Line>().Single();
    var p = System.Text.Json.JsonSerializer.SerializeToElement(new { handle = line.Handle.ToString() });
    var target = EditTargetResolver.Resolve(s, p);
    Assert.NotNull(target);
    Assert.Equal(EditOwnerKind.Paper, target!.Owner);
    Assert.Equal("布局1", target.LayoutName);
}
```
（`PaperSession()` 返回 `(DrawingSession, ACadSharp.Objects.Layout)`：`DxfFixture` 建 Layout + `AddViewport`（**先 AddViewport 再取 AssociatedBlock**）+ 纸面 Line；`new DrawingSession(path, doc)` 内部构造。）

- [ ] **Step 2: 红** → `dotnet test tests/CadAgent.Data.Tests/CadAgent.Data.Tests.csproj --filter "FullyQualifiedName~EditTargetResolverTests|FullyQualifiedName~PaperEditHandlers" --nologo`

- [ ] **Step 3: 实现**（按 Interfaces；含 `Viewport` 守卫与 block 拒绝）

- [ ] **Step 4: 绿 + Data.Tests 全绿**（既有 Find 语义：仅 handle 时模型优先 → 历史测试不变）

- [ ] **Step 5: 提交** `feat(data): 纸面/属性统一目标解析 + 既有编辑处理器三空间化`

---

### Task 2: set-text / set-attribute 处理器

**Files:**
- Add `src/CadAgent.Data/Handlers/TextHandlers.cs`
- Modify `HandlerRegistry`
- Test `tests/CadAgent.Data.Tests/SetTextAttributeTests.cs`

**Interfaces:**
- `set-text {handle,text}`：`TextEntity.Value` / `MText.Value` / `AttributeEntity.Value`；校验（非 null、≤4096、TEXT/ATTRIB 禁换行、空串允许）；`AttributeDefinition` → 拒绝「属性定义默认值编辑未支持」；`Viewport` → 拒绝。
- `set-attribute {handle,value}`（主形态）或 `{insert,tag,value}`（次形态）：仅顶层 Insert.Attributes；同 insert 同 tag 多条 → 拒绝「同标签属性不唯一，请用句柄」；tag 缺失 → 拒绝；insert 不在模型/纸面顶层 → 拒绝。
- 返回 TargetHandle = 被改实体句柄；审计 params 由 StepEngine 原样记录。

- [ ] **Step 1: 失败测试**：TEXT/MTEXT/ATTRIB 改值；换行拒绝（TEXT/ATTRIB）；MText 换行允许；超长拒绝；空串成功；ADEF 拒绝；set-attribute 双形态；重复 tag 拒绝；纸面 ATTRIB 可改且 ComputeHash before≠after。

- [ ] **Step 2: 红 → Step 3: 实现 → Step 4: 绿（Data.Tests 全绿）**

- [ ] **Step 5: 提交** `feat(data): set-text/set-attribute 处理器（实例属性/文字）`

---

### Task 3: CLI 纸面计划 E2E

**Files:**
- Test `tests/CadAgent.Cli.Tests/PaperPlanE2ETests.cs`

**Interfaces:**
- `run <plan>` 无需代码改动（处理器自动注册）；params 契约 `space/layout/attribute/sheetId`。

- [ ] **Step 1: 失败测试**（夹具：模型线 + 布局视口 + 纸面线 + 标题块 Insert(AttributeEntity)）
  - 计划含：纸面 `move-entity`（带 space/layout）、纸面 `delete-entity`、纸面 `add-line`、`set-attribute`、`set-text`（纸面文字）；断言：
    - exit 0、tx-commit、`audit verify` 绿；
    - 落盘回读：纸面实体位移/数量/新线/属性值/文字值正确，模型实体未变；
    - `tx-start` 与 `tx-commit` 哈希不同（ComputeHash 覆盖纸面/属性）；
    - 无 `space/layout` 的历史风格计划（模型 move）行为不变。
- [ ] **Step 2: 运行**：T1/T2 已注册处理器，本任务为**集成钉住**（首跑可能即绿——如实记录「实件先行」；若红则修实现）。
- [ ] **Step 3: 提交** `test(cli): 纸面计划端到端（审计哈希可辨/模型不变）`

---

### Task 4: EditViewModel 工具与参数

**Files:**
- Modify `src/CadAgent.View/ViewModels/EditViewModel.cs`
- Test `tests/CadAgent.View.Tests/EditViewModelTests.cs`

**Interfaces:**
```csharp
enum EditTool { Select, Line, Text, Move, Delete, ChangeLayer, InsertBlock, SetText }
Task<EditResult> MoveAsync(string handle,double dx,double dy,string? space=null,string? layout=null);
Task<EditResult> DeleteAsync(string handle,string? space=null,string? layout=null, bool attribute=false);
Task<EditResult> ChangeLayerAsync(string handle,string layer,string? space=null,string? layout=null);
Task<EditResult> DrawLineAsync(double x1,double y1,double x2,double y2,string? layer=null,string? space=null,string? layout=null);
Task<EditResult> AddTextAsync(double x,double y,string text,double height,string? layer=null,string? space=null,string? layout=null);
Task<EditResult> SetTextAsync(string handle,string text);
Task<EditResult> SetAttributeAsync(string handle,string value);
```
- `KindFor(SetText)="set-text"`；space/layout 仅在非 null 时写入匿名对象（构造函数按条件组装 `Dictionary<string,object>` 或匿名类型分支），保证审计参数不留 `"space":null` 噪声。
- WithHandle 仍只接受十进制 ulong（纸面/ATTRIB 句柄同为 ulong）。

- [ ] **Step 1: 失败测试**：用真实 `DrawingSession`（DxfFixture）+ `EditViewModelTests` 既有模式：`SetTextAsync` 改纸面文字→落盘值变化、`view-edit` 请求审计 params 含 `tool=SetText`、tx step 审计 params 含 `handle/text`；`MoveAsync/DeleteAsync/AddLineAsync` 带 space/layout → 请求审计 params 逐字含该键（无 space 时不出现该键）。
- [ ] **Step 2: 红 → Step 3: 实现 → Step 4: 绿（EditViewModelTests 全绿）**
- [ ] **Step 5: 提交** `feat(view): 编辑 VM 纸面参数与改文字工具入口`

---

### Task 5: MainWindow 纸面交互

**Files:**
- Modify `src/CadAgent.View/MainWindow.cs`
- Test `tests/CadAgent.View.Tests/PaperEditHeadlessTests.cs`（+ SheetSelector 相关调整）

**行为：**
- Route `Paper`：
  - **Move/Delete/ChangeLayer**：纸面实体（`hit.Space==PaperSpace` 且 `BlockName==null` 且 `!Locked`）→ 执行（Move 用纸面增量原值；Delete 确认后执行；ChangeLayer 下拉执行）。
  - **BlockChild**（`hit.BlockName!=null`）→ 拒绝「块子图元编辑将在后续切片提供（Plan-3）」。
  - **Locked** → 既有「视口边框只读」。
- **Line/Text/InsertBlock 空点**：
  - 首击与次击均 `Paper` → 纸面新建（params space="paper", layout=当前布局名；Text 高度=输入值纸面单位）。
  - 均 `Model` → 既有逆映射模型新建（不变）。
  - 混空间 → 清锚拒绝（不变）。
- **新工具「改文字」**：工具条按钮；点击图元/属性（或已选中）→ `SetTextAsync(SourceHandle, EditTextInput.Text)`；目标须 `Text` kind 或 `AttributeTag!=null`，否则「仅文字/属性可改文字」；空输入允许（清空）。
- 状态栏：布局幅面文案改为「（纸面/模型双空间）」；删除「纸面编辑将在后续切片提供」路径（BlockChild 另文案）。
- `TryMoveDelta`：纸面选中时返回原始 dx/dy（不再 ÷s）；模型仍 ÷s：
```csharp
if (route.Intent is EditSpaceIntent.Paper or EditSpaceIntent.BlockChild && selected.Space == RenderSpace.PaperSpace)
    return true; // 纸面增量即图纸增量
```
（BlockChild 纸面在 Plan-2 先被 TryGuardSelection 拒绝，不会到达此处；保留判据防御。）
- 刷新：`RefreshAfterEdit` 已重建纸面+模型；纸面编辑后图纸标题属性变化需可见。

- [ ] **Step 1: 失败测试**（headless；复用 Plan-1 夹具扩展：纸面带一条 Line + 标题块属性）
  1. 纸面线 Move（选中→目标点击）→ 落盘纸面坐标位移、模型不变、tx=1；
  2. 纸面线 Delete（确认 seam）→ 落盘消失、tx=1；
  3. 纸面线 ChangeLayer → 落盘层名变、tx=1；
  4. 视口外 Line 两击 → 纸面新线落盘（space/layout 审计参数）、模型实体数不变；
  5. 视口外 Text → 纸面文字落盘；
  6. 改文字：选标题属性（AttributeTag）→ 输入新值 → 落盘 ATTRIB.Value 变、渲染文本更新；
  7. 块内子图元（Insert 子线）选中 → Move/Delete → 拒绝文案含「Plan-3」、无 tx；
  8. 视口框 Delete → 既有「视口边框只读」（回归）；
  9. Undo 一次纸面编辑 → 纸面/属性还原；
  10. 跨空间两击（模型→纸面）→ 拒绝且无 tx（回归）。
- [ ] **Step 2: 红 → Step 3: 实现 → Step 4: 绿（View.Tests 全绿；既有 450 项不变或按新事实修正并披露）**
- [ ] **Step 5: 提交** `feat(view): 纸面空间编辑交互（移动/删除/改层/新建/改文字）`

---

### Task 6: 门禁、注记与收尾

- [ ] **Step 1:** 全 sln `dotnet test CadAgent.sln --nologo` → 记录通过/跳过（基线 1897/1skip；本计划新增用例使其增长；已知 View flake 隔离复跑留证）。
- [ ] **Step 2:** spec §12 追加 B 闭环注记（追加式，不删旧文）：纸面编辑/属性编辑/新建、处理器与解析器、审计哈希可辨、测试数字、边界（Plan-3 块编辑、视口对象不可编辑、顶层属性 only、文本规则）。
- [ ] **Step 3:** 提交 `docs(spec): 项2 Plan-2（B）闭环注记`。

---

## 执行记录（执行中追加）

- 基线：main b9b693c 全 sln 1897 通过 / 0 失败 / 1 设计内 skip（共 1898 例）；本计划分支 `feat/cad-agent-paper-edit` @ 1621cb0（计划文档提交 `docs(plan): 项2 Plan-2（B 纸面空间编辑）实施计划`）。
- Task 1：`ed30a11`（EditTargetResolver + 既有编辑处理器三空间化）→ 评审修 `a7aa90e`（space 仅 `paper` 预校验、add-circle 纸面化；Ruling R2/R3）。Data 531/531。
- Task 2：`ef58c01`（set-text/set-attribute + Registry + HandlerKindDocs 首版；Ruling R4/R5 记账）。Data 561/561。
- Task 3：`92d74bf`（CLI 纸面计划 E2E 3 例 + HandlerKindDocs 契约补全）→ `bd44cbe`（评审小项：端点/step 参数/契约/注记）→ `350cf10`（set-attribute insert 形态补 space 须配 layout）。Cli 362/362。
- Task 4：`e131608`（EditTool.SetText + VM 纸面参数 + SetTextAsync/SetAttributeAsync）→ 评审修 `fef533d`（DeleteAsync paper/attribute 转发双测 + 私有 ApplyAsync 守卫）。View 459/459。
- Task 5：`26fa8fb`（纸面 Move/Delete/ChangeLayer + 纸面新建 Line/Text/InsertBlock + 改文字工具 + insert-block 纸面写入口）→ 评审修 `2090ae5`（Move 二击复检 BlockChild、Plan-3 常量、死夹具/文案清理、覆盖补齐）。View 470/470、Data 563/563。
- Task 6（本任务）：目标套件 `outputs/plan2-gates/{core,data,view,cli,report,llm,tarchbridge,zwcad,acad}.log` —— **Core 284 / Data 563 / View 470 / Cli 362 / Report 31 / Llm 33 / TArchBridge 76 / Zwcad 16 / Acad 181（180 通过 + 1 既有真机 skip）**，全 sln 两次：`sln.log`（exit 1，2015 通过 / 1 失败 / 1 skip，失败=Zwcad `OpenSaveAs_RoundTrip_EntityCountPreserved` 被 `VendorGuard` 外来 ZWCAD 实例拒绝，环境性）→ `sln-run2.log`（exit 0）**2016 例：通过 2015 / 失败 0 / 跳过 1**；基线 1897 → **净增 118 例**。抖动隔离留证（不改测试）：`view-isolate-checkbox.log`/`view-rerun.log`（pointer flake 1/1 与 470/470）、`acad-isolate-ownership.log`（静态缝顺序抖动 1/1）、`zwcad-rerun.log`（16/16）。
- 收尾：spec §12 追加 B 闭环注记（追加式，旧文 0 删）——`docs(spec): 项2 Plan-2（B）闭环注记`。未 push/merge/改 git config。
- 更正（2026-11-26 合并前加固波）：上述 Task 6「2016 例（Data 563 / View 470）」为早轮口径，最终 HEAD 门禁为 **2028 例 = 2027 通过 / 0 失败 / 1 跳过（Data 569 / View 476 / CLI 362）**；加固波再增 15 例（Data 580、View 480、Cli PaperPlan 3 复跑绿，全 sln 未重跑）。
