using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Vision;

/// <summary>
/// 幅面驱动瓦片（tile-coverage 根因修复）：视觉读图实验实证——可读分辨率→准确，不可读→先验幻觉。
/// 真实标本 5/6 瓦为空的三根因修复：
/// ① 微字峰（尺寸/轴号 2.4 世界单位）主导字高中位数→regionW≈幅宽 0.27%：
///    字高间隙分割（distinct 排序相邻倍率≥8 判双峰→上峰计数加权中位数；单峰保持旧中位数口径）；
/// ② 锚点 X/Y 独立中位数合成幻影点（L 形锚簇落空区）：图签瓦改锚层实体真实 bbox 中心；
/// ③ 单锚偏移网格不覆盖大幅面：密度格网选瓦（瓦落内容实体密集格，空格不选）。
/// 以可读字高反算单视口世界区域（regionW = viewportW × readableH / targetTextPx，默认目标字高 24px），
/// 产 1 overview（rect=FrameBounds，全局定位，恒首）+ 图签瓦 + 密度格瓦（10% 骑缝重叠边距，
/// 上限 6 非 overview 瓦防爆炸）。TilingResult 披露覆盖率/区域/字高口径（诚实：不假装全幅已读）。
/// 无文字（或字高/视口参数退化、单视口已覆盖整幅）→单 overview（HeightBasis="none"、覆盖率 0）。
/// 瓦片矩形裁剪到幅面内；渲染经 IRenderSnapshot seam 注入（Data 不引用 View）。
/// </summary>
public static class SheetTiler
{
    /// <summary>瓦片数上限（防爆炸：大幅面/小字高图纸不产生海量瓦片；语义=非 overview 瓦数上限）。</summary>
    public const int MaxTiles = 6;

    // 锚点第一梯队层名关键词：说明/图框/图名/符号/TITLE（图签文字密集区优先作读图锚点）
    private static readonly string[] AnchorLayerKeywords = { "说明", "图框", "图名", "符号", "TITLE" };

    /// <summary>
    /// 切瓦结果：Tiles（overview 恒首）+ ContentCoverage（被≥1 非 overview 瓦覆盖的 inSheet
    /// walked 实体比例 [0,1]，无瓦时 0）+ RegionW/RegionH（可读区域世界尺寸）+
    /// HeightBasis ∈ {"bimodal-upper","median","none"}（字高口径披露）。
    /// </summary>
    public sealed record TilingResult(
        IReadOnlyList<(string SheetId, (double MinX, double MinY, double MaxX, double MaxY) Rect)> Tiles,
        double ContentCoverage, double RegionW, double RegionH, string HeightBasis);

    public static TilingResult Tiles(
        SheetRecord sheet, IReadOnlyList<WalkedEntity> walked,
        double targetTextPx = 24, int viewportW = 2800, int viewportH = 1750)
    {
        var frame = sheet.FrameBounds;
        var overview = (sheet.SheetId, frame);
        var result = new List<(string SheetId, (double MinX, double MinY, double MaxX, double MaxY) Rect)> { overview };

        double frameW = frame.MaxX - frame.MinX, frameH = frame.MaxY - frame.MinY;
        if (frameW <= 1e-9 || frameH <= 1e-9 || targetTextPx <= 0 || viewportW <= 0 || viewportH <= 0)
            return new TilingResult(result, 0, 0, 0, "none"); // 退化幅面/参数→单 overview

        var inSheet = walked.Where(w => w.SheetId == sheet.SheetId).ToList();

        // 字高收集（TextEntity 与 MText 独立分支：MText : Entity，非 TextEntity）
        var heights = new List<double>();
        foreach (var w in inSheet)
        {
            double h = w.Entity switch
            {
                TextEntity t => t.Height,
                MText mt => mt.Height,
                _ => 0,
            };
            if (h > 1e-9 && double.IsFinite(h)) heights.Add(h);
        }
        if (heights.Count == 0) return new TilingResult(result, 0, 0, 0, "none"); // 无文字→单 overview

        // 根因①：间隙分割可读字高（微字峰不得主导；单峰保持旧中位数口径）
        double readableH = ReadableHeight(heights, out bool bimodal);
        double regionW = viewportW * readableH / targetTextPx;
        double regionH = viewportH * readableH / targetTextPx;
        if (regionW <= 1e-9 || regionH <= 1e-9) return new TilingResult(result, 0, 0, 0, "none");
        // 单视口已覆盖整幅（文字足够大）→无需切瓦
        if (regionW >= frameW && regionH >= frameH) return new TilingResult(result, 0, 0, 0, "none");

        // 根因②③：密度格网——瓦落在内容实体密集格，非幻影锚偏移
        double margin = 0.10; // 骑缝重叠边距（spec §12⑥闭合）
        double cellW = regionW, cellH = regionH;
        var cells = new List<(int Cx, int Cy, int Score, (double MinX, double MinY, double MaxX, double MaxY) Rect)>();
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
        (double MinX, double MinY, double MaxX, double MaxY)? titleRect = null;
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
        return new TilingResult(result, coverage, regionW, regionH, bimodal ? "bimodal-upper" : "median");
    }

    /// <summary>可读字高（单参包装，供测试直调）。</summary>
    internal static double ReadableHeight(List<double> heights) => ReadableHeight(heights, out _);

    /// <summary>
    /// 根因①：字高双峰间隙分割——微字（尺寸/轴号）峰不得主导可读 region。
    /// distinct 排序相邻倍率≥8 判双峰（实证 2.7→150=55×；峰内≤1.6×）→上峰计数加权中位数；
    /// 单峰→全集中位数（R4：偶数样本取中间两值平均，旧口径逐字保持）。
    /// bimodal 披露字高口径（Tiles 据此定 HeightBasis）。
    /// </summary>
    internal static double ReadableHeight(List<double> heights, out bool bimodal)
    {
        bimodal = false;
        if (heights.Count == 0) return 0;
        var groups = heights.GroupBy(h => Math.Round(h, 3))
            .OrderBy(g => g.Key).Select(g => (H: g.Key, N: g.Count())).ToList();
        int split = -1; double bestGap = 8; // 倍率≥8 判双峰（取最大间隙）
        for (int i = 1; i < groups.Count; i++)
        {
            double ratio = groups[i].H / groups[i - 1].H;
            if (ratio >= bestGap) { bestGap = ratio; split = i; }
        }
        if (split < 0) return Median(heights); // 单峰：旧中位数口径不变
        bimodal = true;
        return WeightedMedian(groups.Skip(split).ToList());
    }

    /// <summary>上峰计数加权中位数（累计计数过半取该组字高）。</summary>
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

    /// <summary>中位数（偶数样本取中间两值平均；调用方保证非空）。</summary>
    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
