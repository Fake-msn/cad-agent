# 视觉瓦片覆盖率优化（Tile Coverage）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 5/6 空瓦根因（微字峰主导 regionW→显微瓦片；独立轴中位数幻影锚点；单锚偏移网格不覆盖大幅面），改为双峰字高分割+密度格网选瓦+真实 bbox 图签瓦+重叠边距+覆盖率披露。

**Architecture:** 根因三重实证（outputs/api-probe/tile-coverage-out.txt）：①字高双峰（微字 1.5/2.4/2.5/2.7 共 972 vs 标注字 150/250/400/600 共 598），中位数/众数落微字峰→regionW=280 世界单位=幅宽 0.27%；②锚点 X/Y 独立中位数合成幻影点（sheet1 锚邻域实体=0 实证）；③6 瓦×0.27%≈1.6% 覆盖。修复：字高**间隙分割**（相邻distinct字高倍率≥8 判双峰，取上峰计数加权中位数）→regionW≈27.7% 幅宽；**密度格网**（regionW×regionH 格网按内容实体计数 top-N 选格，保证瓦落内容上）；图签瓦=锚层实体**真实 bbox 中心**；瓦片 10% 重叠边距；**覆盖率指标**（内容实体被≥1 瓦覆盖比例，不含 overview）stdout 披露。

**Tech Stack:** .NET 8 / xUnit / ACadSharp 3.8.35 / 既有 SheetTiler/VisionScanMode/IRenderSnapshot 链

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §12（视觉边界⑥瓦片零重叠骑缝闭合+覆盖率诚实披露）

## Global Constraints

- 分支 `feat/cad-agent-sheet-aware` 追加（head a6db64c；PR#20 已开——本切片提交后 push 更新 PR#20，**push 需用户批准**保留 finishing 菜单）。
- 根因修复非症状修补：三根因各有钉住测试（微字峰/幻影锚/覆盖率）。
- 行为保持：单峰字高图纸→中位数口径不变；无文字/退化幅面→单 overview 不变；MaxTiles=6 上限不变；overview 恒首。
- stdout 契约：coverage/regionW/heightBasis 加性字段；无预算 shape pin 测试 sanctioned 更新（含新字段）。
- 中文注释；EOF 换行；git 署名仅环境变量 Fake-msn（绝不改 config）。
- Windows GBK：dotnet 输出重定向 UTF-8 文件再读；单项目或全 sln（MSB1008）。
- TDD 每任务红→绿→提交；真实标本只读（outputs 副本）；云端调用已授权≤6/跑。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Modify `src/CadAgent.Data/Vision/SheetTiler.cs` | 间隙分割字高+密度格网+真实bbox图签瓦+重叠+TilingResult | 1 |
| Modify `tests/CadAgent.Data.Tests/SheetTilerTests.cs` | 三根因钉住+覆盖率+重叠+上限+退化保持 | 1 |
| Modify `src/CadAgent.View/VisionScanMode.cs` | 消费 TilingResult+stdout coverage/regionW/heightBasis | 2 |
| Modify `tests/CadAgent.View.Tests/VisionScanModeTests.cs` | shape pin 更新+coverage 断言 | 2 |
| Modify spec §12 + 证据 + 门禁 | 收尾 | 3 |

---

### Task 1: SheetTiler 根因修复

**Files:**
- Modify: `src/CadAgent.Data/Vision/SheetTiler.cs`
- Modify: `tests/CadAgent.Data.Tests/SheetTilerTests.cs`

**Interfaces:**
- Consumes: `SheetRecord`(SheetId/FrameBounds/LayoutName)；`WalkedEntity`(Entity/WorldX/WorldY/CanonicalLayer/SheetId)；`ConventionLibrary.ClassForLayer`
- Produces:
  - `public sealed record TilingResult(IReadOnlyList<(string SheetId, (double MinX,double MinY,double MaxX,double MaxY) Rect)> Tiles, double ContentCoverage, double RegionW, double RegionH, string HeightBasis);`
  - `public static TilingResult Tiles(SheetRecord sheet, IReadOnlyList<WalkedEntity> walked, double targetTextPx = 24, int viewportW = 2800, int viewportH = 1750)`（**签名返回类型变更**：旧 IReadOnlyList 返回→TilingResult；消费方 T2 同步；旧测试同步 sanctioned）
  - HeightBasis ∈ {"bimodal-upper", "median", "none"}；ContentCoverage=被≥1 瓦（不含 overview）覆盖的 inSheet walked 实体比例 [0,1]；无瓦时 0
  - 内部：`internal static double ReadableHeight(List<double> heights)`（间隙分割：distinct 排序相邻倍率≥8 判双峰→上峰计数加权中位数；否则全集中位数）；`internal static ... DensityCells(...)` 可 internal 供测试

- [ ] **Step 1: 写失败测试（三根因钉住）**

```csharp
[Fact]
public void ReadableHeight_BimodalMicroAndAnnotation_PicksUpperCluster()
{
    // 微字峰 972 个（1.5/2.4/2.5/2.7）+ 标注峰 598 个（150/250/400/600）——真实标本分布口径
    var heights = new List<double>();
    heights.AddRange(Enumerable.Repeat(2.4, 477));
    heights.AddRange(Enumerable.Repeat(1.5, 228));
    heights.AddRange(Enumerable.Repeat(2.5, 207));
    heights.AddRange(Enumerable.Repeat(2.7, 60));
    heights.AddRange(Enumerable.Repeat(150, 206));
    heights.AddRange(Enumerable.Repeat(250, 255));
    heights.AddRange(Enumerable.Repeat(400, 82));
    heights.AddRange(Enumerable.Repeat(600, 55));
    double h = SheetTiler.ReadableHeight(heights);
    Assert.Equal(250, h, 3); // 上峰加权中位数（非 2.4 微字峰）
}

[Fact]
public void ReadableHeight_Unimodal_KeepsMedian()
{
    var heights = Enumerable.Repeat(350.0, 100).ToList();
    Assert.Equal(350, SheetTiler.ReadableHeight(heights), 3);
}

[Fact]
public void Tiles_ContentClusterAwayFromAnchor_TilesCoverContent()
{
    // 幻影锚回归：锚层（说明）点集中右侧；内容实体集中左侧；
    // 旧算法独立轴中位数+偏移→左内容无瓦；新算法密度格网→左内容格入选
    // 夹具：frame (0,0)-(100000,60000)；内容 500 点聚 (10000..30000, 20000..40000)；
    // 说明层 20 点聚 (80000..90000, 50000..55000)；字高双峰（内容文字 250、微字 2.4 散布）
    var result = SheetTiler.Tiles(sheet, walked);
    var tiles = result.Tiles.Skip(1).ToList(); // 除 overview
    Assert.Contains(tiles, t => t.Rect.MinX <= 15000 && t.Rect.MaxX >= 25000); // 左内容被瓦覆盖
    Assert.True(result.ContentCoverage > 0.5);
}

[Fact]
public void Tiles_CoverageMetric_ExcludesOverview()
{
    // 单 overview（无文字）→ ContentCoverage==0（overview 不计覆盖）
    var result = SheetTiler.Tiles(sheetNoText, walked);
    Assert.Single(result.Tiles);
    Assert.Equal(0, result.ContentCoverage);
    Assert.Equal("none", result.HeightBasis);
}

[Fact]
public void Tiles_OverlapMargin_TenPercent()
{
    // 瓦矩形较 regionW 扩 10%（夹取幅面内）：相邻瓦重叠>0
    var result = SheetTiler.Tiles(sheet, walked);
    // 断言存在两瓦相交面积>0（骑缝闭合）
}

[Fact]
public void Tiles_TitleTile_RealBboxCenter_NotPhantomMedian()
{
    // 锚层点 L 形分布（独立轴中位数落空区）；图签瓦中心=锚层真实 bbox 中心（落在锚点簇内）
    // 断言图签瓦内含≥1 锚层实体
}

[Fact]
public void Tiles_MaxTilesCap_And_OverviewFirst_Kept()
{
    var result = SheetTiler.Tiles(sheet, walked);
    Assert.True(result.Tiles.Count - 1 <= SheetTiler.MaxTiles);
    Assert.Equal(sheet.FrameBounds, result.Tiles[0].Rect);
}
```

（夹具构造镜像 SheetTilerTests 既有模式：SheetRecord 直构+WalkedEntity 直构（Entity=TextEntity/Line 设 Layer/Height+WorldX/Y）；读现状测试文件夹具助手复用。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter SheetTiler`
Expected: 编译失败（TilingResult/ReadableHeight 不存在）+ 旧返回类型消费点红

- [ ] **Step 3: 实现 SheetTiler 重构**

核心算法（替换锚点+offset 段）：

```csharp
    // 根因①：字高双峰间隙分割——微字（尺寸/轴号）峰不得主导可读 region
    internal static double ReadableHeight(List<double> heights)
    {
        var groups = heights.GroupBy(h => Math.Round(h, 3))
            .OrderBy(g => g.Key).Select(g => (H: g.Key, N: g.Count())).ToList();
        if (groups.Count == 0) return 0;
        int split = -1; double bestGap = 8; // 倍率≥8 判双峰（实证 2.7→150=55×；峰内≤1.6×）
        for (int i = 1; i < groups.Count; i++)
        {
            double ratio = groups[i].H / groups[i - 1].H;
            if (ratio >= bestGap) { bestGap = ratio; split = i; }
        }
        var upper = split >= 0 ? groups.Skip(split).ToList() : groups;
        return WeightedMedian(upper);
    }

    private static double WeightedMedian(List<(double H, int N)> groups)
    {
        int total = groups.Sum(g => g.N);
        int half = total / 2;
        int acc = 0;
        foreach (var g in groups)
        {
            acc += g.N;
            if (acc > half) return g.H;
        }
        return groups[^1].H;
    }
```

密度格网选瓦（替换 offsets 循环）：

```csharp
        // 根因②③：密度格网——瓦落在内容实体密集格，非幻影锚偏移
        var inSheet = walked.Where(w => w.SheetId == sheet.SheetId).ToList();
        double margin = 0.10; // 骑缝重叠边距（spec §12⑥闭合）
        double cellW = regionW, cellH = regionH;
        var cells = new List<(int Cx, int Cy, int Score, (double,double,double,double) Rect)>();
        int nx = Math.Max(1, (int)Math.Ceiling(frameW / cellW));
        int ny = Math.Max(1, (int)Math.Ceiling(frameH / cellH));
        for (int cx = 0; cx < nx; cx++)
        for (int cy = 0; cy < ny; cy++)
        {
            double minX = frame.MinX + cx * cellW, minY = frame.MinY + cy * cellH;
            double maxX = Math.Min(frame.MaxX, minX + cellW), maxY = Math.Min(frame.MaxY, minY + cellH);
            int score = inSheet.Count(w => w.WorldX >= minX && w.WorldX < maxX && w.WorldY >= minY && w.WorldY < maxY);
            if (score == 0) continue; // 空格不选（根因：空瓦）
            // 重叠边距扩 rect（夹取幅面）
            double ex = (maxX - minX) * margin, ey = (maxY - minY) * margin;
            var rect = (MinX: Math.Max(frame.MinX, minX - ex), MinY: Math.Max(frame.MinY, minY - ey),
                        MaxX: Math.Min(frame.MaxX, maxX + ex), MaxY: Math.Min(frame.MaxY, maxY + ey));
            cells.Add((cx, cy, score, rect));
        }
        // 图签瓦：锚层实体真实 bbox 中心（根因②幻影锚修复），含锚实体才产
        var anchorEnts = inSheet.Where(w => AnchorLayerKeywords.Any(k => (w.Entity.Layer?.Name ?? "").Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
        (double,double,double,double)? titleRect = null;
        if (anchorEnts.Count > 0)
        {
            double ax0 = anchorEnts.Min(w => w.WorldX), ax1 = anchorEnts.Max(w => w.WorldX);
            double ay0 = anchorEnts.Min(w => w.WorldY), ay1 = anchorEnts.Max(w => w.WorldY);
            double tcx = (ax0 + ax1) / 2, tcy = (ay0 + ay1) / 2;
            titleRect = (MinX: Math.Max(frame.MinX, tcx - regionW / 2), MinY: Math.Max(frame.MinY, tcy - regionH / 2),
                         MaxX: Math.Min(frame.MaxX, tcx + regionW / 2), MaxY: Math.Min(frame.MaxY, tcy + regionH / 2));
        }
        var seen = new HashSet<(double, double, double, double)>();
        if (titleRect is { } tr && tr.MaxX - tr.MinX > 1e-9 && tr.MaxY - tr.MinY > 1e-9 && tr != frame && seen.Add(tr))
            result.Add((sheet.SheetId, tr));
        foreach (var cell in cells.OrderByDescending(c => c.Score))
        {
            if (result.Count - 1 >= MaxTiles) break;
            if (cell.Rect == frame || !seen.Add(cell.Rect)) continue;
            result.Add((sheet.SheetId, cell.Rect));
        }
        // 覆盖率（不含 overview）：被≥1 瓦覆盖的 inSheet 实体比例
        var tileRects = result.Skip(1).Select(t => t.Item2).ToList();
        int covered = inSheet.Count(w => tileRects.Any(r => w.WorldX >= r.MinX && w.WorldX <= r.MaxX && w.WorldY >= r.MinY && w.WorldY <= r.MaxY));
        double coverage = inSheet.Count == 0 ? 0 : (double)covered / inSheet.Count;
        return new TilingResult(result, coverage, regionW, regionH, split >= 0 ? "bimodal-upper" : "median");
```

（无文字/退化早返路径返回 `new TilingResult(result, 0, 0, 0, "none")`；heights 计算改 ReadableHeight；旧 Median 助手保留供 ReadableHeight 单峰路径或删若 unused。）

- [ ] **Step 4: 运行确认绿 + Data 全量**

Run: `dotnet test tests/CadAgent.Data.Tests`
Expected: PASS（旧 SheetTiler 测试同步 TilingResult 返回类型 sanctioned；VisionScanMode 编译红留 T2——若 Data 全量因 View 编译失败则先跑 Data 单项目成功即可，View 红 T2 修）

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Data/Vision/SheetTiler.cs tests/CadAgent.Data.Tests/SheetTilerTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "fix: tile coverage root causes (bimodal height split + density cells + real-bbox title tile + overlap + coverage metric)"
```

---

### Task 2: VisionScanMode 消费 TilingResult + stdout 披露

**Files:**
- Modify: `src/CadAgent.View/VisionScanMode.cs:202,250-259`
- Modify: `tests/CadAgent.View.Tests/VisionScanModeTests.cs`（shape pin sanctioned 更新+coverage 断言）

**Interfaces:**
- Consumes: T1 `TilingResult`(Tiles/ContentCoverage/RegionW/RegionH/HeightBasis)
- Produces: stdout JSON 加性字段 `coverage`（0-1 双精度）/`regionW`/`heightBasis`（两 shape 均含）；--max-tiles 截断作用于 TilingResult.Tiles（overview 恒保留）；llm-call/审计口径不变

- [ ] **Step 1: 写失败测试**

```csharp
[AvaloniaFact]
public async Task VisionScan_StdoutIncludesCoverageAndBasis()
{
    // fake client + 双峰字高夹具 → stdout JSON 含 coverage>0 + heightBasis=="bimodal-upper" + regionW>0
}
[AvaloniaFact]
public async Task VisionScan_NoMaxTiles_ShapeIncludesCoverage()
{
    // 旧 shape pin 更新：无预算 shape 现含 coverage/regionW/heightBasis（sanctioned 契约演进）
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.View.Tests --filter VisionScan_StdoutIncludesCoverage`
Expected: FAIL（字段缺/编译红）

- [ ] **Step 3: 实现消费+披露**

:202 `var tiling = doc.Sheets.Select(s => SheetTiler.Tiles(s, walked)).ToList(); var tiles = tiling.SelectMany(t => t.Tiles).ToList();`；coverage 聚合=按 sheet 加权（inSheet 实体数权重）或简单平均——取**实体数加权**（诚实口径）：`coverage = sum(t.ContentCoverage * sheetCount) / sum(sheetCount)`（sheetCount=该幅 inSheet walked 数；0 权跳过）；regionW/heightBasis 取首幅非 none 值（多幅异 basis 时取首非 none，注释说明）；两匿名 shape 加 `coverage, regionW, heightBasis`；--max-tiles 截断 tiles 时 overview 保留口径读现状保持。

- [ ] **Step 4: 运行确认绿 + View/Cli 全量**

Run: `dotnet test tests/CadAgent.View.Tests` + `tests/CadAgent.Cli.Tests`
Expected: PASS（relay/E2E shape 断言同步 sanctioned）

- [ ] **Step 5: 提交** `feat: vision-scan stdout coverage/regionW/heightBasis disclosure (TilingResult consumption)`

---

### Task 3: 真实标本覆盖率实证 + spec 闭环 + 门禁

**Files:**
- 证据: `outputs/tile-coverage/`（before/after tiles+coverage+claims/seeded 对照）
- Modify: spec §12（⑥骑缝闭合+覆盖率披露+根因三重实证注记）
- Test: skip-gated 真实覆盖率断言（1#配套用房 coverage>0.5 且空瓦数≤1）

- [ ] **Step 1: skip-gated 真实测试**

```csharp
[SkippableFact]
public void RealSpecimen_TileCoverage_ContentCovered()
{
    // 1#配套用房0928 副本：SheetTiler.Tiles 首幅 → ContentCoverage>0.5 + 空瓦（内容实体=0）数≤1
    // before 基线：coverage≈0.003（6 瓦×0.27%）——after 断言>0.5
}
```

- [ ] **Step 2: 真实云端对照实战**（bash，证据 outputs/tile-coverage/）：同标本 --vision --confirm-egress --max-tiles 6 重跑→stdout coverage/claims/seeded/ambiguous 与 before（claims14/seeded0/ambiguous14）对照存档；GT 抽查新增 claims 吻合/拦截记录；audit verify 绿；密钥 grep 零命中。
- [ ] **Step 3: spec §12 注记**（根因三重实证+修复+覆盖率披露+⑥闭合；剩余：设备类种子实证待可读瓦命中后复核）+ 提交。
- [ ] **Step 4: 门禁连绿两次** `dotnet test CadAgent.sln`（ZWCAD 真机前后 tasklist；日志 outputs/tile-coverage/gate{1,2}.log；精确数字）。
- [ ] **Step 5: 提交（docs/test:）；push 更新 PR#20 保留用户批准**；report+finishing 菜单。

---

## Self-Review 记录

- **Spec 覆盖**：§12⑥骑缝闭合（10% 重叠）+覆盖率诚实披露；根因三重实证入注记；设备种子实证留 T3 复核。
- **占位符扫描**：夹具"镜像既有"指向 SheetTilerTests 现状助手；WeightedMedian/密度格网/图签瓦完整代码给出；无 TBD。
- **类型一致性**：TilingResult 五字段 T1 定义 T2 消费 T3 断言；Tiles 返回类型变更消费点（VisionScanMode:202+测试）T1/T2 同步 sanctioned。
- **已知取舍**：间隙倍率阈 8（实证 55× vs 峰内 1.6×，裕度充分）；密度格网 score 用全部 inSheet 实体（含微字——微字密集区也是内容区，合理）；coverage 加权口径实体数；多幅 basis 取首非 none（注释说明）。

---

## 执行记录（SDD 2026-11-23）

- T1 SheetTiler 根因修复：4982fe2 + fix 8a7bb00（R5 单遍分桶 O(N+K)+long 溢出修复）；Data 318/318。
- T2 stdout 披露：d9e9083；View 292/292、Cli 210/210（真实 E2E 实跑）。
- T3 真实实证+闭环：925ebcf+8946072；首幅 coverage 0.003→1.0、12 幅加权 0.741074；claims 14→77（20 条幻觉全拦截，含凭空"君悦新居"）；seeded 0→0（设备种子实证保持开放）；幅面4/6 纯微字详图 coverage≈0.09 含 1 空瓦（诚实披露）；云端总成本 3 跑×6 瓦=18 瓦；审计 8 条验链绿；密钥 grep 零命中；标本 SHA-256 前后一致。
- 门禁：全 sln 1082/1082 连绿两次（gate1/gate2.log，0 失败 0 跳过，无 ZWCAD 残留）。
- 最终全切片审查：0 Critical/Important；修复波 145aba6（计划文档 EOF 换行）；scoped 复审 ADDRESSED。
- Rulings：R1 T1→T2 间 View 编译红 sanctioned（顺序任务，门禁只在 T3）；R2 ReadableHeight out bimodal 重载（计划 split 越界笔误）；R3 夹具按注释规格直构 sanctioned；R4 单峰路径旧 Median 口径逐字保持（WeightedMedian 仅双峰上峰）；R5 密度评分单遍分桶+long（plan-mandated Important 本环修复，语义保持）；R6 最终修复波仅 EOF 换行，none 三态混同/cx clamp/锚层关键词 internal 化 park 后续。
- 遗留 minors（整支 triage 可留）：倍率=8 边界/空表直测、半开 vs inclusive 口径、浮点归桶压线差一格（10% 扩边兜底）、long.MaxValue 极端、none-跳过聚合夹具、锚层关键词测试镜像、存量 CS8602/CS8604 警告、弱子串断言。
- spec §12：⑥骑缝闭合+覆盖率披露注记（8946072）；④设备种子/⑦多幅预算分配保持开放。
