using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Vision;

/// <summary>
/// 幅面驱动瓦片（T6）：视觉读图实验实证——可读分辨率→准确，不可读→先验幻觉。
/// 以幅面内文字字高中位数反算单视口可覆盖的世界区域
/// （regionW = viewportW × medianH / targetTextPx，默认目标字高 24px），
/// 产 1 overview（rect=FrameBounds，全局定位）+ N tile rects（语义层中位数锚点+
/// 左右/上下/对角偏移覆盖幅面，上限 6 tiles 防爆炸）。
/// 无文字（或字高/视口参数退化、单视口已覆盖整幅）→单 overview（诚实：无字高依据不切瓦）。
/// 瓦片矩形裁剪到幅面内；渲染经 IRenderSnapshot seam 注入（Data 不引用 View）。
/// </summary>
public static class SheetTiler
{
    /// <summary>瓦片数上限（防爆炸：大幅面/小字高图纸不产生海量瓦片）。</summary>
    public const int MaxTiles = 6;

    // 锚点第一梯队层名关键词：说明/图框/图名/符号/TITLE（图签文字密集区优先作读图锚点）
    private static readonly string[] AnchorLayerKeywords = { "说明", "图框", "图名", "符号", "TITLE" };

    public static IReadOnlyList<(string SheetId, (double MinX, double MinY, double MaxX, double MaxY) Rect)> Tiles(
        SheetRecord sheet, IReadOnlyList<WalkedEntity> walked,
        double targetTextPx = 24, int viewportW = 2800, int viewportH = 1750)
    {
        var frame = sheet.FrameBounds;
        var overview = (sheet.SheetId, frame);
        var result = new List<(string, (double, double, double, double))> { overview };

        double frameW = frame.MaxX - frame.MinX, frameH = frame.MaxY - frame.MinY;
        if (frameW <= 1e-9 || frameH <= 1e-9 || targetTextPx <= 0 || viewportW <= 0 || viewportH <= 0)
            return result; // 退化幅面/参数→单 overview

        var inSheet = walked.Where(w => w.SheetId == sheet.SheetId).ToList();

        // 字高中位数（TextEntity 与 MText 独立分支：MText : Entity，非 TextEntity）
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
        if (heights.Count == 0) return result; // 无文字→单 overview

        double medianH = Median(heights);
        double regionW = viewportW * medianH / targetTextPx;
        double regionH = viewportH * medianH / targetTextPx;
        if (regionW <= 1e-9 || regionH <= 1e-9) return result;
        // 单视口已覆盖整幅（文字足够大）→无需切瓦
        if (regionW >= frameW && regionH >= frameH) return result;

        // 锚点=语义层组世界锚点中位数：第一梯队（说明/图框/符号层）优先，
        // 第二梯队=约定库成类层；都缺省→幅面中心。
        var anchorPoints = inSheet
            .Where(w => AnchorLayerKeywords.Any(k => (w.Entity.Layer?.Name ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(w => (X: w.WorldX, Y: w.WorldY)).ToList();
        if (anchorPoints.Count == 0)
            anchorPoints = inSheet
                .Where(w => ConventionLibrary.ClassForLayer(w.CanonicalLayer) != SemanticClass.Unknown)
                .Select(w => (X: w.WorldX, Y: w.WorldY)).ToList();
        double ax = frame.MinX + frameW / 2, ay = frame.MinY + frameH / 2;
        if (anchorPoints.Count > 0)
        {
            ax = Median(anchorPoints.Select(p => p.X).ToList());
            ay = Median(anchorPoints.Select(p => p.Y).ToList());
            // 锚点夹取到幅面内（中位数理论上在框内，守卫远端残元/数值边界）
            ax = Math.Clamp(ax, frame.MinX, frame.MaxX);
            ay = Math.Clamp(ay, frame.MinY, frame.MaxY);
        }

        // 中心锚+左右/上下/对角偏移（中心瓦优先），裁剪到幅面、去重、上限 6
        var offsets = new (double Dx, double Dy)[]
        {
            (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
            (-1, -1), (1, -1), (-1, 1), (1, 1),
        };
        var seen = new HashSet<(double, double, double, double)>();
        foreach (var (dx, dy) in offsets)
        {
            if (result.Count - 1 >= MaxTiles) break;
            double cx = ax + dx * regionW, cy = ay + dy * regionH;
            var rect = (
                MinX: Math.Max(frame.MinX, cx - regionW / 2),
                MinY: Math.Max(frame.MinY, cy - regionH / 2),
                MaxX: Math.Min(frame.MaxX, cx + regionW / 2),
                MaxY: Math.Min(frame.MaxY, cy + regionH / 2));
            if (rect.MaxX - rect.MinX <= 1e-9 || rect.MaxY - rect.MinY <= 1e-9) continue; // 空瓦
            if (rect == frame) continue; // 与 overview 重复
            if (!seen.Add(rect)) continue; // 裁剪后重合
            result.Add((sheet.SheetId, rect));
        }
        return result;
    }

    /// <summary>中位数（偶数样本取中间两值平均；调用方保证非空）。</summary>
    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
