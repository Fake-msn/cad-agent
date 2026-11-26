# 项2 Plan-3：块内子图元/块定义编辑（C）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 开启「子图元」模式后可编辑块定义内容（移动/删除/改层/改文字），全实例同步生效，带实例计数确认与硬守卫；模型幅面/纸面布局行为在模式关闭时保持现状。

**Architecture:** `block` 参数从 Plan-2 的显式拒绝转为块定义检索（一层，不递归块中块目标）；世界/纸面增量经 Insert 链复合仿射逆解为块局部增量；全部变更仍经 StepEngine（整文档快照/审计/B| 哈希）。View 增加「子图元」Toggle（默认关）与变更确认。

**Tech Stack:** C#/.NET 8；ACadSharp 3.8.35；Avalonia 11.3.2 headless；xUnit。

**Spec:** `docs/superpowers/specs/2026-11-26-item2-paper-subentity-design.md` §3.1（解析）、§3.2（子图元语义）、§4.3（Toggle/确认）、§4.4（实例计数）、§5.3（边界）
**前置:** Plan-2 已合并 main `31534c8`（纸面/属性编辑；`block` 现为 Plan-3 拒绝）

## Global Constraints

- 分支：`feat/cad-agent-block-edit`（自 main 31534c8）；不合并不 push（逐次授权）。
- Git 身份仅环境变量（逐命令）：`GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`；禁止 `git config`。
- 全部编辑经 StepEngine；禁止 View 直改 Doc。
- **块定义编辑=全实例生效**：每次变更前必须确认（块名+约 N 实例）；审计 params 含 `block`（View 另附 `affectedInstances`，引擎不依赖）。
- 守卫（fail-closed）：匿名块（`*D`/Dimension，`BlockTypeFlags.Anonymous`）、XRef/XRefOverlay/XRefDependent、`IsDynamic`、`IsUnloaded` 一律拒绝；`AttributeDefinition` 不可改（set-text 拒绝，Plan-2 规则保持）。
- 目标仅为**块内一层直接实体**（`BlockRecord.Entities` 按句柄）；不处理块中块的目标解析（嵌套子图元的块名=最内层，即编辑该最内层块定义）。
- `block` 参数：对 move/delete/change-layer/set-text 生效；与 `space/layout/attribute` 互斥（同时给出 fail-closed）。add-line/add-text/add-circle 的 `block` 仍拒绝（insert-block 的 block=块名，语义不同）。
- 块局部增量：世界（模型）或纸面坐标增量 → Insert 链复合仿射（OCS∘InsertAffine，外→内）线性部逆解；奇异（零缩放等）拒绝，不近似。
- 实例计数为**约**值：模型+纸面顶层 Insert 展开（深度 ≤2 与渲染守卫一致），按路径出现次数计；深度截断如实标注“约”。
- `ComputeHash` 已含 `B|`/`ADEF|` 行（Plan-1），块编辑 before≠after；**不得改哈希格式**。
- 块内 `AttributeDefinition`（模板）与块内嵌套 Insert 的 ATTRIB **不编辑**（前者 Plan-2 已定边界，后者属块定义内容但本切片不做）——本切片块内文字仅 TEXT/MTEXT。ACadSharp 块标志类型：`ACadSharp.Blocks.BlockTypeFlags`（Anonymous/XRef/XRefOverlay/XRefDependent…）。
- 测试夹具 `%TEMP%` 清理；SQLite 清理前 `ClearAllPools()`；Windows GBK 输出重定向 UTF-8；已知 flake 隔离复跑留证。
- 门禁基线：main 31534c8 全 sln 2042 pass / 0 fail / 1 designed skip。

## 文件结构

- Modify `src/CadAgent.Core/Cad/Affine.cs`（线性逆）
- Modify `src/CadAgent.Data/Handlers/EditTargetResolver.cs`（BlockDefinition 空间 + 守卫）
- Modify `src/CadAgent.Data/Handlers/EditHandlers.cs`（delete owner 块定义；移除四类 block 拒绝）
- Modify `src/CadAgent.Data/Handlers/TextHandlers.cs`（set-text 支持 block；set-attribute 仍拒绝 block）
- Modify `src/CadAgent.Data/Handlers/LineHandlers.cs`（`RejectBlockParam` 仅 add-* 保留；文档）
- Add `src/CadAgent.Data/Blocks/BlockInstanceCounter.cs`
- Add `src/CadAgent.View/Rendering/BlockEditMath.cs`（链解析/复合/逆解；复用 RenderModel OCS）
- Modify `src/CadAgent.View/Rendering/RenderModel.cs`（暴露 internal OcsAffine）
- Modify `src/CadAgent.View/ViewModels/EditViewModel.cs`（Move/Delete/ChangeLayer/SetTextAsync 增 `block`）
- Modify `src/CadAgent.View/MainWindow.cs`（子图元 Toggle/确认/块编辑路由/实例计数/文案）
- Modify `src/CadAgent.View/ViewModels/…`（无新 VM 文件）
- Tests：`AffineInverseTests`、`BlockEditResolverTests`、`BlockEditHandlerTests`、`BlockInstanceCounterTests`、`BlockEditMathTests`、`BlockEditHeadlessTests`、`BlockPlanE2ETests`
- Docs：`HandlerKindDocs.cs`、spec §12 闭环注记

---

### Task 1: Resolver 块定义空间 + 守卫

**Files:**
- Modify `src/CadAgent.Data/Handlers/EditTargetResolver.cs`
- Test `tests/CadAgent.Data.Tests/BlockEditResolverTests.cs`

**Interfaces:**
- `EditOwnerKind` + `BlockDefinition`；`EditTarget` 增可选 `string? BlockName = null`（末位）。
- `Resolve/TryResolve`：`block` 存在时仅检索该 `BlockRecord.Entities`（OrdinalIgnoreCase 块名精确一层）；与 `space/layout/attribute` 同给 → null；未知块/受保护块 → null（处理器给专用文案）。
- 新增 `public static BlockRecord? FindBlock(DrawingSession s, string name)` 与 `public static bool IsBlockEditable(BlockRecord b)`（Anonymous/XRef*/Dynamic/Unloaded → false）。
- 文案常量：`BlockNotFoundMessage`、`BlockProtectedMessage`（匿名/外部参照/动态/未加载）。

- [ ] **Step 1: 失败测试**：块内实体按 block+handle 命中（BlockDefinition + BlockName）；无 block 不检索块定义（既有）；块名大小写不敏感；未知块 null；匿名块（`BlockTypeFlags.Anonymous`）/XRef/Dynamic/Unloaded 用 `IsBlockEditable=false` 拒绝；block+space、block+attribute 互斥 null；同名句柄在模型与块定义时：无 block→模型命中（块不检索），有 block→块命中（不歧义）。
- [ ] **Step 2: 红** → `dotnet test tests/CadAgent.Data.Tests --filter "FullyQualifiedName~BlockEditResolverTests" --nologo`
- [ ] **Step 3: 实现**（保留 Plan-2 行为；`BlockDeferredMessage` 从解析器移除，改由 add-* 专用拒绝使用）

```csharp
public const string BlockNotFoundMessage = "块定义中未找到句柄: {0}（块 {1}）";
public const string BlockProtectedMessage = "块不可编辑（匿名/外部参照/动态/未加载）: {0}";

// TryResolve 内、handle 读取后：
if (HasValue(p, "block"))
{
    if (HasValue(p, "space") || HasValue(p, "layout") || HasValue(p, "attribute")) return null;
    string? name = HandlerHelpers.OptString(p, "block");
    BlockRecord? block = FindBlock(s, name ?? "");
    if (block is null || !IsBlockEditable(block)) return null; // 处理器已用 FindBlock/IsBlockEditable 给专用文案
    var match = block.Entities.FirstOrDefault(e => e.Handle == handle);
    return match is null ? null : new EditTarget(match, EditOwnerKind.BlockDefinition, BlockName: block.Name);
}

public static BlockRecord? FindBlock(DrawingSession s, string name) =>
    string.IsNullOrEmpty(name) ? null
        : s.Doc.BlockRecords.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

public static bool IsBlockEditable(BlockRecord b) =>
    !b.IsAnonymous && !b.IsDynamic && !b.IsUnloaded
    && (b.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay | BlockTypeFlags.XRefDependent)) == 0;
```
- [ ] **Step 4: Data.Tests 全绿（Plan-2 既有 block 拒绝测试按新事实改写：四类改成功路径，add-* 保留拒绝）**
- [ ] **Step 5: 提交** `feat(data): 块定义编辑目标解析与块守卫`

---

### Task 2: Affine 线性逆（Core）

**Files:**
- Modify `src/CadAgent.Core/Cad/Affine.cs`
- Test `tests/CadAgent.Core.Tests/AffineInverseTests.cs`

**Interfaces:**
```csharp
public bool TryInvertLinear(out Affine inverse); // 仅线性部（Tx/Ty=0）；det≈0 → false
```
- 测试：旋转 30°+非均匀缩放 (2,0.5) 随机向量 `inverse.Apply(forward delta)` ≈ 原 delta（1e-9）；零缩放 det=0 → false；单位阵 → 单位阵。

- [ ] **Step 1: 失败测试（上述三例）→ 红**

- [ ] **Step 2: 实现**

```csharp
/// <summary>线性部逆（Tx/Ty 置 0）：用于把世界/纸面增量换算为块局部增量。det≈0 → false。</summary>
public bool TryInvertLinear(out Affine inverse)
{
    double det = A * D - B * C;
    if (Math.Abs(det) < 1e-12) { inverse = default; return false; }
    inverse = new Affine(D / det, -B / det, -C / det, A / det, 0, 0);
    return true;
}
```

- [ ] **Step 3: 绿 → 提交** `feat(core): Affine 线性逆（块局部增量用）`

---

### Task 3: BlockEditMath（链复合与增量逆解）

**Files:**
- Modify `src/CadAgent.View/Rendering/RenderModel.cs`（`internal static Affine OcsAffine(XYZ normal)` 包装既有 OcsMapper）
- Add `src/CadAgent.View/Rendering/BlockEditMath.cs`
- Test `tests/CadAgent.View.Tests/BlockEditMathTests.cs`

**Interfaces:**
```csharp
public static (bool Ok, double Dx, double Dy, string Error) WorldDeltaToBlockDelta(
    ACadSharp.CadDocument doc, IReadOnlyList<string> insertChain, double dx, double dy);
```
- 链解析：chain[0] 在模型顶层或任一纸面顶层按句柄找 Insert；后续句柄在前一 Insert.Block.Entities 中找；缺失 → 失败“插入链解析失败: <handle>”。
- 复合：`t = t.Compose(OcsAffine(ins.Normal).Compose(Affine.InsertAffine(ins)))`（与 RenderModel 同序）；线性逆解 delta；奇异 → “块插入变换不可逆（零缩放）”。
- 测试：单层旋转 0°+缩放(2,3)：世界 (10,6) → 局部 (5,2)；旋转 90°+缩放(2,3) 用正向 Apply 推导期望（不写死手算）；两层嵌套旋转+缩放链往返；纸面布局顶层 Insert 链（paper coords）；缺失链/零缩放失败。

- [ ] **Step 1: 失败测试 → 红**

- [ ] **Step 2: 实现**

```csharp
internal static Affine OcsAffine(XYZ normal) => OcsMapper(normal).Map; // RenderModel 暴露

public static (bool Ok, double Dx, double Dy, string Error) WorldDeltaToBlockDelta(
    CadDocument doc, IReadOnlyList<string> insertChain, double dx, double dy)
{
    Affine t = Affine.Identity;
    IReadOnlyList<Entity> scope = doc.Entities;
    foreach (string h in insertChain)
    {
        Insert? ins = scope.OfType<Insert>()
            .FirstOrDefault(i => string.Equals(i.Handle.ToString(CultureInfo.InvariantCulture), h, StringComparison.Ordinal));
        if (ins?.Block is null) return (false, 0, 0, $"插入链解析失败: {h}");
        t = t.Compose(RenderModelBuilder.OcsAffine(ins.Normal).Compose(Affine.InsertAffine(ins)));
        scope = ins.Block.Entities;
    }
    if (!t.TryInvertLinear(out Affine inv)) return (false, 0, 0, "块插入变换不可逆（零缩放）");
    var (lx, ly) = inv.Apply(dx, dy);
    return (true, lx, ly, "");
}
```
（纸面宿主链：调用方先传纸面顶层 `layout.AssociatedBlock.Entities` 为 scope；实现可加可选 `IEnumerable<Entity> roots` 参数。）

- [ ] **Step 3: 绿 → 提交** `feat(view): 块局部增量逆解（Insert 链仿射）`

---

### Task 4: 实例计数（咨询值）

**Files:**
- Add `src/CadAgent.Data/Blocks/BlockInstanceCounter.cs`
- Test `tests/CadAgent.Data.Tests/BlockInstanceCounterTests.cs`

**Interfaces:**
```csharp
public static int Count(CadDocument doc, string blockName, int maxDepth = 2);
```
- 遍历模型顶层 + 纸面布局顶层；遇 Insert 且 `Block.Name` OrdinalIgnoreCase 相等 → +1；递归块内 Insert 至深度守卫（≤2）；布局关联块不作根（防重复）。测试：两实例、嵌套（外层块含内层引用）、深度截断、无名块 0、大小写不敏感。

- [ ] **Step 1: 失败测试 → 红**

- [ ] **Step 2: 实现**

```csharp
public static int Count(CadDocument doc, string blockName, int maxDepth = 2)
{
    if (string.IsNullOrEmpty(blockName)) return 0;
    int n = 0;
    void Walk(IEnumerable<Entity> entities, int depth)
    {
        foreach (Entity e in entities)
            if (e is Insert ins && ins.Block is not null)
            {
                if (string.Equals(ins.Block.Name, blockName, StringComparison.OrdinalIgnoreCase)) n++;
                if (depth < maxDepth) Walk(ins.Block.Entities, depth + 1);
            }
    }
    Walk(doc.Entities, 0);
    if (doc.Layouts is not null)
        foreach (Layout l in doc.Layouts.Where(l => l.IsPaperSpace && l.AssociatedBlock is not null))
            Walk(l.AssociatedBlock!.Entities, 0);
    return n;
}
```

- [ ] **Step 3: 绿 → 提交** `feat(data): 块实例计数（确认提示用，约值）`

---

### Task 5: 处理器块定义化

**Files:**
- Modify `EditHandlers.cs`（delete owner 块定义；入口移除四类 block 拒绝）
- Modify `TextHandlers.cs`（set-text block 生效；set-attribute 仍拒绝 block）
- Modify `LineHandlers.cs`（`RejectBlockParam` 文档/仅 add-* 使用）
- Test `tests/CadAgent.Data.Tests/BlockEditHandlerTests.cs`

**行为：**
- move-entity {block,handle,dx,dy}：dx/dy 为**块局部**增量；实体 OCS 逆投影仍按实体 normal（既有 switch）。
- delete-entity {block,handle}：从 `BlockRecord.Entities` 删除（Remove 结果校验）；Viewport 拒绝；ADEF 删除允许？**不允许**（定义模板保护，返回“属性定义不可删除”）。
- change-layer {block,handle,layer}：任意直接实体；Viewport 拒绝。
- set-text {block,handle,text}：块内 TEXT/MTEXT 可改；ADEF 拒绝（既有文案）；换行规则同 Plan-2。
- 入口：`block` 存在 → 不调 `RejectBlockParam`；先 `FindBlock`/`IsBlockEditable`（失败专用文案）；再 Resolver。
- 未知/受保护块：`未知块名: X` / `块不可编辑（匿名/外部参照/动态/未加载）: X`。
- 审计 Target = 实体句柄；params 原样含 block。

- [ ] Step 1 失败测试：块内线 move（局部增量 applied，全实例读回同变）/delete/change-layer；块内 TEXT set-text；ADEF 删除/set-text 拒绝；Viewport 拒绝；未知块/匿名块/Dynamic/Xref 拒绝；block+space 拒绝；块外句柄 + 错误 block 名 → 未命中；模型无 block 行为不变。
- [ ] 红→实现→绿（Data.Tests 全绿；Plan-2 的 block 拒绝用例改写）→ 提交 `feat(data): move/delete/change-layer/set-text 块定义空间`

---

### Task 6: View 子图元模式 + 确认 + 路由

**Files:**
- Modify `MainWindow.cs`、`EditViewModel.cs`
- Test `tests/CadAgent.View.Tests/BlockEditHeadlessTests.cs`（+ 既有 BlockChild 拒绝用例改写）

**Interfaces:**
- `EditViewModel` `MoveAsync/DeleteAsync/ChangeLayerAsync/SetTextAsync` 增可选 `string? block = null`（非 null 才写入 params）。
- MainWindow：`internal ToggleButton? SubEntityToggle`（默认 false）、`internal Func<string,Task<bool>>? ConfirmEditOverride`；状态栏提示「子图元模式：块内编辑=全实例生效」。
- 路由（EditRouting 不变）：
  - `BlockChild` 且 Toggle 关：模型幅面→既有父整体；布局幅面→「块子图元需开启「子图元」模式（全实例生效）」。
  - `BlockChild` 且 Toggle 开：block=item.BlockName、handle=item.SourceHandle、chain=item.ParentInsertChain；View 预检 `BlockInstanceCounter.Count` 与块守卫；确认文案「将修改块定义 <名>，影响约 N 个实例（含嵌套估算）；所有引用同步变化。继续？」；取消→无 tx。
  - Move：增量来源——纸面项=纸面 delta；模型项在布局=纸面 delta÷s；模型幅面=世界 delta；统一经 `WorldDeltaToBlockDelta(chain)` 得局部增量；失败显示错误无 tx。
  - Delete/ChangeLayer/SetText：block 参数直传；SetText 仍要求 Text/ATTRIB 目标（ATTRIB 走 Plan-2 实例路径，BlockName 为 null 不进入块分支）。
  - 锁框先于一切；属性项（AttributeTag）语义不变。
- 编辑后 RefreshAfterEdit（全实例重建）。

- [ ] Step 1 失败测试（headless，两实例块夹具 + 嵌套/旋转缩放夹具）：
  1) Toggle 关布局 BlockChild → 新文案、无 tx；模型幅面父整体行为不变（既有用例保持）；
  2) Toggle 开 + 确认 → 块内线 Move：两实例均位移；局部增量在旋转 90°+缩放(2,3)下精确（期望值由正向 InsertAffine 变换推导，勿手算；逆解 [[0,-3],[2,0]] 对 (10,6) 得 (3,-10/3)）；审计含 block；
  3) Delete 块内线 → 两实例缺失；4) ChangeLayer；5) SetText 块内文字；6) 取消确认→无 tx；7) 匿名块（Dimension 子图元）拒绝；8) Dynamic/Xref 拒绝；9) 属性项在 Toggle 开时仍实例级（不误入块编辑）；10) Undo 还原块编辑（两实例恢复）。
- [ ] 红→实现→绿（View.Tests 全绿；既有 BlockChild 拒绝/整父用例按新事实改写并保留旧语义断言）→ 提交 `feat(view): 子图元模式与块定义编辑（确认/计数/路由）`

---

### Task 7: CLI 块编辑 E2E + 契约文档

**Files:**
- Modify `src/CadAgent.Data/Handlers/HandlerKindDocs.cs`
- Test `tests/CadAgent.Cli.Tests/BlockPlanE2ETests.cs`

- 计划：块内 move（局部增量）、delete、change-layer、set-text；同块两实例；断言落盘两实例同步、模型/纸面其余不变、tx-start≠tx-commit（B| 行变化）、audit verify 绿、step params 含 block。
- 负例：block+space 拒绝；未知块；匿名/动态块；add-line+block 仍拒绝。
- HandlerKindDocs：四类注明 `block?`（块名，一层直接实体，局部增量，全实例生效，匿名/外部参照/动态/未加载拒绝；与 space/attribute 互斥）；insert-block 的 block=块名注记保持。
- [ ] 红/实件→绿→提交 `test(cli): 块编辑计划端到端 + 契约补全`

---

### Task 8: 门禁、注记与收尾

- [ ] 全 sln `dotnet test CadAgent.sln --nologo` → 记录（基线 2042/0/1；本计划新增使增长；已知 flake 隔离复跑留证）。
- [ ] spec §12 追加 C 闭环注记（追加式）：块编辑/子图元模式/守卫/实例计数/局部增量逆解/CLI 契约/测试数字/边界（仅一层直接实体、ADEF 不可改、嵌套块=最内层块定义、计数“约”、XRef/Dynamic 拒绝）。
- [ ] 计划执行记录提交；`docs(spec): 项2 Plan-3（C）闭环注记`。

---

## 执行记录（执行中追加）

### Task 8 收尾记录（2026-11-26，feat/cad-agent-block-edit）

- 计划/勘误：6ddf7d8（计划入册）、8fe163c（Task3 期望值示例修正）、bce9b20（Task6 逆解示例数值更正）。
- Task 1：0b6dbf3（resolver/块守卫）→ 977530b、702181c（评审修复）。
- Task 2：751f53e（`Affine.TryInvertLinear`）→ d8f71b4（det≠1 用例/失败契约）。
- Task 3：e9bb9d5、6cfea6d（`BlockEditMath` 局部增量逆解）→ 8e05c2e（链首跨空间歧义 fail-closed）。
- Task 4：641cdfc（`BlockInstanceCounter`）→ 07aea06（计数口径澄清/补强）。
- Task 5：cff2f99（四类处理器块定义化）→ c6af41d（未知块文案/互斥优先/断言）。
- Task 6：6aeeabf（子图元模式/确认/路由）→ f0156fd（SourceHandle 定位/目标空间状态/文案）。
- Task 7：a2da287（CLI 块计划 E2E + 契约收尾）。
- Task 8：927cade（评审 Minor 文案收尾 + 两步部分写入 E2E）→ 本提交（本执行记录 + spec §12 注记）。
- **门禁（Task 8 实测，outputs/plan3-gates/）**：套件 Core 288 / Data 652 / View 507 / Cli 368 / Report 31 / Llm 33 / TArchBridge 76 / Zwcad 16 / Acad 181（180 通过 + 1 既有设计内 skip）；全 sln `dotnet test CadAgent.sln --nologo` **2152 例 = 2151 通过 / 0 失败 / 1 跳过**（一次通过，0 flake）。基线 main 31534c8：2042 通过 / 0 失败 / 1 skip → **净增 109 例**。本计划门禁轮零事实漂移修复（无新增测试改动）；Task 8 代码改动仅文案/常量/用例，聚焦复跑 Cli BlockPlan 6/6、Data SetAttribute+HandlerKindDocs 34/34、View 状态栏 1/1 先行确认。
