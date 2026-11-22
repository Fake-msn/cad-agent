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

## 执行记录（2026-11-22，Task 11）

环境：Windows 11 (10.0.26320) x64 · Intel i7-9700K @3.60GHz · 16GB RAM · Intel UHD Graphics 630 ·
主显示器 2560×1440 @125% 缩放 + 副显示器 1440×2560 竖屏 @100% 缩放（双活显示器，WinForms Screen 枚举实证）·
.NET SDK 8 · Avalonia 11.3.2。执行工具：computer-use SDK（SendInput/UIAutomation/Windows.Graphics.Capture）。
夹具：`outputs/view-closure/verify.dxf`（15 实体→24 render items/0 skipped，RenderModelTests 夹具生成法，
生成器 `outputs/view-closure/gen/`）与 `big.dxf`（10000 实体：6000 Line+2500 Circle+1500 Arc）；
验证在 `%TEMP%\cadagent-view-closure-verify` / `-big` 项目副本上执行（.dxf 编辑为原位写，夹具原件零污染）。
拉起：`dotnet run --project src/CadAgent.Cli -- view --drawing <副本> --project <项目> [--skill examples/skills/sample-institute]`。

| # | 协议项 | 结果 | 截图 |
|---|---|---|---|
| 1 | 中键拖拽平移手感 | **注入限制（诚实标注）**：SDK `drag` 仅左键（文档无 mouse_button；实传 middle 被忽略），注入后视图零平移——与计划4 同限制，非应用缺陷；headless `Canvas_MiddleDrag_PansByScreenDelta` 覆盖同一控件路径且绿。真机中键手感仍待人工 | view-closure-03-middle-drag-attempt.png |
| 2a | Text 自定义字高 7 | **已验证**：字高输入 7+内容"真机字高7"→画布落字字形明显大于 3.5/4.2 对照；0 层计数 14→15、状态栏 24→25 items、Undo 启用；tx 997a98d2 commit ✓ | view-closure-06-text-height7.png |
| 2b | Move 圆移动 | **已验证**：Move 工具点选圆周（圆心内部不命中——hit-test 沿轮廓，符合设计）→黄色高亮+状态栏 `Selected 78 (Circle, layer WALL)`→目标点落位圆随移；tx a55b99ea commit ✓ | view-closure-07-move-circle.png |
| 2c | Delete 模态两路 | **已验证**：第一击激活工具、第二击弹 `确认删除` 模态（独立窗口，文案含句柄 79+审计/Undo 提示）；**取消**→模态闭、选中保持、零 tx；**确认删除**→弧消失、AXIS 3→2、24 items、tx 6353a0a0 commit ✓ | view-closure-08-delete-modal.png / view-closure-09-delete-confirmed.png |
| 2d | ChangeLayer | **已验证**：ChangeLayer 激活+选中圆→层下拉选 AXIS 即应用：圆红→绿、AXIS 2→3/WALL 4→3、下拉回显 AXIS；tx 7b9ae85d commit ✓ | view-closure-10-changelayer.png |
| 2e | InsertBlock 缩放2旋转90° | **已验证（缩放视觉实证；旋转见限制注记）**：块名 MARK+缩放 2+旋转 90 输入→落点展开 3 items（圆半径视觉加倍≈2×）、24→27 items、tx bf436792 commit ✓。**限制**：MARK 十字块 90° 旋转自对称，旋转角视觉不可区分——度→弧度与 R*S 变换序由 headless（EditViewModelTests/RenderModelTests）钉住 | view-closure-11-insertblock-s2-r90.png |
| 2f | 审计 live-rebuild（T5 承接） | **已验证**：5 笔 tx（text/move/delete/changelayer/insert）编辑后时间线**无需重开**逐笔 live 出现 commit ✓（列表虚拟化需滚动见新 tx） | view-closure-12-audit-live-rebuild.png |
| 3 | 渲染视觉 | **已验证**：Dimension 匿名块展开（尺寸线+界线+箭头+文字"100"）；Hatch 实心红填充+岛 EvenOdd 挖空；dash 三档（[5,2] 直挂/ByLayer [3,1] 蓝/LineTypeScale2 [10,4]）虚实可辨；复杂线型 FENCELINE1 实线降级（明示边界）；镜像 OCS 圆对关于 Y 轴对称落位（(-5,5)/(5,5)）；空图层 EMPTY-LAYER (0) 橙色样显示。图案填充轮廓/复杂线型 shapes/倾斜 OCS 近似为明示 deferred 边界，不假装 | view-closure-01-launch-render.png / view-closure-02-render-detail.png / view-closure-04-mirror-ocs-dash.png |
| 4 | 计划预览 | **无 profile 引导已验证**：`localhost:11434` 探测 connection refused（Ollama 不可得，诚实记录）→全链不执行；"从问题填充"自 mandatory SK-SAMPLE-001 拼意图入框；"生成计划"→行内红字「无活动 LLM profile——请先在 CLI 执行 llm add / llm use 配置（spec §8）。」**限制**：云端出境同意真机首启未执行（任务约束不配置真实密钥；headless T10 覆盖同意/拒绝两路） | view-closure-13-plan-preview-noprofile.png |
| 5 | 高 DPI/多显示器 | **部分已验证+限制**：125% 非整数缩放下文字/画布清晰不错位（ Skia DPI 感知）；窗口拖移至竖屏副显示器（100% DPI）→**per-monitor DPI 切换布局完整、视口自动重适配 Zoom 1x**（双显示器实证，非"硬件不可得"）。**限制**：150% 缩放切换被权限分类器拦截（OS 级共享显示设置，未获显式授权不重试）——150% 专项仍待人工；现有 125%/100% 双 DPI 已覆盖非整数缩放路径 | view-closure-14-dpi125-percent.png / view-closure-15-second-monitor.png |
| 6 | 大图性能 | **已验证（数字+方法论注记）**：10000 items 启动全渲染（BIG-A/B/C 3334/3333/3333）；滚轮 zoom 1→1.73x 锚点正确无残影；Line 编辑后状态栏 10000→10001+时间线 tx 6668b156 live ✓。**RefreshAfterEdit 耗时**：第二击注入→状态栏计数更新 wall ≤1212ms（状态轮询 2 次命中，分辨率≈0.6s；含 StepEngine 10k 哈希/快照+Refresh 全链）；组件基准（同机 scratch harness）：DxfReader load 523ms、RenderModelBuilder.Build 10k items min 4.1/avg 5.6/max 9.6ms（5 跑）。**限制**：帧率无插桩，平移/缩放流畅度为主观判定（注入→捕获间无可见卡顿）；大图中键平移同项 1 注入限制 | view-closure-16-big-10k-launch.png / view-closure-17-big-zoom.png / view-closure-18-big-edit-refresh.png |
| 7 | 审计链 | **已验证**：CLI `audit --project` 两项目均「审计链完整」——verify 26 条 / big 5 条，exit 0 | outputs/view-closure/audit-verify.log / audit-big.log |

Issues parity 顺带实证：Refresh 后「6 issues, 1 mandatory, 3 skipped」，mandatory 深红徽章 SK-SAMPLE-001（--skill 直通），
与 CLI check 口径一致（view-closure-05-issues-refresh.png / view-closure-05b-issues-mandatory.png）。

**缺陷发现**：本轮真机未发现应用缺陷（零 TDD 修复）；全部限制为注入/权限/环境边界，已逐项诚实标注。
**收尾**：两 View 验证进程已终止（big 窗口标题栏 X 坐标点击正常关闭；verify 窗口位于副显示器负坐标区，
X 坐标点击与 UIA「关闭」invoke 均未关闭——成因未定论：computer-use 跨显示器坐标映射限制或标题栏按钮注入响应，
**不认定为应用缺陷**；改以进程级终止收尾，编辑均已 tx 落盘无数据损失。留人工复核副显示器标题栏关闭手感）。
性能/夹具/日志归档：outputs/view-closure/（fixture-manifest.txt、bench-result.txt、hardware-baseline.txt、screens.txt、gen/、各 log）。
