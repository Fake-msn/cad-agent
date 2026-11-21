# 计划4 真机 UI 验证协议与结果（T10）

日期：2026-11-22 · 环境：Windows 11 (10.0.26320) x64 · .NET SDK 8.0.428 · Avalonia 11.3.2（Fluent，code-driven 无 XAML）
执行方式：真机启动 `CadAgent.View` + computer-use SDK（SendInput/UIAutomation/Windows.Graphics.Capture）操作与截图；
截图存于本目录（PNG，窗口客户区含标题栏）。headless 对照覆盖见 `tests/CadAgent.View.Tests`（Avalonia.Headless.XUnit，真实 Skia 帧）。

## 1. 夹具与启动

夹具：`fixture-plan4.dxf`（本目录，ACadSharp 3.8.35 DxfWriter 生成，R2000 ASCII）。实体清单：

| 实体 | 图层 | 备注 |
|---|---|---|
| Line (0,0)-(200,0) | WALL | 红 |
| Circle c(60,40) r20 | WALL | 红 |
| Arc c(140,40) r15 0..90° | AXIS | 绿 |
| LwPolyline 闭合矩形 (0,60)-(50,90) | WALL | 红 |
| Text "字高违规4.2" h=4.2 | W-墙体 | 字高系列违规（5.0.2）+ 汉英混用（13.0.1） |
| Text "合规字高3.5" h=3.5 | W-墙体 | 合规字高 |
| Insert MARK（圆+线块）@(180,70) | AXIS | 块展开 2 render items |

共 8 render items。图层：0 / AXIS / W-墙体 / WALL。

再生成（临时 scratch 工程，引用 ACadSharp 3.8.35；生成器源码见本报告附录 A 的等价描述：
`new CadDocument()` + 上表实体 + `DxfWriter.Write(fs, doc)`；块经 `new BlockRecord("MARK")` + `doc.BlockRecords.Add` + `new Insert(mark)`）。

启动（验证项目目录 `%TEMP%\cadagent-plan4-verify`，`.cadagent/roles.yaml` 将本机 Windows 账号映射为 Review）：

```
dotnet run --project src/CadAgent.View -- --drawing docs/verification/fixture-plan4.dxf --project %TEMP%\cadagent-plan4-verify
```

CLI 拉起（--skill 直通验证，第 9 项）：

```
dotnet run --project src/CadAgent.Cli -- view --drawing docs/verification/fixture-plan4.dxf --project %TEMP%\cadagent-plan4-verify --skill examples/skills/sample-institute
```

## 2. 验证项与结果

| # | 协议项 | 结果 | 截图 |
|---|---|---|---|
| 1 | 启动渲染正确（线/圆/弧/多段线/文字/块展开） | **已验证**：8 render items，几何/图层色/文字/块均正确；标题 `CadAgent — fixture-plan4.dxf`；状态栏 `fixture-plan4.dxf — 8 render items \| Zoom 1x` | 01-launch-render.png |
| 2a | 滚轮缩放（光标锚点） | **已验证**：Zoom 1.44x，状态栏同步 | 02-zoom-in.png |
| 2b | 中键拖拽平移 | **手动验证待执行**：computer-use 的 middle drag 注入三次未产生平移（注入限制，非应用缺陷——headless `Canvas_MiddleDrag_PansByScreenDelta` 覆盖同一控件路径且绿）。03-pan-attempt.png 为注入尝试留证（视图未变） | 03-pan-attempt.png |
| 2c | Fit（视口变化重适配） | **已验证**：最大化→内容重适配 Zoom 1x；还原→再次适配 | 04-fit-maximized.png / 04b-restored-fit.png |
| 3 | 图层隔离 + Show all | **已验证**：双击 WALL 行→仅 WALL 可见（其余复选框 unchecked）；Show all 恢复。**T10 修复后**（见 §3 缺陷 2） | 05-layer-isolate-WALL.png / 05b-show-all.png |
| 4 | 问题清单刷新 + 条款引用 | **已验证**：4 issues / 0 mandatory / 3 skipped；行内徽章+规则 Id+条款；展开见 GB/T 50001-2017 13.0.1 原文摘录/建议/检测依据/Targets | 06-issues-refresh.png / 06b-issue-expanded.png |
| 5 | 手动画线 + 撤销 | **已验证**：Line 两击→9 items、图层 0 计数 3、Undo 启用；Undo→8 items、Undo 禁用；再画线→第二 tx（9 items）。审计链 CLI verify 绿（9 条：view-edit 请求先于 tx） | 07-line-added.png / 08-undo.png / 09-line-second-tx.png |
| 6 | 审计时间线选择 + 高亮 + 前后对比 | **已验证**：重开后时间线列两 tx（commit ✓ 绿）；选 tx→画布高亮 step 目标+状态栏 Tx 摘要；Diff→added 绿叠加+`Diff +1 -0 ~0` | 10a-reopen-timeline.png / 10b-tx-selected-highlight.png / 10-timeline-diff.png |
| 7a | 签认 Review 角色→成功 | **已验证**（异操作者 tx）：种子项目 tx 操作者 `drawer`，本机 Review 账号签认→`approved ✓ by 水晶花园的电脑\aaa12`。同账号自审被拒见 7b | 11b-signoff-approved.png |
| 7b | 自审禁止（spec §5.3） | **已验证**：本机账号编辑的 tx 以 Review 签认→行内拒绝「绘制者不能自审」，签认未落盘 | 11-signoff-selfreview-refused.png |
| 7c | Draw 角色→按钮禁用 | **已验证**：roles.yaml 改 Draw 重开→Approve/Reject disabled | 12-signoff-draw-gate.png |
| 8a | 纠偏 Scan + Map 联动 | **已验证**：Scan→2 ambiguous（W-墙体  hint 疑似墙体 + 样例句柄；0）；Map W-墙体→Wall→1 ambiguous、annotations v2、overlay 落盘 | 13a-semantic-scan.png / 13-semantic-map.png |
| 8b | 裁定升级→issues 联动 | **已验证**：R-50001-5.0.2→Mandatory（reason 必填校验先拒空理由一次，行内报错正确）；Apply 后 issues **自动**刷新 4 issues/1 mandatory，深红 Mandatory 徽章；当前裁定列表显示升级记录 | 14-adjudication-mandatory-linkage.png |
| 9 | CLI view --skill 直通 | **已验证**：CLI exit 0 拉起 View；issues 5 issues/1 mandatory（SK-SAMPLE-001 示例院标 mandatory，深红徽章+条款 1.0 引用），无需手动配置 | 15-cli-view-skill-passthrough.png |

审计链收尾：`cadagent audit --project` 两验证项目均「审计链完整」（9 条 / 7 条）。

## 3. 真机验证发现并修复的缺陷（T10，TDD）

1. **画布未裁剪**：缩放/平移后图元溢出绘制到图层/签认面板之上。修复：`DrawingCanvas.ClipToBounds = true`；
   headless 回归 `IntegrationHeadlessTests.Canvas_ClipsDrawingToItsBounds`。修复前留证：旧 02 截图（已被修复后截图覆盖）。
2. **图层名双击不隔离**：旧行结构整行是 CheckBox 内容——双击名字被 Toggle 消费（两次翻转抵消）且 DoubleTapped 不冒泡。
   修复：行重构为 CheckBox（仅勾选框）+ 色样 + 名称（名称区指针事件不被处理，双击冒泡到 ListBox 隔离）；
   headless 回归 `MainWindowHeadlessTests.LayerPanel_DoubleClickOnName_Isolates`。修复前留证：旧 05 截图（已覆盖）。
3. **徽章对比度（T5 minor 承接）**：Mandatory #E53935 / Informational #9E9E9E 白字对比不足 → 深红 #B71C1C / 深灰 #616161
   （白字 ≥4.5:1），黄底黑字不变；headless WCAG 计算断言 `IssueBadges_AllThreeForces_MeetWcagContrast`。真机 14/15 截图可见深红徽章。

## 4. 手动验证待执行清单（诚实标注）

- **中键拖拽平移真机手感**：注入限制未能自动化（§2 项 2b）；逻辑 headless 已覆盖。需人工中键拖拽确认。
- **Text / Move / Delete / ChangeLayer / InsertBlock 工具真机操作**：本轮真机仅执行 Line + Undo；其余工具
  headless 全覆盖（EditPanelHeadlessTests / EditViewModelTests），真机手感待人工执行。
- **多显示器/DPI 缩放下的布局**：本轮单显示器 100% 缩放；高 DPI 待人工。
- **大图性能（万级实体 pan/zoom 帧率）**：夹具 8 items；性能待人工/后续基准。

## 5. 复现步骤

1. 再生夹具（§1）或使用本目录 fixture-plan4.dxf。
2. 建临时项目目录 + `.cadagent/roles.yaml`（`roles: { 'DOMAIN\user': Review }`）。
3. §1 启动命令；按 §2 表操作；截图对照本目录 PNG。
4. 签认成功路径需异操作者 tx：用 StepEngine 以其他身份种子一条 add-line（本轮经临时 scratch 工程，
   `FixedId("drawer", Role.Draw)` + `StepEngine.Run`），或以第二 Windows 账号编辑后签认。
5. `dotnet run --project src/CadAgent.Cli -- audit --project <项目>` 收尾验链。
