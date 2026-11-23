using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>房间信息：名称+锚点（房名文本插入点）+区域尺寸；Assumed=无墙体证据的默认区域。</summary>
public sealed record RoomInfo(string Name, double X, double Y, double Width, double Depth, bool Assumed);

/// <summary>
/// 房间识别（3d，启发式保守切片）：房名文本精确匹配模式列表→锚点；
/// 区域=锚点 10m 半径内 Wall 类线（叠加层标注或图层约定）端点 bbox；
/// 无墙体/退化 bbox→6m×6m 默认区域显式 Assumed=true（诚实标注，不假装精确）。
/// Room 标注 GeometryInference 0.6——低于闸口 0.8，须人工 map 确认后放行语义操作。
/// 精确房间多边形重建留后续（spec §12）。
/// </summary>
public static class RoomDetector
{
    private static readonly string[] RoomNames =
    {
        "客厅", "卧室", "主卧", "次卧", "厨房", "卫生间", "餐厅",
        "办公室", "会议室", "走廊", "阳台", "书房", "储物"
    };
    private const double WallSearchRadius = 10000;
    private const double DefaultExtent = 6000;

    public static IReadOnlyList<RoomInfo> Detect(DrawingSession session, SemanticOverlay? overlay)
        => DetectWalked(BlockWalker.Walk(session.Doc, session.Sheets).ToList(), overlay);

    /// <summary>
    /// 块遍历口径核心：墙线端点/房名锚点均用世界坐标；墙约定匹配用归一层名
    /// （bound xref "x$0$WALL"→WALL）；叠加层墙标注按 HandlePath（含嵌套后缀句柄）。
    /// </summary>
    internal static IReadOnlyList<RoomInfo> DetectWalked(IReadOnlyList<WalkedEntity> walked, SemanticOverlay? overlay)
    {
        var wallHandles = overlay?.Annotations
            .Where(a => a.Class == SemanticClass.Wall)
            .Select(a => a.TargetHandle)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var walls = walked
            .Where(w => w.Entity is Line && w.WorldPoints.Count >= 2
                && (wallHandles.Contains(w.HandlePath)
                    || ConventionLibrary.ClassForLayer(w.CanonicalLayer) == SemanticClass.Wall))
            .ToList();

        var rooms = new List<RoomInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 排序保持顶层旧行为（按实体句柄数值序）；嵌套同句柄实体按路径次序稳定排列
        foreach (var w in walked.Where(w => w.Entity is TextEntity)
            .OrderBy(w => w.Entity.Handle).ThenBy(w => w.HandlePath, StringComparer.Ordinal))
        {
            var t = (TextEntity)w.Entity;
            string name = (t.Value ?? "").Trim();
            if (!RoomNames.Contains(name) || !seen.Add(name)) continue;
            var anchor = (w.WorldX, w.WorldY);
            var near = walls.Where(l =>
                Distance(l.WorldPoints[0], anchor) <= WallSearchRadius ||
                Distance(l.WorldPoints[1], anchor) <= WallSearchRadius).ToList();
            if (near.Count == 0)
            {
                rooms.Add(new RoomInfo(name, anchor.WorldX, anchor.WorldY, DefaultExtent, DefaultExtent, Assumed: true));
                continue;
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var l in near)
            {
                foreach (var p in l.WorldPoints)
                {
                    minX = System.Math.Min(minX, p.X); maxX = System.Math.Max(maxX, p.X);
                    minY = System.Math.Min(minY, p.Y); maxY = System.Math.Max(maxY, p.Y);
                }
            }
            double width = maxX - minX, depth = maxY - minY;
            if (width <= 0 || depth <= 0)
                rooms.Add(new RoomInfo(name, anchor.WorldX, anchor.WorldY, DefaultExtent, DefaultExtent, Assumed: true));
            else
                rooms.Add(new RoomInfo(name, anchor.WorldX, anchor.WorldY, width, depth, Assumed: false));
        }
        return rooms;
    }

    /// <summary>房名文本标 Room（GeometryInference 0.6）；已有 ≥0.6 的 Room 标注不降级不重复。</summary>
    public static void AnnotateRooms(DrawingSession session, SemanticOverlay overlay)
        => AnnotateRoomsWalked(BlockWalker.Walk(session.Doc, session.Sheets).ToList(), overlay);

    /// <summary>块遍历口径核心（扫描器复用同一次 Walk）；句柄=HandlePath（含嵌套后缀）。</summary>
    internal static void AnnotateRoomsWalked(IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay)
    {
        foreach (var w in walked)
        {
            if (w.Entity is not TextEntity t) continue;
            string name = (t.Value ?? "").Trim();
            if (!RoomNames.Contains(name)) continue;
            string handle = w.HandlePath;
            if (overlay.Annotations.Any(a => a.TargetHandle == handle
                    && a.Class == SemanticClass.Room && a.Confidence >= 0.6)) continue;
            // T4：stamp 幅面归属（WalkedEntity.SheetId；框外=null legacy 全局）
            overlay.UpsertAnnotation(new SemanticAnnotation(handle, SemanticClass.Room,
                Provenance.GeometryInference, 0.6, $"房名模式: {name}", w.SheetId));
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }
}
