# 项3 大图性能设计（严格交互线：空间索引 + LOD + 位图缓存 + 加载/哈希优化）

日期：2026-11-26 ｜ 分支：feat/cad-agent-perf（自 main af72091）
状态：设计方向已认可（严格 <50ms/<2ms；四项全做；裁剪+LOD+位图缓存；真实大图验收）
基线证据：`outputs/perf-baseline/baseline.md`（合成）、`outputs/perf-baseline/real-baseline.md`（真实）
真实标本（只读副本，原件未动，SHA256 记录）：
- `outputs/perf-baseline/specimens/地下室接地平面图.dwg`（12.3MB，abf6f43f…ac5960）
- `outputs/perf-baseline/specimens/地下室强电系统图.dwg`（7.6MB，aa46b50c…e5c67ce）

## 0. 决策记录

| 决策 | 选择 |
|---|---|
| 目标档位 | 严格：交互帧 <50ms、命中 <2ms（headless 软件渲染） |
| 范围 | ①View 交互 ②报告缩略图/快照 ③加载/工作副本 ④ComputeHash 增量缓存 |
| 缓存策略 | 裁剪+LOD + 位图平移缓存（两者都做） |
| 验收载体 | 用户提供的真实大图（上述两份）+ 合成 20k/80k/200k |
| 交互口径 | 两阶段：交互粗绘（<50ms）+ 静止二次精绘 |
| 加载闸口 | 空 census sidecar 增加“源哈希+mtime+已验证 Empty”，源变化即失效（对 R9 的语义调整） |
| 缓存参数 | 位图外扩 128px；Hatch 投影面积 <4px² 或路径点 >2000 → 仅轮廓/bbox |

## 1. 实测基线（结论）

- 真实图块展开后 **55k–77k 图元**；`强电系统图` Hatch 路径点总量 **105 万**；文字 5.7k–8.2k。
- fit 单帧 313–488ms；命中线性扫描 ≈2.8–4ms/次（2000 次 5.6–8.1s）。
- 冷哈希 194–524ms；快照 340–895ms；构建 0.31–0.94s；内存峰值 342–489MB。
- 加载：DWG 首载 6–12s（census+DWG 解析+working 物化）；working 载 2.9–4.9s（**带 persisted census**）。
- 现实现热点：每帧按 `VisibleItems` 全量绘制；无可视裁剪；文字/1M 点 Hatch 全量进 Skia；命中 O(n)；ComputeHash 每次全量；Green 图空 census 每开必重读源 DWG（R9）。

## 2. §1 空间索引 + 视口裁剪（View）

### 2.1 数据
- `RenderItemIndex`（CadAgent.View，纯 C# 可单测）：
  - 构造时对 Items **一次性预计算**包围盒（含 Circle/Arc 半径、Text 字盒、Hatch 跳过 NaN 分隔符、折线顶点）。
  - 均匀网格：世界空间 cell 尺寸 = max(contentWidth, contentHeight) / clamp(√n, 32, 512)，clamp 后再保证单轴格数 ∈ [32,512]；cell→item 索引（按 bbox 多格登记）。
  - **长实体溢出表**：单图元 bbox 覆盖格数 >64（长线/大 Hatch）登记到 overflow 列表，查询时总是参与（避免一格图元跨数百格导致索引爆炸）；索引内存上限与真实图测量记录。
  - API：
    ```csharp
    IReadOnlyList<int> QueryViewport((double MinX,double MinY,double MaxX,double MaxY) rect, IReadOnlySet<string> hiddenLayers);
    int? HitTest(double wx, double wy, double tolerance); // Items 序首个精确命中
    ```
- CanvasViewModel 持索引（Refresh 重建）；`VisibleItems` 保留（兼容），新增 `VisibleItemsInViewport(worldRect)` 供画布。
- 世界视口矩形：由屏幕矩形逆变换（含 Y 翻转与 Fit/Pan/Zoom）。

### 2.2 画布
- `DrawingCanvas.Render` 用视口查询结果（几何相交，非锚点）；隐藏图层在查询内过滤；diff 叠加为对比模式，保持全量语义（见 §3 缓存失效），但同样套 LOD。
- 选择/高亮命中集按查询结果过滤；**高亮不做全量扫描**。

### 2.3 命中
- `HitTest` 走索引：按容差扩张查询格，候选按 Items 序取首个精确命中，保持既有图元命中盒数学（Text 宽=字符×h×0.6 等）；空句柄/隐藏层语义不变。
- 目标：真实两份图纸 2000 次命中总耗时 <4s（即 <2ms/次），合成 200k 同档。

## 3. §2 LOD（两阶段）

### 3.1 质量档
- `RenderQuality { Interactive, Refined }`；`DrawingCanvas` 交互期（pan/zoom/拖动）用 Interactive，停止交互 150ms 后以 Refined 重绘一次（DispatcherTimer）。
- 测试 seam：`internal void SetQualityForTests(RenderQuality q)`（计时器仅在生产路径触发；headless 测试直接切档，不依赖真实计时）。
- 阈值（Refined / Interactive）：

| 元素 | Refined | Interactive |
|---|---|---|
| Text 投影字高 | <2px 跳过；2–6px 简化条；≥6px 字形 | <4px 跳过；4–10px 简化条；≥10px 字形 |
| 微图元屏幕包围盒 | <0.5px → 1px 点 | <1px → 1px 点 |
| Hatch | 投影面积 <4px² **或** 路径点 >2000 → 仅轮廓/bbox；否则填充 | 路径点 >500 或面积 <16px² → 仅 bbox |
| Polyline | 顶点屏幕间距 <0.5px 合并（抽稀，保端点） | <1px 合并 |
| 虚线 Pen | 按（color,thickness,dashSig）缓存 | 同 |
| FormattedText | 按（text, heightBucket, scaleBucket）LRU ≤512 | 不缓存（简化条/跳过为主） |

### 3.2 选中/高亮预算（用户质疑修正）
- 选中与高亮**同走视口裁剪 + LOD**；额外全细节仅当该图元满足预算：投影包围盒 ≤ 视口 25% **且** Hatch 路径点 ≤2000 **且** Polyline 顶点 ≤2000；否则高亮降级为**包围盒/轮廓 + 2px 描边**。
- 语义（选中、命中、审计、编辑目标）不因 LOD 变化；状态栏在降级时注明「高亮为包围盒（图元过密）」。

### 3.3 诚实性
- 概览态跳过文字/填充是**显示降级**，非数据丢失；状态栏/图层面板可选显示「概览」标记（Refined 完成后消失）。
- Snapshot/报告缩略图使用 Refined 档（质量优先），但共享裁剪+LOD 阈值（Hatch/微图元降级）。

## 4. §3 位图平移缓存（DrawingCanvas）

- 缓存内容：当前 Zoom/尺寸/Items/隐藏层/diff 状态下的视口位图，**四周外扩 128px**。
- pan：新旧视口在缓存范围内 → 直接 blit 偏移（含 InterpolationMode.None）；超出边距 → 重建缓存。
- 失效：Zoom 变化、控件尺寸变化、`Refresh`（编辑/撤销）、HiddenLayers 变化、Diff 开关、DPI 缩放变化。
- 叠加层（选中/高亮/中心线/预览/悬停）不烘焙进缓存，实时绘制在 blit 之后；叠加层自身走 §3.2 预算。
- 内存：缓存 ≈ 视口面积+边距（1600×1000+边距 ≈6.9MB/位）；单缓存，重建即替换。
- 正确性：headless 像素测试——同状态 blit 结果与直绘在容差 ≤2/255 通道差内；逐项失效后必须重绘（比较帧签名变化）。

## 5. §4 加载与工作副本

1. **空 census 信任（R9 语义调整）**：`UnparsedCensusStore` sidecar v2 增字段：
   `sourceSha256`（源 DWG 内容哈希）、`sourceSize`、`sourceMtimeUtc`、`verifiedEmpty`（true 表示已从源重建且确认 Empty）。
   - working 载入时：persisted 非空 → 现状；persisted Empty 且 `verifiedEmpty && sourceSha256==当前源哈希 && size/mtime 一致` → **信任 Empty，不重读源 DWG**；否则按 R9 从源重建（保留 fail-closed）。
   - 任何源变化（哈希不符）→ sidecar 失效，重建并重写。
2. **Async 加载（View）**：新增 `MainViewModel.LoadAsync(drawingPath, projectDir, skillDirs)`（后台线程 Load+Build；失败以 ErrorMessage 返回）与同步构造并存（既有测试/CLI 零改动）；启动入口改用异步路径并显示进度/骨架屏，失败语义不变。
3. **不改工作副本格式**：working 仍为 `.working.dxf`；DWG 首载物化与 census 行为保持正确性。若真实图 working 解析仍 >5s，记 tracked（可选后续：AC1027 working 或二进制缓存，需另立切片）。
4. 目标：两次打开同一 Green 图的第二次不重读源 DWG（当前冷载 6–12s → working 载 2.9–4.9s 或更低）；UI 不阻塞。

## 6. §5 ComputeHash 修订缓存

- `IDrawingSession` 增：
  ```csharp
  long Revision { get; }
  void InvalidateHash();   // 语义：文档内容已变
  ```
- `DrawingSession`：缓存 `(Revision, hash)`；`InvalidateHash` 递增 Revision 并清缓存。
- 失效点：StepEngine 每步 handler 成功后（含 undo/rollback 后 `RestoreState`/`Reload` 自身递增）、`Reload`、`RestoreState`、`SaveWorking` 不改内容不递增；`MarkModified` 供直接变更（handlers 内已由引擎统一调用，无需逐个改）。
- 契约：任何绕过 StepEngine 的直接 Doc 变更必须调用 `InvalidateHash()`；Data/View 现有直接变更测试同步调用（披露）。
- 哈希投影口径**不变**；冷哈希成本保留（194–524ms），重复哈希（审计/闸口/回归）降为 O(1)。

## 7. §6 验收与基线工具

- 提交 `tools/CadAgent.PerfBaseline/`（net8 控制台，非测试套件）：
  - 合成 20k/80k/200k（线+20% 文字）；
  - `--drawing <path>` 真实图（只读副本）；
  - 输出 markdown：load/build/hash/snapshot/interactive frame/refined frame/hit×2000/内存。
- 目标（headless 软件渲染，报告为准）：
  - 真实两图：交互帧 **<50ms**、命中 **<2ms/次**、Refined 帧 <150ms（首帧/静止）、快照 <150ms；
  - 合成 200k 线：交互帧 <50ms、命中 <2ms；
  - 双开同 Green 图：第二次打开不重读源 DWG（审计侧可观测）。
- CI 不硬断言绝对耗时（机器差异）；功能回归仍由既有 2172 用例 + 真实 6k 标本承载；性能数字入 PR 证据。

## 8. 明示边界

- 不改 GPU/渲染后端；headless 软件渲染为验收口径，真机 GPU 可能更快。
- LOD 为视觉降级：概览态文字/填充/细图元可能不显示或简化；Refined 恢复；导出/快照使用 Refined。
- 位图缓存仅平移复用；缩放必重绘（交互档 <50ms）。
- 加载绝对读速受 ACadSharp 限制；本切片只做异步与空 census 信任，工作副本格式不改。
- ComputeHash 缓存仅对经引擎/显式失效的变更正确；绕过路径须 `InvalidateHash()`（文档化契约）。
- 索引内存 O(n)（候选格重复登记）；上限与真实图测量记录。
- 冲突/遮挡顺序仍按 Items 序；LOD 不改变绘制顺序语义。

## 9. 文件影响面（计划输入）

- View：`CanvasViewModel`（索引/视口查询）、`DrawingCanvas`（LOD/缓存/两阶段）、新 `RenderItemIndex`、`RenderSnapshotProvider`（Refined+LOD 复用）、`MainViewModel`/启动异步加载。
- Data：`UnparsedCensusStore`（v2 sidecar）、`DrawingSession`（Revision/缓存/空信任加载）、`StepEngine`（每步 InvalidateHash）。
- Core：`IDrawingSession`（Revision/InvalidateHash）、StepEngine 调用点。
- Tests：索引/命中矩阵；LOD 决策（阈值边界）；缓存失效与像素一致性；census v2 信任/失效；revision 缓存正确性；headless 交互两阶段；性能工具冒烟。
- Docs：spec §12 闭环注记；工具 README。
