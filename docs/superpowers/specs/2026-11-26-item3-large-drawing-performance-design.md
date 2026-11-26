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
- `DrawingCanvas.Render` 用视口查询结果（几何相交，非锚点）；隐藏图层在查询内过滤；diff 叠加为对比模式，保持全量语义（diff 绕过缓存，失效规则见 §4 位图平移缓存），但同样套 LOD。
- 选择/高亮命中集按查询结果过滤；**高亮不做全量扫描**。

### 2.3 命中
- `HitTest` 走索引：按容差扩张查询格，候选按 Items 序取首个精确命中，保持既有图元命中盒数学（Text 宽=字符×h×0.6 等）；空句柄/隐藏层语义不变。
- 目标：真实两份图纸 2000 次命中总耗时 <4s（即 <2ms/次），合成 200k 同档。

## 3. §2 LOD（两阶段）

### 3.1 质量档
- `RenderQuality { Interactive, Refined }`；`DrawingCanvas` 交互期（滚轮缩放 / 中键平移拖动 / 编辑工具点击）用 Interactive，停止交互 150ms 后以 Refined 重绘一次（DispatcherTimer）。
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
- 「概览」**只跟实际显示的基元位图**：仅在它本身为交互档时标注；交互期仍复用已烘焙的 Refined 位图（仅缩放/平移）时不标。
- 选中/高亮叠加层按当前质量档实时绘制；精绘守卫命中（缓存已是 Refined 快照、不重建位图）时仍须按 Refined 重绘叠加层，否则会残留交互档的粗叠加（小字不可见/hatch 为 bbox）。
- Snapshot/报告缩略图使用 Refined 档（质量优先），但共享裁剪+LOD 阈值（Hatch/微图元降级）。

## 4. §3 位图平移缓存（DrawingCanvas）

- 缓存内容：当前 Zoom/尺寸/Items/隐藏层状态下的视口位图，**每边外扩 128px**（长/宽各 +2×128）。
- pan：新旧视口在缓存范围内 → 直接 blit 偏移（含 InterpolationMode.None）；超出边距 → 重建缓存。
- 失效：Zoom 变化（交互期先缩放旧位图，静止 150ms 重建）、控件尺寸/DPI 缩放、`Refresh`（编辑/撤销）、HiddenLayers 变化、世界原点变化；Diff 开关**绕过缓存**（diff 帧直绘并释放缓存位图，diff 内容不进缓存）。
- 叠加层（选中/高亮/中心线/预览/悬停）不烘焙进缓存，实时绘制在 blit 之后；叠加层自身走 §3.2 预算。
- 内存：缓存 ≈ 视口面积+四周 128px（1600×1000+128 ≈9.3MB/位）；单缓存，重建先释放旧位图再渲染
  （DPI=2 时像素面积 ×4，缓存约 37MB/位——峰值仍按单缓存披露，Skia 渲染目标内部可能临时再分配）。
- 正确性：headless 像素测试——同状态 blit 结果与直绘在容差 ≤2/255 通道差内；逐项失效后必须重绘（比较帧签名变化）。

## 5. §4 加载与工作副本

1. **空 census 信任（R9 语义调整）**：`UnparsedCensusStore` sidecar v2 增字段：
   `sourceSha256`（源 DWG 内容哈希）、`sourceSize`、`sourceMtimeUtc`、`verifiedEmpty`（true 表示已从源重建且确认 Empty）。
   - working 载入时：persisted 非空 → 现状；persisted Empty 且 `verifiedEmpty && sourceSha256==当前源哈希 && size/mtime 一致` → **信任 Empty，不重读源 DWG**；否则按 R9 从源重建（保留 fail-closed）。
   - 任何源变化（哈希/size/mtime 不符）→ sidecar 失效，重建并重写。
   - **安全边界（明示）**：sidecar 为本地普通文件，任何本地进程均可读写/改写；`verifiedEmpty`
     只是免重读源的性能捷径，**不是**安全边界——伪造指纹可让 Yellow 源冒充 Green；
     引擎/版本（sidecar `Producer` 字段，仅可观测，载入忽略）同样未签名。需完整校验时依赖
     源 DWG 解析（R9）或外部可信存证。
   - 写入窗口（明示）：Save 的 size/mtime/hash 取自 Save 时刻的源，census 来自此前的 Load；
     源在两者之间被换图会把旧 census 挂上新指纹（窗口极短，不闭合）。
2. **Async 加载（View）**：新增 `MainViewModel.LoadAsync(drawingPath, projectDir, skillDirs)`（后台线程 **Load + RenderModelBuilder.Build + 幅面登记簿（Sheets）**；失败以 ErrorMessage 返回）与同步构造并存（既有测试/CLI 零改动，同步构造同样产出 Items/Sheets）；启动入口改用异步路径并显示进度/骨架屏，失败语义不变。MainWindow 直接消费 `MainViewModel.Items`/`Sheets`，不在 UI 线程重建。
3. **不改工作副本格式**：working 仍为 `.working.dxf`；DWG 首载物化与 census 行为保持正确性。若真实图 working 解析仍 >5s，记 tracked（可选后续：AC1027 working 或二进制缓存，需另立切片）。
4. 目标：两次打开同一 Green 图的第二次不重读源 DWG（当前冷载 6–12s → working 载 2.9–4.9s 或更低）；UI 不阻塞。

## 6. §5 ComputeHash 修订缓存

- Core 新增**可选能力接口** `IHashRevision`（不改 `IDrawingSession`，避免全部测试 fake 爆改）：
  ```csharp
  public interface IHashRevision { long Revision { get; } void InvalidateHash(); }
  ```
- `DrawingSession : IDrawingSession, IHashRevision`：缓存 `(Revision, hash)`；`InvalidateHash` 递增 Revision 并清缓存。
- 失效点：StepEngine 每步 handler 成功后 `(session as IHashRevision)?.InvalidateHash()`；`Reload`/`RestoreState` 自身递增；`SaveWorking` 不改内容不递增。非 `IHashRevision` 会话（测试 fake）永不走缓存，行为不变。
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
- 交互期缩放复用旧位图（按比例变换），静止 150ms 精绘；越界/失效重建为全量重绘。
  模拟重建（质量档+缩放失效；与生产同一 RebuildCache 例程），**77.6/120.4ms，TARGET 未达**
  <50ms——生产触发点为交互期越界/编辑点击；<50ms 仅缓存复用路径（见 §12.2 注与 §12.6）。
- 加载绝对读速受 ACadSharp 限制；本切片只做异步与空 census 信任，工作副本格式不改。
- **空 census 信任非安全边界**：sidecar 可被任何本地进程改写，伪造 `verifiedEmpty`/指纹可使
  Yellow 源显示为 Green；`Producer` 仅可观测、未签名。防篡改需外部可信存证（不在本切片）。
- 真实 TCH-DWG「换源后回 Yellow」路径由既有 census 闸口用例承载（合成 TCH DWG 写入器不支持）；
  调用层面的「源变更 → 指纹失效 → 重写 sidecar」由 CLI 用例钉住。
- ComputeHash 缓存仅对经引擎/显式失效的变更正确；绕过路径须 `InvalidateHash()`（文档化契约）。
- 索引内存 O(n)（候选格重复登记）；上限与真实图测量记录。
- 冲突/遮挡顺序仍按 Items 序；LOD 不改变绘制顺序语义。

## 9. 文件影响面（计划输入）

- View：`CanvasViewModel`（索引/视口查询）、`DrawingCanvas`（LOD/缓存/两阶段）、新 `RenderItemIndex`、`RenderSnapshotProvider`（Refined+LOD 复用）、`MainViewModel`/启动异步加载。
- Data：`UnparsedCensusStore`（v2 sidecar）、`DrawingSession`（Revision/缓存/空信任加载）、`StepEngine`（每步 InvalidateHash）。
- Core：`IDrawingSession`（Revision/InvalidateHash）、StepEngine 调用点。
- Tests：索引/命中矩阵；LOD 决策（阈值边界）；缓存失效与像素一致性；census v2 信任/失效；revision 缓存正确性；headless 交互两阶段；性能工具冒烟。
- Docs：spec §12 闭环注记；工具 README。

## 12. 闭环注记（T7 实测，2026-11-27）

本节为项3 收口：机制落地清单、真实图前后数字、R9/哈希语义与 racked 边界。数字来源
`outputs/perf-baseline/after-real.md`、`after-synth.md`（工具生成，与基线
`real-baseline.md`/`baseline.md` 同机同口径对照；验收运行记录于 T7 报告）。

### 12.1 落地机制

| 切片 | 机制 | 主要位置 |
|---|---|---|
| T1 | 空间索引（均匀网格 + 长实体 overflow 表）、视口裁剪、命中宽相+精相 | `View/Rendering/RenderItemIndex`、`CanvasViewModel` |
| T2 | 统一绘制器 + LOD 决策 + 画笔/文字缓存 LRU | `View/Rendering/{CanvasProjection,RenderItemDrawer}` |
| T3 | 位图平移缓存（128px 外扩，blit）+ 两阶段交互（静止 150ms 精绘） | `View/Controls/DrawingCanvas` |
| T4 | 快照/缩略图改走 Refined 绘制器（共享裁剪 + LOD） | `View/Rendering/RenderSnapshotProvider` |
| T5 | 空 census 信任（sidecar v2 指纹）+ 异步加载 + 状态注记 | `Data/UnparsedCensus`、`Data/DrawingSession`、`View/MainViewModel` |
| T6 | ComputeHash 修订缓存（`IHashRevision`，引擎每步失效） | `Core/Cad/IHashRevision`、`Data/DrawingSession`、`Core/StepEngine` |
| T7 | 基线工具（合成 20k/80k/200k + `--drawing`）入 sln | `tools/CadAgent.PerfBaseline` |

### 12.2 真实图前后（headless Skia 软件渲染，1600×1000）

| 指标 | 接地图 before → after | 强电图 before → after | 目标 |
|---|---|---|---|
| 交互帧（blit 复用缓存，3 次中位） | 488 → **1.95ms**（1.36–2.35） | 313 → **2.44ms**（2.26–3.71） | <50ms PASS |
| 模拟重建（质量档+缩放失效；与生产同一 RebuildCache 例程，全量重建+blit，3 次中位） | — → **77.6ms**（79.3/77.6/77.0） | — → **120.4ms**（125.7/120.4/117.1） | <50ms **TARGET 未达** |
| 命中 2000 次总 / 每次（3 次中位） | 5567ms / 2.78ms → **10ms / 0.005ms** | 8101ms / 4.05ms → **12ms / 0.006ms** | <2ms/次 PASS |
| Refined 冷首帧（渲染） | 488（含编码）→ **88.6ms**（+65.9ms 编码） | 313（含编码）→ **119.9ms**（+56.2ms 编码） | <150ms 渲染 PASS |
| Refined 冷首帧（含编码，与旧基线同口径） | 488 → **154.5ms** | 313 → **176.1ms** | <150ms **TARGET** |
| 快照 RenderPng（冷 / 重复最小） | 895 → **434ms**（重复 182ms） | 340 → **193ms**（重复 191ms） | <150ms **TARGET 未达** |
| 源首载（DWG 解析 + working 物化） | 11744 → **6463ms** | 6031 → **2151ms** | —（口径见注） |
| 二开（working + 侧车 census） | 4908 → **4230ms** | 2879 → **2089ms** | 不重读源（PASS） |
| ComputeHash 冷 / 重复 | 524ms / — → **333.5ms / 0.060ms** | 194ms / — → **346.6ms / 0.002ms** | 重复 O(1) PASS |
| 构建 items | 310 → 218ms（54,929） | 938 → 981ms（76,589） | — |
| 索引构建（CanvasViewModel ctor / RenderItemIndex，终审新增单列） | — → **21ms** | — → **12ms** | —（信息项） |
| 托管内存（单文档） | 489 → **494MB** | 342 → **248MB** | — |
| 二开源未动 / 源移走可再载 | — → **True / True**（4729ms） | — → **True / True**（1931ms） | PASS |

注：
- 源首载在 `%TEMP%` 同字节副本上测（标本目录零写入），**OS 文件缓存已暖**，非磁盘冷载口径；
  与基线（冷盘）之差含缓存因素，只作参考。
- Refined「含编码」= 渲染 + PNG 编码（65.9/56.2ms）；旧基线 frame 计时本就含编码，
  故同口径为 154.5/176.1ms：显著优于基线但**超过 150ms**（记为 TARGET，单列于上表）。
- 快照冷首帧 434/193ms，重复最小 182/191ms：对 150ms 目标为 **+21%/+27%**（上一轮 214/198ms =
  +43%/+32%，跨运行波动大，二者均未达）。扣编码后的 116/135ms 是**派生估算**（重复快照减 refined
  编码，非独立实测，不改变判据）；判据仍为冷首帧，**150ms 未达**。快照仍较基线 895/340ms 改善
  约 2.1×/1.8×，瓶颈为软件光栅 55k–77k 基元的描边/轮廓成本。
- **模拟重建**（spec §8 越界/失效重建目标 <50ms 的实测口径）：暖缓存存在时
  `SetQualityForTests(Interactive)` + 缩放令缓存结构失效，下一次渲染走 `RebuildCache(Interactive)`
  + blit；每样本 `BaseRenderCount` 递增（`rebuilt=True`，见 after-real.md 明细），3 次中位
  **77.6/120.4ms → TARGET 未达**。触发为**模拟**（质量档+缩放失效），与生产同一
  `RebuildCache` 例程但非生产交互期真实触发——生产触发点为交互期越界/编辑点击。同状态
  Refined 暖重建 82/138ms，即交互档 LOD 仅省 ~5%/13%：重建成本是软件光栅全量基元
  （约 55k/77k）+ 9.3MB 缓存位图重分配，LOD 降不了线条主体。**<50ms 只在缓存命中/复用路径
  成立**（平移 blit 1.95/2.44ms、交互缩放仿射）。残留见 §12.6。
- 合成 200k：平移交互 **2.04ms**（1.77–3.13）、命中 **0.194ms/次**；模拟重建 200.7ms、
  索引构建 **418ms**（RenderModelBuilder.Build 216ms，终审复测）、Refined 冷 272.7 / 暖
  198.3ms、快照 751ms（重复 535）。对应「合成 200k 交互 <50ms、
  命中 <2ms」PASS；重建/Refined/快照仅记录（spec §7 对合成只设交互/命中目标）。
- **终审复测（2026-11-27，新增 `index build` 单列）**：RenderModelBuilder.Build 240/1126ms
  与索引构建 21/12ms（synth 200k：Build 216ms / 索引 418ms，200k 索引成本显著）；
  模拟重建复测 78.2/108ms（synth 207.1ms），平移 2.43/1.67ms、命中 0.004/0.006ms。
  复测在上一轮验收运行的波动内（判据仍以上表验收运行为准）；完整行见
  `outputs/perf-baseline/after-real.md`、`after-synth.md`。

### 12.3 LOD / 缓存语义边界

- **两阶段**：交互期（滚轮/中键拖动/被消费的工具点击）Quality=Interactive，只变换已烘焙位图
  （缩放按比例仿射），静止 150ms 后 Refined 重建缓存并重绘叠加层；选择点击不算交互（不重绘）。
- **阈值**（Refined / Interactive）：文字 2–6px 简化条、<2px 跳过（交互 4/10px）；微图元
  0.5px→1px（交互 1px）；Hatch 面积 <4px² 或 >2000 点→仅轮廓/bbox（交互 >500 点或 <16px²）；
  折线抽稀 0.5px（交互 1px）。降级是**显示降级**，不改变数据/命中/审计/编辑目标。
- **选中/高亮预算**：投影 bbox ≤ 视口 25% 且 Hatch ≤2000 点且折线 ≤2000 顶点才给全细节，
  否则包围盒/轮廓 +2px 描边，状态栏注记「高亮为包围盒（图元过密）」；叠加层不烘焙进位图。
- **文字缓存**：FormattedText LRU ≤512，键 `(text, fontBucket, bold, colorRgb)`；交互档基本不缓存
  （以简化条/跳过为主）。概览位图的「概览」注记只跟实际呈现档位（复用 Refined 位图时不标）。
- **缓存失效**：Items 引用、控件尺寸、DPI、隐藏层集合、世界原点/ Fit 偏移、Zoom、Diff 开关
  （diff 直绘且绕过缓存）、脱离视觉树即释放；平移仅在 128px 外扩内 blit，越界重建；重建先释放旧位图。
- **快照**：恒 Refined + 共享裁剪/LOD，无交互缓存/叠加层；导出与报告缩略图同源。

### 12.4 R9 空 census 信任（语义调整，非安全边界）

- sidecar v2 增 `sourceSha256/sourceSize/sourceMtimeUtc/verifiedEmpty`；`LoadTrusted` 需
  路径+哈希+size+mtime 全匹配且 census 为空才返回 `verifiedEmpty=true`，`DrawingSession.Load`
  据此**免重读源 DWG**（源缺失亦放行）。不匹配/旧 v1 → 回退 R9 fail-closed 重建。
- **明示**：sidecar 为本地普通文件，任何本地进程可改写；`verifiedEmpty` 只是性能捷径，
  **不是安全边界**（伪造指纹可使 Yellow 源显示 Green）；`Producer` 仅可观测、未签名。
  需强校验时依赖源 DWG 解析（R9）或外部可信存证。
- 已知窗口（不闭合）：Save 的 size/mtime/hash 取自 Save 时刻源文件，census 来自此前 Load。
- **信任旁路可达性（明示）**：源删除后「免重读源」的信任旁路只能经
  `DrawingSession.Load(..., persisted, trustEmptySource: true)` 直接调用到达；`LoadTrusted`
  自身要求 `File.Exists(sourcePath)`（源缺失即返回 Empty + false），CLI/View 的 sidecar
  通路均经 `LoadTrusted` 取信任，故正常路径不可达——此为本意（design.md §7 口径），
  测试中的源删除成功载入即该直接调用。
- **真实路径边界（明示）**：两份真实标本均为 **Yellow**（census 非空，342/7557），其二开走
  working 副本 + 侧车（实测不重读源 DWG，4230/2089ms，`noSourceReread=True`），**不触发**
  verified-empty 分支；verified-empty 的「双开免重读源」仅由合成 DXF / CLI 用例覆盖。
  env-gated 的真实 Yellow→Green 换源用例在门禁中**默认跳过**（Cli 套件 1 例 skip）。

### 12.5 哈希契约

- `IHashRevision { long Revision; void InvalidateHash(); }` 为**可选能力**（`IDrawingSession` 未改）；
  `DrawingSession` 缓存 `(Revision, hash)`，命中即 O(1)（实测重复 0.060/0.002/0.056ms）。
- 失效点：StepEngine 每步成功后、`Reload`/`RestoreState`；`SaveWorking` 不改 content 不失效；
  缓存条目带修订号 + `Volatile` 存取（计算期间被失效不会钉旧值）。
- **契约**：任何绕过 StepEngine 直接改 `Doc` 的调用方**必须**在 `ComputeHash` 前
  `InvalidateHash()`，否则拿到旧值（T6 已披露清单 + 契约用例钉住）。非 `IHashRevision`
  会话（测试 fake）不走缓存，行为不变。哈希投影口径未改。

### 12.6 Racked 边界与残留风险

- **工作副本格式未改**：仍为 `.working.dxf`（DWG 首载物化）。真实图 working DXF 解析仍
  约 4.3/2.0s（二开 2 次打开不重读源 DWG 但 working 解析仍是主成本）；AC1027 working /
  二进制缓存属另立切片。
- **快照 150ms 目标未达**（冷 434/193、重复最小 182/191；跨运行 193–301/182–214 波动）：
  留作后续（减少基元描边/路径合并、或快照按视口瓦片并行）；本轮不改渲染后端。
- **交互期越界/编辑重建 <50ms 未达**（模拟重建实测 77.6/120.4ms，同状态 Refined 暖重建
  82/138ms；触发为质量档+缩放失效的模拟，非生产交互期真实触发）：
  失效/越界重建必须全量软件光栅 55k/77k 基元并重分配 9.3MB 缓存位图，交互档 LOD 只能省
  5%–13%；<50ms 仅适用于缓存命中/复用（平移 blit、交互缩放仿射）。若需重建也 <50ms，
  需另立切片（瓦片化/并行光栅或增量重绘脏区），本切片不改渲染后端。
- **GPU/headless 差异**：验收口径为 headless Skia 软件渲染；真机 GPU 可能更快，亦可能因
  纹理上传更慢——数字不承诺跨机复现（CI 不断言绝对耗时）。
- **测量方差**：同机重复运行 Refined/快照波动可达 ±40%（JIT tiering/GC/进程级预热）；
  本轮区间实测：Refined 冷 88.6–96.2 / 119.9–122.8ms、快照冷 193–301 / 434–483ms、
  托管内存 248–349MB（强电图）。工具对平移帧/失效重建/命中各取 3 次中位、快照重复取 2 次
  最小，单次样本并列于明细（after-*.md）；报告以冷首帧/中位为 PASS/TARGET 判据。
- 索引内存 O(n)（候选格重复登记 + overflow 表）；命中顺序仍为 Items 序首个精确命中，
  LOD 不改变绘制顺序语义。
- 空 census 信任的 sidecar 写入窗口、`Producer` 未签名（见 12.4）。
