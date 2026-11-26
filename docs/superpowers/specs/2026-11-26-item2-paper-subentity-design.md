# 项2 纸面/子图元编辑设计（方案一：双空间统一渲染 + 空间感知路由 + StepEngine 处理器族）

日期：2026-11-26 ｜ 分支：feat/cad-agent-view-deepread（堆叠于 PR#34）
状态：设计已逐节认可（§1 基础层 / §2 处理器族 / §3 View 交互 / §4 CLI/LLM / §5 测试边界里程碑）
前置：项1 审图报告导出已落盘（head 2be18d9，PR#34）

## 0. 决策记录（用户裁定）

| 决策 | 选择 |
|---|---|
| 切片范围 | A+B+C 全做（纸面视图逆映射编辑模型 → 纸面空间渲染+编辑 → 块内子图元编辑），按序交付 |
| 子图元语义 | 两者都要：块定义几何（移动/删除/改层）+ 属性/文字内容；块定义编辑=全实例生效 |
| 执行通道 | StepEngine 新处理器（View/CLI/LLM 同源、全审计、可回滚、无旁路） |
| 纸面渲染 | 完整渲染纸面空间（标题栏/图框/纸面注记 + 视口边框） |
| 架构 | 方案一（双空间统一渲染 + 空间感知编辑路由 + 处理器族） |

## 1. 目标与非目标

**目标**
1. 布局幅面从「只读视图」变为「纸面/模型双空间可编辑视图」：视口内编辑模型（纸→模型逆映射），视口外编辑纸面空间实体/属性。
2. 块内子图元可编辑：块定义几何与文字（全实例生效，强警告+审计）、实例属性值（单实例）。
3. 全部编辑仍经 StepEngine（tx-start/step/tx-commit、整文档快照、可 undo/rollback、审计哈希前后可辨）。
4. 纸面空间完整可见（标题栏/图框/纸面注记/属性文本/视口边框），与打印视图语义一致。

**非目标（明示不做，见 §5.3）**
TwistAngle≠0 或非矩形视口的模型编辑；视口对象自身编辑；XREF/动态块/匿名块块内编辑；属性定义默认值编辑；块内新建实体；新建/删除布局、改视口、REFEDIT 会话 UI。

## 2. §1 基础层（里程碑 F，先行）

### 2.1 RenderItem 加性扩展（`src/CadAgent.View/Rendering/RenderModel.cs`）
在既有位置参数末尾追加可选参数（源码兼容；`Handle` 的 `"{insert}:{i}"` 语义保持不变）：

```csharp
public enum RenderSpace { ModelSpace, PaperSpace }

record RenderItem(..., string Handle, bool Closed = false,
    IReadOnlyList<double>? DashPattern = null, bool Fill = false,
    RenderSpace Space = RenderSpace.ModelSpace,
    string? SourceHandle = null,                     // 可点击源的真正 DXF 句柄：
                                                     // 普通实体=Handle；块子图元=子实体句柄；
                                                     // 属性=ATTRIB 句柄；视口框=视口句柄
    string? BlockName = null,                        // 所属块定义（最内层）
    IReadOnlyList<string>? ParentInsertChain = null, // Insert 句柄链（块局部增量逆解）
    string? AttributeTag = null,
    bool Locked = false);                            // 视口边框=只读
```

### 2.2 纸面渲染（B 的渲染半支，在 F 内完成）
- `RenderModelBuilder.BuildPaperSpace(doc, layoutName)`：`layout.AssociatedBlock.Entities`（排除 Viewport），identity 变换，`Space=PaperSpace`；Insert 递归沿用深度守卫（≤2 层），子图元带 `BlockName/ParentInsertChain`。
- `BuildViewportFrame(vp)`：视口矩形（`Center ± (Width/2, Height/2)`）→ 4 条线，`Space=PaperSpace, Locked=true, Handle/SourceHandle=视口句柄`。
- **属性渲染（新增，跨模型/纸面）**：Insert 展开后追加 `insert.Attributes`（`AttributeEntity.Value/InsertPoint/Height/Rotation`），`Handle="{insert}:attr:{tag}"`、`SourceHandle=ATTRIB 句柄`、`AttributeTag=tag`。标题栏字段从此可见。
  - 披露：含 ATTRIB 的既有图纸渲染项数变化，相关计数断言按新事实修正。
- 布局合成顺序：`[纸面实体, 视口内模型, 视口边框]`（模型层在纸面之上、边框最上）。

### 2.3 纸→模型逆映射（`src/CadAgent.Core/SheetModel.cs`）
- `ViewportTransform.PaperToModel(paper, viewTarget, viewHeight, paperCenter, paperHeight)` = `ModelToPaper` 精确逆（s=paperHeight/viewHeight）。
- View 每幅面持 `LayoutViewportContext { ViewportHandle, PaperRect, Scale, ViewTarget, Center, CanMapModel }`。
- `|TwistAngle|>1e-9` → `CanMapModel=false`：模型编辑拒绝（「视口带旋转，纸→模型映射未支持；纸面编辑仍可用」），纸面编辑不受影响。
- **非矩形视口裁剪**：ACadSharp 3.8.35 未暴露可检裁剪边界——不做检测、不假装检测（注记；若未来 API 暴露再补拒绝）。
- 单测：往返恒等（1e-9）、twist 拒绝、视口内外判定。

### 2.4 ComputeHash 全空间扩展（承重修复）
现状仅投影「图层+模型顶层实体」→ 纸面/块定义/属性编辑后哈希不变（审计=无修改、零写入/回滚校验失真）。
- 统一 `ProjectEntity`（模型/纸面/块定义共用）：类型字段投影（Line/TextEntity/MText/LwPolyline/Polyline2D/Circle/Arc/Insert+Attributes/Hatch 计数），未知类型回退 `handle|type|layer|color`。
- 规范行：
  - `L|` 图层（不变）；
  - `E|{handle}|…` 模型顶层实体（扩展类型字段，MText 等纳入）；
  - `P|{layout}|{handle}|…` 纸面顶层实体；
  - `B|{block}|{handle}|…` 块定义实体；
  - `A|{insert}|{tag}|{value}` 属性值；
  - `ADEF|{block}|{handle}|{tag}|{value}` 属性定义默认值。
- 排序：布局名/块名 Ordinal、实体句柄升序 → 确定性。
- 迁移注记：哈希值相对旧版本变化；旧审计记录按存储字段验链不受影响；深读面板旧 overlay 的 DrawingHash 可能提示「来源哈希不一致」（既有文案，诚实保留）。
- 单测：纸面移动/块定义移动/属性改值 → 哈希变化；无修改 Save/Reload → 稳定；排序确定性。

## 3. §2 处理器族（`src/CadAgent.Data/Handlers`）

原则：零旁路——纸面/块内/属性编辑均为普通 Step；快照已是整文档（`CaptureState` 序列化整 Doc），无需改。

### 3.1 统一目标解析 `EditTargetResolver`
```csharp
enum EditOwner { ModelSpace, PaperSpace, BlockDefinition, InsertAttribute }
record EditTarget(Entity Entity, EditOwner Owner, string? LayoutName, string? BlockName, ulong? InsertHandle);
EditTarget? Resolve(DrawingSession s, JsonElement p);
```
- **默认检索序（仅 `handle`）**：模型顶层 → 纸面顶层（布局名 Ordinal 序）→ Insert 属性（模型+纸面递归）。**默认不检索块定义**（防误伤全实例）。
- 显式限定（fail-closed，未命中不回退）：`space:"paper"`+`layout` / `block:"<名>"` / `attribute:true`。
- 同句柄多空间命中（脏图）→ 拒绝并要求显式限定。
- 兼容：历史计划（仅 handle）行为逐字不变。

### 3.2 既有处理器扩展（不新增 kind）
| kind | 新增 | 坐标语义 |
|---|---|---|
| `move-entity` | 纸面实体；`block` 下块定义实体 | 目标空间增量（纸面=纸坐标；块内=块局部）；实体 OCS 逆投影沿用 `OcsDelta` |
| `delete-entity` | 纸面实体（含 ATTRIB）；`block` 下块定义实体 | — |
| `change-layer` | 纸面/块内 | —
| `add-line`/`add-text` | `space:"paper"`+`layout` → 写 `layout.AssociatedBlock.Entities`；默认模型 | 纸面坐标 |

块内编辑必须显式 `block`。审计 params 逐字含 `space/layout/block`；View 可附 `affectedInstances`（引擎不校验）。

### 3.3 新处理器
- `set-text {handle,text,block?}`：`TextEntity.Value` / `MText.Value` / `AttributeEntity.Value`。校验：非 null、≤4096 字符；TextEntity/AttributeEntity 拒绝 `\n`（MText 允许）；空串允许（审计记 `""`）；`AttributeDefinition` 拒绝（「属性定义默认值编辑未支持」）。
- `set-attribute {handle,value}`（主形态）或 `{insert,tag,value}`（次形态）：递归扫描模型+纸面 Insert.Attributes；同 insert 同 tag 多条 → fail-closed（要求句柄形态）。实例级语义。
- ATTRIB 的移动/删除/改层经 3.2 默认解析直接可达（实例级）。

### 3.4 块编辑硬守卫
匿名块（含 Dimension 匿名块）/XREF/`IsUnloaded`/`IsDynamic` → 拒绝；`block` 不存在/实体不在块 → 拒绝。校验先于任何变更；失败走既有 `step-failed` 审计。

### 3.5 通道
- 注册入 `HandlerRegistry.All` → CLI `run`/agent/skill 可达（角色 Draw；Red/Yellow 闸口不变）。
- View `EditViewModel` 薄方法：`SetTextAsync`/`SetAttributeAsync`；`MoveAsync/DeleteAsync/ChangeLayerAsync/AddLineAsync/AddTextAsync` 增可选 `space/layout/block`——仍构 StepPlan → StepEngine。

## 4. §3 View 交互与路由

### 4.1 只读闸替换为空间路由
- 删除 `_layoutSheetActive` 全幅面只读硬闸；每幅面持 `LayoutViewportContext`。
- 纯函数（可单测）：
```csharp
record EditRoute(RenderSpace Space, bool InViewport, bool CanMapModel,
                 bool IsBlockChild, bool Locked);
EditRoute Route(LayoutViewportContext? vp, RenderItem? hit, (double X,double Y) paperPoint);
```
按序判定：Locked→只读；`BlockName!=null`→块子图元（需子图元模式）；ModelSpace→模型（`CanMapModel`）；PaperSpace→纸面；未命中→点 `PaperRect` 内=模型，否则纸面。
无有效视口：纸面可编辑，模型编辑拒绝。

### 4.2 工具矩阵（布局幅面不再整体禁用）
| 工具 | 视口内（模型） | 纸面 | 块子图元（模式开） |
|---|---|---|---|
| Select | 选择 | 选择 | 选择 |
| Line | 两击坐标逆映射 | 纸面坐标 | — |
| Text/InsertBlock | 插入点逆映射；字高/缩放为模型单位 | 纸面坐标/纸面单位 | — |
| Move | dx,dy ÷ s | dx,dy 原样 | 世界增量→块局部增量（Insert 链仿射逆解） |
| Delete/ChangeLayer | 选中源实体 | 同左 | 块定义编辑（确认后） |
| 改文字（新工具） | 选中源实体 | 同左 | 块内文字（全实例） |

- 预览锚点统一存纸坐标，提交时按路由一次性换算；状态栏显示目标空间（模型/纸面/块定义 <名>）。
- 两击跨空间 → 拒绝（不得混空间成线）。
- 状态栏「（只读）」退役 → 「（纸面/模型双空间）」；`LayoutReadOnlyMessage` 常量移除（注释留痕）。

### 4.3 选中与子图元模式
- 新增 `SelectedSource {Handle=SourceHandle ?? Handle, Owner, BlockName?, AttributeTag?, Locked}`（VM 层可测）。
- **子图元模式 Toggle（默认关）**：关=旧语义（子图元选中编辑父实体整体，行为零变化）；开=目标为子实体（块定义语义，确认后执行），状态栏提示「块内编辑：影响所有引用实例」。
- 深读/问题定位：`SelectByHandle` 追加 `SourceHandle` 精确匹配。

### 4.4 确认与错误面
- 块定义任何变更（移动/删除/改层/改文字）→ 模态确认（复用 `ConfirmDialog`；测试 seam 扩为 `ConfirmEditOverride`）：「将修改块定义 <名>，影响约 N 个实例（含嵌套估算）；所有引用同步变化。继续？」
- 硬拒绝走 `EditError` 行内：匿名/动态/XREF、twist、缺视口、跨空间成线、视口边框只读、句柄歧义、不支持实体类型。
- 实例数 N：View 递归统计模型+纸面 Insert 展开（深度守卫内），“约”值，界面如实标注。

### 4.5 刷新与一致性
编辑成功 → 既有 `RefreshAfterEdit`：重读 sheets/模型图元/当前幅面（纸面+模型+视口上下文）/Layers/Undo/审计 live-rebuild/深读面板。
块定义编辑后 overlay/块指纹可能陈旧 → 深读面板既有 DrawingHash 不一致告警自然提示（不新增机制）。

## 5. §4 CLI/LLM 契约

### 5.1 通用可选参数（非法 fail-closed）
`space:"paper"`、`layout:"<名>"`、`block:"<名>"`、`attribute:true`、`sheetId`。
`sheetId` 透传供上下文闸/审计定位（View 纸面编辑与视口内模型编辑均附当前幅面 Id）；**当前编辑处理器未声明语义需求，闸口不拦截**（不假装闸口生效）。

### 5.2 kind 矩阵
- 既有扩展：`move-entity`/`delete-entity`/`change-layer`/`add-line`/`add-text` 增可选限定。
- 新增：`set-text {handle,text,block?}`、`set-attribute {handle,value}` 或 `{insert,tag,value}`。
- 块编辑一律 `block`+实体句柄；`affectedInstances` 允许出现在 params，引擎不依赖。

示例：
```json
{"planId":"p1","steps":[
 {"kind":"move-entity","params":{"handle":"5F2","dx":50,"dy":0,"space":"paper","layout":"布局1","sheetId":"L5F0"}},
 {"kind":"set-attribute","params":{"handle":"7C1","value":"栖贤雅庭"}},
 {"kind":"change-layer","params":{"block":"A2图框","handle":"2D0","layer":"0"}},
 {"kind":"add-text","params":{"x":420,"y":297,"text":"出图日期","height":3.5,"space":"paper","layout":"布局1"}}
]}
```

### 5.3 CLI 行为
无新命令、无新输出字段；tx/审计/快照/exit 语义不变；Red/Yellow 全图级拒绝不变；角色 Draw 不变。仅 handle 时模型优先 → 历史行为不变。

### 5.4 LLM/Skill
`AgentOrchestrator` JSON 契约不变；系统提示词补充：改块必须显式 `block` 并说明全实例影响；纸面编辑建议带 `sheetId`；不得编造 `affectedInstances`。文档更新（本 spec + 示例注释），skill 模板行为不变。

## 6. §5 测试、边界与里程碑

### 6.1 里程碑（均堆叠 PR#34 分支；逐里程碑 commit + 全门禁）
- **F 基础**：§2.1-2.4。验收：往返/twist/纸面渲染/哈希四类变化单测；全 sln 绿；计数断言按新事实修正；court 标本哈希耗时记录（tracked）。
- **A 纸面视图内编辑模型**：路由+逆映射+守卫。验收：路由矩阵单测；headless 视口内落点回验/移动增量/拒绝路径；模型幅面既有 403 项行为不变。
- **B 纸面渲染可交互+纸面编辑**：纸面 Move/Delete/ChangeLayer、纸面 add、标题栏属性 `set-attribute`、视口框只读。验收：headless 标题栏可见/属性改文字后渲染更新/新增/拒绝；CLI 纸面计划 E2E；哈希覆盖纸面编辑+undo 还原。
- **C 子图元/块定义编辑**：模式开关+确认；块内文字（set-text+block）/改层/删除先行，**块内移动最后**（仿射逆解：旋转/非均匀缩放/嵌套/OCS；奇异或不可逆变换拒绝，不近似）。实例属性 `set-attribute` 属 B（单实例语义）。验收：模式关=旧行为；开+确认=全实例同步；匿名/动态/XREF 拒绝；旋转+缩放夹具坐标精确；审计含 block。

### 6.2 回归口径
每里程碑全 sln（基线 1796 通过/1 skip）；View 已知 audit.db teardown flake 隔离复跑留证；真实标本以合成夹具为主，可选真机抽查不设硬门禁（不伪造真机证据）。

### 6.3 明示边界
- TwistAngle≠0：模型编辑拒绝（纸面可用）；非矩形视口裁剪不可检测（不假装检测）。
- 视口对象自身移动/缩放/删除不做；新建/删布局、改幅面名不做。
- XREF/动态块/匿名块（含标注）块内编辑不做；属性定义（ATTDEF 默认值）编辑不做；块内新建实体不做。
- 不支持类型（MLeader/Table 等）：纸面渲染回退占位、编辑拒绝「暂不支持实体类型」。
- 嵌套块编辑只针对最内层块定义；实例影响数为“约”值。
- ComputeHash 扩面 → 新哈希与旧版不同、旧 overlay 可能告警（文档明示）。
- REFEDIT 会话式 UI 不做。

### 6.4 风险与对策
| 风险 | 对策 |
|---|---|
| 哈希扩展回归面大 | F 先行独立门禁；无固定哈希断言；全 sln 强验证 |
| 属性渲染改变既有计数 | 按新事实修断言并披露 |
| 块移动变换逆解复杂 | C 内最后实现；旋转+非均匀缩放+嵌套夹具先红后绿；奇异/不支持变换拒绝不近似 |
| 误改全实例 | 子图元模式默认关 + 模态确认 + 审计 block |
| 大图哈希耗时 | F 验收记录耗时；缓存/增量投影留 tracked |

## 6.5 实施计划拆分建议
本设计体量超出单份实现计划的健康边界，writing-plans 阶段按里程碑拆为多份计划、顺序执行：
Plan-1（F 基础 + A 逆映射编辑）→ Plan-2（B 纸面渲染交互+纸面/属性编辑）→ Plan-3（C 子图元/块定义编辑）。
每份计划独立可验收、独立门禁；全部堆叠于 PR#34 分支（用户既定偏好）。

## 7. 文件影响面（实施计划输入）
- Core：`SheetModel.cs`（PaperToModel/上下文）；StepEngine 不改（快照已整文档）。
- Data：`DrawingSession.ComputeHash`；`Handlers/*`（新增 `EditTargetResolver`、`SetTextHandler`、`SetAttributeHandler`，扩展既有处理器与 `HandlerRegistry`）。
- View：`Rendering/RenderModel.cs`（新字段/纸面构建/属性渲染）；`MainWindow.cs`（路由、工具矩阵、确认、刷新）；`ViewModels/EditViewModel.cs`（薄方法）；`Controls/DrawingCanvas.cs`（无需改，命中复用）；测试夹具。
- CLI：无代码改动（处理器自动注册）；文档更新。
- Docs：本 spec + 终端设计 spec §12 逐里程碑闭环注记。
