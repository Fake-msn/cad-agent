# 项3 大图性能 Implementation Plan（索引/裁剪 + 两阶段 LOD + 位图缓存 + 加载/哈希）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 真实地下室强电图（55k–77k 展开图元、Hatch 105 万点）上达到：交互帧 <50ms、命中 <2ms/次；Refined 帧/快照 <150ms；Green 图二次打开不重读源 DWG；重复 ComputeHash 降为 O(1)。

**Architecture:** View 层新增空间索引与统一绘制器（CanvasProjection + RenderItemDrawer）；交互期画布只变换上一张缓存位图（保证 <50ms），静止 150ms 后按 LOD 重绘并刷新缓存；命中走索引。Data 层升级 census sidecar（空 census 信任）与 ComputeHash 修订缓存（`IHashRevision` 可选接口，StepEngine 失效）。

**Tech Stack:** C#/.NET 8；Avalonia 11.3.2（headless Skia 验收）；ACadSharp 3.8.35；xUnit。

**Spec:** `docs/superpowers/specs/2026-11-26-item3-large-drawing-performance-design.md`（基线：`outputs/perf-baseline/{baseline,real-baseline}.md`）

## Global Constraints

- 分支：`feat/cad-agent-perf`（自 main af72091）；不合并不 push（逐次授权）。
- Git 身份仅环境变量（逐命令）：`GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`；禁止 `git config`。
- 验收口径：headless Skia 软件渲染；真实标本只读副本 `outputs/perf-baseline/specimens/`（原件不动）；CI 不断言绝对耗时。
- 功能语义不因性能变化：命中/选择/审计/编辑目标/diff/中心线/预览行为保持；LOD 仅视觉降级。
- 选中/高亮同样走裁剪+预算（密集 Hatch 超预算→包围盒/轮廓高亮）。
- 哈希投影口径不变；仅加缓存。
- 测试夹具 `%TEMP%` 清理；SQLite `ClearAllPools()`；Windows GBK 输出重定向 UTF-8；已知 flake 隔离复跑留证。
- 门禁基线：main af72091 全 sln 2173 例 = 2172 pass / 0 fail / 1 designed skip。

## 文件结构

- Create `src/CadAgent.View/Rendering/RenderItemIndex.cs`（bbox 索引/视口查询/命中候选）
- Create `src/CadAgent.View/Rendering/CanvasProjection.cs`（世界↔屏幕快照，含 offset）
- Create `src/CadAgent.View/Rendering/RenderItemDrawer.cs`（从 DrawingCanvas 抽出的基元绘制 + LOD 决策与缓存）
- Modify `src/CadAgent.View/Controls/DrawingCanvas.cs`（裁剪绘制、两阶段、位图缓存、叠加层）
- Modify `src/CadAgent.View/ViewModels/CanvasViewModel.cs`（索引/视口矩形/查询命中；保持 API）
- Modify `src/CadAgent.View/Rendering/RenderSnapshotProvider.cs`（Refined+LOD）
- Create `src/CadAgent.Core/Cad/IHashRevision.cs`
- Modify `src/CadAgent.Data/DrawingSession.cs`（Revision/缓存/空信任加载）
- Modify `src/CadAgent.Data/UnparsedCensus.cs`（sidecar v2）
- Modify `src/CadAgent.Core/StepEngine.cs`（每步成功后失效哈希缓存）
- Modify `src/CadAgent.View/ViewModels/MainViewModel.cs`、`src/CadAgent.View/App.cs`（异步加载/占位）
- Modify `src/CadAgent.View/MainWindow.cs`（LOD 状态注记：概览/包围盒高亮）
- Add `tools/CadAgent.PerfBaseline/`（控制台工具，入 sln）
- Tests：`RenderItemIndexTests`、`CanvasProjectionTests`、`RenderItemDrawerTests`、`DrawingCanvasCacheTests`、`RenderSnapshotPerfTests`、`UnparsedCensusTrustTests`、`MainViewModelAsyncTests`、`HashRevisionCacheTests`；工具冒烟 `PerfBaselineToolTests`（可跳过长测）

---

### Task 1: RenderItemIndex + CanvasViewModel 视口/命中

**Files:**
- Create `src/CadAgent.View/Rendering/RenderItemIndex.cs`
- Modify `src/CadAgent.View/ViewModels/CanvasViewModel.cs`
- Test `tests/CadAgent.View.Tests/RenderItemIndexTests.cs`

**Interfaces:**
```csharp
public sealed class RenderItemIndex
{
    public RenderItemIndex(IReadOnlyList<RenderItem> items, int minCellsPerAxis = 32, int maxCellsPerAxis = 512, int maxCellsPerItem = 64);
    public IReadOnlyList<int> Query((double MinX,double MinY,double MaxX,double MaxY) rect,
        IReadOnlySet<string>? hiddenLayers = null); // Items 索引升序
    public int? FirstHit(double x, double y, double tolerance, IReadOnlySet<string>? hiddenLayers = null);
    public (double MinX,double MinY,double MaxX,double MaxY) BoundsOf(int index);
}
```
- bbox：Line/Polyline/Hatch（跳过 NaN 分隔符）；Circle/Arc 圆心±r；Text 插入点+字盒（宽=len*height*0.6）；空点退化。
- 网格：cell = max(contentW,contentH)/clamp(√n, minCells, maxCells)；按 bbox 登记；覆盖格 > maxCellsPerItem → overflow（总是参与查询）。
- Query：矩形与格子交集 → 候选并集 → 按 bbox 相交过滤 → 隐藏层过滤 → 升序去重。
- FirstHit：bbox 扩 tolerance 的候选，按索引升序调用既有 `Hits` 精确判定（把 `CanvasViewModel.Hits` 改为 `internal static` 供复用）。
- CanvasViewModel：
  ```csharp
  public RenderItemIndex Index { get; }                    // Refresh 时重建
  public (double MinX,double MinY,double MaxX,double MaxY) WorldViewport(); // 屏幕矩形逆变换
  public IReadOnlyList<int> QueryViewport(...);            // 委托 Index
  public int? HitTest(double wx,double wy,double tol);     // 改走 Index.FirstHit（签名不变）
  ```
- 现有 `VisibleItems` 保留；`WorldViewport` 在未 Fit（viewport 0）时返回空/反相矩形由调用方判空。

- [ ] Step 1 失败测试：索引 bbox（各 Kind/NaN Hatch/Text 盒）；小/大/长实体（overflow）查询正确；隐藏层过滤；结果升序；`FirstHit` 与旧线性实现随机 500 点×3 图元集完全一致（含 tolerance/Arc 角度/Text 盒）；`WorldViewport` 与 `ScreenToWorld` 角点互逆。
- [ ] Step 2 红：`dotnet test tests/CadAgent.View.Tests --filter "FullyQualifiedName~RenderItemIndexTests" --nologo`
- [ ] Step 3 实现（索引 + VM 接线；旧 `HitTest` 循环删除，`Y` 翻转注意 WorldViewport 的 min/max 归一）。
- [ ] Step 4 绿 + 全 View.Tests（既有命中/选择测试不变）。
- [ ] Step 5 提交 `feat(view): 渲染图元空间索引与视口/命中查询`

---

### Task 2: CanvasProjection + RenderItemDrawer（裁剪/LOD/画笔缓存）

**Files:**
- Create `src/CadAgent.View/Rendering/CanvasProjection.cs`
- Create `src/CadAgent.View/Rendering/RenderItemDrawer.cs`
- Modify `src/CadAgent.View/Controls/DrawingCanvas.cs`（Render 改用 Drawer；行为暂等价）
- Test `tests/CadAgent.View.Tests/CanvasProjectionTests.cs`、`RenderItemDrawerTests.cs`

**Interfaces:**
```csharp
public sealed record CanvasProjection(double MinX,double MinY,double Scale,double PanX,double PanY,
    double OffsetX,double OffsetY,double ViewportW,double ViewportH,double ExtraOffsetX=0,double ExtraOffsetY=0)
{
    public static CanvasProjection From(CanvasViewModel vm, double extraOffsetX=0, double extraOffsetY=0);
    public (double X,double Y) WorldToScreen(double x,double y);
    public (double X,double Y) ScreenToWorld(double sx,double sy);
    public (double MinX,double MinY,double MaxX,double MaxY) WorldViewport();
}
public enum RenderQuality { Interactive, Refined }
public enum RenderDecision { Full, Simplified, Bbox, Skip }
public sealed class RenderItemDrawer
{
    public void Draw(DrawingContext ctx, CanvasProjection p, IReadOnlyList<RenderItem> items,
        IReadOnlyList<int>? indices, RenderQuality q, PenResolver pens, TextCache? texts,
        RenderItem? selected, IReadOnlySet<string>? highlighted, bool selectionBudgetExceeded = false);
    public static RenderDecision DecideText(double screenFontPx, RenderQuality q); // 2/6, 4/10
    public static RenderDecision DecideHatch(int points, double screenAreaPx2, RenderQuality q); // <4px²||>2000 / >500||<16
    public static RenderDecision DecideTiny(double screenExtentPx, RenderQuality q); // 0.5 / 1.0
    public static IReadOnlyList<int> Simplify(IReadOnlyList<(double X,double Y)> pts, double minScreenStepPx, double scale, out bool closed);
}
```
- `PenResolver`：color→Pen 缓存；dash→(color,thickness,dashSig) 缓存上限 512，超出清空（简单 LRU 替代）。
- `TextCache`：LRU 512，键 `(text, fontBucket=round(px*2)/2, bold, colorRgb)`。
- `Draw` 叠加层：selected/highlighted 命中项最后画（全细节受预算：selectionBudgetExceeded→只画 bbox+2px 描边）；diff 由调用方另行处理。
- DrawingCanvas.Render 改为：背景 → drawer(base+overlay)；diff 分支保留旧路径（暂不重构）。
- 保留预览/中心线叠加代码在 DrawingCanvas。

- [ ] Step 1 失败测试：
  - Projection：`From(vm)` 后 World↔Screen 与 VM 一致；ExtraOffset 平移；WorldViewport 归一。
  - Decide 边界：1.99/2/6.01；3.99/4px²；2000/2001 点；0.49/0.5px；Interactive 阈值全边界。
  - Simplify：折线抽稀保首末与 closed；全部点同屏邻→仅两端。
  - headless 像素：Drawer 直绘与旧 DrawingCanvas 在简单夹具（线/圆/弧/文本/hatch）帧签名一致（容差 2/255）。
- [ ] Step 2 红 → Step 3 实现（从 DrawingCanvas 抽取 DrawItem/DrawArc/pen 缓存；旧私有方法删除）。
- [ ] Step 4 绿 + 全 View.Tests。
- [ ] Step 5 提交 `feat(view): 统一绘制器与 LOD 决策（投影/画笔/文字缓存）`

---

### Task 3: 裁剪绘制 + 两阶段交互 + 位图缓存

**Files:**
- Modify `src/CadAgent.View/Controls/DrawingCanvas.cs`
- Test `tests/CadAgent.View.Tests/DrawingCanvasCacheTests.cs`

**行为：**
- Render 主路径：
  1. 背景；
  2. 若存在有效缓存位图（key 匹配）→ `ctx.DrawImage(cache, destRect)`（当前 Pan 相对缓存锚点的偏移；zoom 变化时按比例缩放旧图）；
  3. 叠加层（选中/高亮/中心线/预览/悬停）实时画（选中/高亮按 Task 2 预算）；
  4. 交互期禁止重绘基元（仅位图变换）；`_interactionUntil=UtcNow+150ms` 由滚轮/中键拖动/编辑工具点击触发；
  5. 空闲计时器（DispatcherTimer 150ms）到点 → 以 Refined 重建缓存位图并 InvalidateVisual；测试 seam `internal void ForceRefinedForTests()`。
- 缓存构造：私有 `CacheLayer : Control`（持有 projection 快照/items/indices/quality/隐藏层/diff=null），尺寸=视口+2×128；`RenderTargetBitmap` 渲染后保存；`_cacheKey = (itemsRef, zoom, size, hiddenHash, dpi, quality)`；不含 Pan（Pan 用偏移复用）。
- 失效：Refresh/Items 引用变化、Zoom、尺寸、DPI、HiddenLayers 集合哈希、Diff 非 null（diff 模式直接走非缓存路径）、ForceRefined 后重建。
- 平移越界（|dx|>128 或 |dy|>128）→ 触发重建。
- 绘制顺序：缓存位图（基元）→ diff（如开）→ 叠加层；裁剪仍 ClipToBounds。
- 状态注记：`internal string? LastRenderNote`（"概览"/"高亮为包围盒（图元过密）"）由 MainWindow 状态栏读取（Task 5 接入）。
- 测试 seam：`internal bool DisableCacheForTests`（直绘对照）、`internal int BaseRenderCount`（缓存重建计数）、`internal void ForceRefinedForTests()`。

- [ ] Step 1 失败测试（headless）：
  - 同状态：缓存 blit 与直绘帧通道差 ≤2/255；
  - pan 32px：不触发重绘（通过计数 seam `BaseRenderCount`），帧位移正确；
  - pan 200px：重绘；
  - Zoom 变化：交互期先缩放旧图（帧非空），ForceRefined 后清晰（像素差变化）；
  - Refresh/HiddenLayers/Diff/尺寸变化：缓存失效重绘；
  - 选中密集 Hatch（>2000 点或 >25% 视口）：LastRenderNote 含「包围盒」，且只画 bbox（像素存在黄色描边于 bbox 边）。
- [ ] Step 2 红（计数 seam 与缓存未实现）→ Step 3 实现。
- [ ] Step 4 绿 + 全 View.Tests；headless 帧测试避免真实 150ms 依赖（用 ForceRefined）。
- [ ] Step 5 提交 `feat(view): 交互期位图缓存与两阶段精绘`

---

### Task 4: 快照/缩略图 Refined+LOD

**Files:**
- Modify `src/CadAgent.View/Rendering/RenderSnapshotProvider.cs`
- Test `tests/CadAgent.View.Tests/RenderSnapshotPerfTests.cs`（功能正确性；耗时不硬断言）

**行为：**
- RenderPng 不再构造 DrawingCanvas；改为 `CanvasProjection` + `RenderItemDrawer`（Refined），直接绘制到 RenderTargetBitmap 的 DrawingContext（通过内部 `SnapshotControl : Control`，其 Render 调 Drawer）。
- 保留裁剪语义（rect==FrameBounds 锚点裁剪 / 瓦片几何相交）；LOD 阈值 Refined（文字/hatch/抽稀）。
- 快照不启用位图交互缓存。

- [ ] Step 1 失败测试：小夹具 PNG 与旧输出在容差内一致（结构存在：非纯背景、主要图元位置抽样）；文字密集夹具（>500 Text）耗时记录日志（不硬断言）；Hatch>2000 点夹具输出存在轮廓。
- [ ] Step 2 红 → Step 3 实现 → Step 4 绿 + View.Tests。
- [ ] Step 5 提交 `feat(view): 快照改走 Refined 绘制器（LOD）`

---

### Task 5: 空 census 信任 + 加载异步 + 状态注记

**Files:**
- Modify `src/CadAgent.Data/UnparsedCensus.cs`（sidecar v2）
- Modify `src/CadAgent.Data/DrawingSession.cs`（Load 重载 trustEmpty）
- Modify `src/CadAgent.Cli/CommandRouter.cs`（LoadWithCensus 使用 LoadTrusted）
- Modify `src/CadAgent.View/ViewModels/MainViewModel.cs`、`src/CadAgent.View/App.cs`、`src/CadAgent.View/MainWindow.cs`
- Test `tests/CadAgent.Data.Tests/UnparsedCensusTrustTests.cs`、`tests/CadAgent.View.Tests/MainViewModelAsyncTests.cs`

**Interfaces：**
- Sidecar v2 字段：`SourcePath, SourceHash, Classes, SourceSize, SourceMtimeUtc, VerifiedEmpty`（旧 JSON 缺失字段→false，安全回退 R9）。
- `UnparsedCensusStore.LoadTrusted(projectDir, sourcePath, out bool verifiedEmpty)`：path/hash/size/mtime 全匹配才算命中；Empty+VerifiedEmpty → verifiedEmpty=true。
- `DrawingSession.Load(path, UnparsedCensus? persisted = null, bool trustEmptySource = false)`：
  - working 分支 persisted 空且 `trustEmptySource` → 直接 Empty（不读源 DWG）；否则现有 R9。
- CLI `LoadWithCensus` 用 LoadTrusted；View `MainViewModel` 同。
- **sidecar 写入规则**：`LoadTrusted` 未信任（verifiedEmpty=false）且源文件存在 → Load 后**总是 Save**（含空 census，写 `VerifiedEmpty = census.TotalCount==0`、SourceSize、SourceMtimeUtc）；已信任空 → 不重写。
- `MainViewModel.LoadAsync(drawingPath, projectDir, skillDirs)`：后台 Load+Build+Sheets；异常→ErrorMessage；同步构造保留。
- `App.OnFrameworkInitializationCompleted`：显示「加载中…」占位窗口 → `await LoadAsync` → 替换为 MainWindow（失败仍进 MainWindow 展示 ErrorMessage）；headless 测试只测 LoadAsync，不测 App 生命周期。
- MainWindow 状态栏：`OnCanvasInvalidated` 追加 `_canvasControl?.LastRenderNote`。

- [ ] Step 1 失败测试：
  - sidecar 信任（具体用例）：①DWG 夹具 + 已验证空 sidecar → **删除源 DWG 后** `Load(path, Empty, trustEmptySource:true)` 仍成功（证明未读源）；同条件 trustEmptySource:false → 抛「无法核验完整性」；②源内容变化（改写一字节/改 mtime）→ LoadTrusted 返回 verifiedEmpty=false，源仍存在时回退 R9 重建；③旧 sidecar（无新字段）→ verifiedEmpty=false。
  - 旧 sidecar 无新字段→不信任（行为=现状）。
  - LoadAsync：真实小 DXF 成功；坏文件 ErrorMessage 不抛；技能目录传递。
- [ ] Step 2 红 → Step 3 实现 → Step 4 绿 + Data/View/Cli 目标套件。
- [ ] Step 5 提交 `feat: 空 census 信任与异步加载（R9 语义扩展）`

---

### Task 6: ComputeHash 修订缓存

**Files:**
- Create `src/CadAgent.Core/Cad/IHashRevision.cs`
- Modify `src/CadAgent.Data/DrawingSession.cs`
- Modify `src/CadAgent.Core/StepEngine.cs`
- Tests `tests/CadAgent.Data.Tests/HashRevisionCacheTests.cs` + 更新直接变更测试（`InvalidateHash()`）

**Interfaces：**
```csharp
public interface IHashRevision { long Revision { get; } void InvalidateHash(); }
```
- DrawingSession：`Revision`（Interlocked）、`_hashCache`；ComputeHash 命中缓存直接返回；InvalidateHash 递增并清。
- Load 初始 Revision=0；SaveWorking 不递增；Reload/RestoreState 递增（内容替换）。
- StepEngine 每步 `result.Ok` 后、`afterHash = session.ComputeHash()` 前：`(session as IHashRevision)?.InvalidateHash()`。
- 所有“直接改 Doc 后立即 ComputeHash”的测试补 `InvalidateHash()`（grep `Doc\.` + `ComputeHash`；披露清单）。

- [ ] Step 1 失败测试：重复 ComputeHash 时间/O(1)（用 Revision/缓存命中 seam，如 `HashComputeCount` internal）；编辑经引擎后哈希变；Reload/Restore 后变；SaveWorking 后同；直接变更未失效→旧值（契约文档），失效后→新值；非 IHashRevision fake 不受影响（StepEngine 测试）。
- [ ] Step 2 红 → Step 3 实现 → Step 4 绿：Data/Core/View/Cli 全量（更新受影响测试）。
- [ ] Step 5 提交 `feat(core,data): ComputeHash 修订缓存（IHashRevision）`

---

### Task 7: 基线工具、门禁与注记

**Files:**
- Add `tools/CadAgent.PerfBaseline/CadAgent.PerfBaseline.csproj` + `Program.cs`
- Add to `CadAgent.sln`
- Modify spec §12（追加闭环注记）

- 工具：合成 20k/80k/200k（线+20% 文字）与 `--drawing <path>`；输出 load/build/cold+repeat hash/snapshot/interactive frame（同一 DrawingCanvas：先渲染一次建缓存，`PanBy(32,0)` 后再渲染并计时=真实 blit 路径）/refined frame（冷启动首帧=Refined）/hit×2000/memory。
- 验收运行（记录，不 hard fail）：真实两图与 200k 合成；目标：交互 <50ms、命中 <2ms、Refined/快照 <150ms、二次打开不重读源。
- 全 sln 门禁；已知 flake 隔离复跑留证。
- spec §12 注记：性能机制、真实数字（改善前后）、LOD/缓存语义边界、R9 空信任调整、哈希契约、racked 边界（工作副本格式、GPU 差异）。
- [ ] 步骤：工具冒烟测试 + 手动基准运行 + 全门禁 + 注记提交 `docs(spec): 项3 大图性能闭环注记`。

---

## 执行记录（执行中追加）
