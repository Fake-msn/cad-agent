namespace CadAgent.View.Rendering;

/// <summary>
/// 渲染图元空间索引（项3 Task 1）：一次性预计算每图元包围盒，均匀网格宽相位查询 +
/// 溢出一律候选表（长线/大 Hatch 跨格过多时不登记格子，避免索引爆炸）。
/// 命中语义与旧 <c>CanvasViewModel.HitTest</c> 完全一致：候选按 Items 序升序，再调用
/// 同一套精确判定 <see cref="Hits"/>；索引只做候选裁剪，不改判定数学。
/// 纯 C#（无 Avalonia 依赖），可 headless 单测。
/// </summary>
public sealed class RenderItemIndex
{
    /// <summary>文本命中盒宽度估算：每字符 0.6×字高（等宽近似，无字形度量）。</summary>
    private const double TextWidthPerHeight = 0.6;

    /// <summary>图元包围盒（无有效坐标的图元 Has=false：不入格、不参与查询）。</summary>
    private readonly struct Box
    {
        public readonly bool Has;
        public readonly double MinX, MinY, MaxX, MaxY;

        public Box(double minX, double minY, double maxX, double maxY)
        {
            Has = true;
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }
    }

    private readonly RenderItem[] _items;
    private readonly Box[] _boxes;
    private readonly int _cellsX, _cellsY;
    private readonly double _gridMinX, _gridMinY, _gridMaxX, _gridMaxY, _cellSize;
    private readonly List<int>?[] _cells;
    private readonly int[] _overflow;

    /// <param name="minCellsPerAxis">期望单轴最少格数（内容退化轴自动降为 1）。</param>
    /// <param name="maxCellsPerAxis">单轴格数上限（cell 由内容尺寸/√n 推出后仍受此夹取）。</param>
    /// <param name="maxCellsPerItem">单图元覆盖格数上限，超过则进溢出表（总是候选）。</param>
    public RenderItemIndex(
        IReadOnlyList<RenderItem> items,
        int minCellsPerAxis = 32,
        int maxCellsPerAxis = 512,
        int maxCellsPerItem = 64)
    {
        _items = items.ToArray();
        _boxes = new Box[_items.Length];
        var overflow = new List<int>();
        int maxCells = Math.Max(1, maxCellsPerItem);

        int contentCount = 0;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < _items.Length; i++)
        {
            if (!TryBox(_items[i], out Box b))
            {
                _boxes[i] = default;
                continue;
            }
            _boxes[i] = b;
            contentCount++;
            if (b.MinX < minX) minX = b.MinX;
            if (b.MinY < minY) minY = b.MinY;
            if (b.MaxX > maxX) maxX = b.MaxX;
            if (b.MaxY > maxY) maxY = b.MaxY;
        }

        if (contentCount == 0)
        {
            _cells = Array.Empty<List<int>?>();
            _overflow = Array.Empty<int>();
            return;
        }

        double width = maxX - minX, height = maxY - minY;
        int lo = Math.Max(1, minCellsPerAxis), hi = Math.Max(lo, maxCellsPerAxis);
        int desired = Math.Clamp((int)Math.Ceiling(Math.Sqrt(_items.Length)), lo, hi);
        // cell = max(w,h)/clamp(√n, lo, hi)：desired≤hi 保证两轴 ceil(内容/cell)≤hi。
        double extent = Math.Max(width, height);
        double cell = extent > 1e-12 ? extent / desired : 1.0;
        if (!double.IsFinite(cell) || cell <= 0) cell = 1.0;

        _gridMinX = minX;
        _gridMinY = minY;
        _gridMaxX = maxX;
        _gridMaxY = maxY;
        _cellSize = cell;
        _cellsX = AxisCells(width, cell, hi);
        _cellsY = AxisCells(height, cell, hi);
        _cells = new List<int>?[_cellsX * _cellsY];

        for (int i = 0; i < _items.Length; i++)
        {
            Box b = _boxes[i];
            if (!b.Has) continue;
            int x0 = CellOfX(b.MinX), x1 = CellOfX(b.MaxX);
            int y0 = CellOfY(b.MinY), y1 = CellOfY(b.MaxY);
            long span = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
            if (span > maxCells)
            {
                overflow.Add(i); // 长实体溢出表：不登记格子，查询时总是候选（再按 bbox 过滤）
                continue;
            }
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    (_cells[cy * _cellsX + cx] ??= new List<int>()).Add(i);
        }

        _overflow = overflow.ToArray();
    }

    /// <summary>
    /// 视口矩形查询：返回包围盒与矩形相交（闭区间）且未被隐藏层过滤的图元索引，
    /// 按 Items 序升序。矩形须已归一（Min≤Max）；反相/NaN 矩形视为空。
    /// </summary>
    public IReadOnlyList<int> Query(
        (double MinX, double MinY, double MaxX, double MaxY) rect,
        IReadOnlySet<string>? hiddenLayers = null)
    {
        if (!IsValidRect(rect) || _items.Length == 0) return Array.Empty<int>();

        var candidates = new List<int>();
        bool gridOverlap = rect.MaxX >= _gridMinX && rect.MinX <= _gridMaxX
            && rect.MaxY >= _gridMinY && rect.MinY <= _gridMaxY;
        if (gridOverlap && _cells.Length > 0)
        {
            int x0 = CellOfX(rect.MinX), x1 = CellOfX(rect.MaxX);
            int y0 = CellOfY(rect.MinY), y1 = CellOfY(rect.MaxY);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                    if (_cells[cy * _cellsX + cx] is { } bucket) candidates.AddRange(bucket);
        }
        candidates.AddRange(_overflow);
        if (candidates.Count == 0) return Array.Empty<int>();

        candidates.Sort();
        var result = new List<int>(candidates.Count);
        int last = -1;
        foreach (int i in candidates)
        {
            if (i == last) continue; // 跨多格重复登记
            last = i;
            if (hiddenLayers is not null && hiddenLayers.Contains(_items[i].Layer)) continue;
            Box b = _boxes[i];
            if (b.MaxX < rect.MinX || b.MinX > rect.MaxX || b.MaxY < rect.MinY || b.MinY > rect.MaxY) continue;
            result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// 命中：bbox 扩 tolerance 的候选按索引升序，调用精确判定 <see cref="Hits"/> 取首个；
    /// 与旧线性 HitTest 完全一致（容差=世界单位，负容差夹 0）。
    /// </summary>
    public int? FirstHit(double x, double y, double tolerance, IReadOnlySet<string>? hiddenLayers = null)
    {
        double tol = Math.Max(0, tolerance);
        if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
        foreach (int i in Query((x - tol, y - tol, x + tol, y + tol), hiddenLayers))
            if (Hits(_items[i], x, y, tol)) return i;
        return null;
    }

    /// <summary>图元包围盒；无有效坐标（空点/无点圆）→ 退化零盒 (0,0,0,0)。</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) BoundsOf(int index)
    {
        Box b = _boxes[index];
        return b.Has ? (b.MinX, b.MinY, b.MaxX, b.MaxY) : (0, 0, 0, 0);
    }

    /// <summary>
    /// 精确命中判定（自旧 CanvasViewModel.Hits 原样迁入，索引与 VM 共用一份数学）：
    /// Line/Polyline 线段距离；Circle/Arc 径向距离（Arc 需角度落在 CCW 扫掠内，端点
    /// 按容差松弛）；Text 轴对齐字盒（宽=字符数×字高×0.6，旋转不参与）；Hatch 不参与命中。
    /// </summary>
    internal static bool Hits(RenderItem item, double x, double y, double tol)
    {
        switch (item.Kind)
        {
            case RenderKind.Line:
                return item.Points.Count >= 2
                    && DistToSegment(x, y, item.Points[0], item.Points[1]) <= tol;

            case RenderKind.Circle:
            {
                if (item.Points.Count < 2) return false;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                return Math.Abs(d - r) <= tol;
            }

            case RenderKind.Arc:
            {
                if (item.Points.Count < 3) return false;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                double dx = x - cx, dy = y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(d - r) > tol) return false;
                double startDeg = item.Points[1].Y;
                double span = NormDeg(item.Points[2].X - startDeg);
                if (span < 1e-9) return true; // start==end 视作整圆（与画布渲染一致）
                double angle = NormDeg(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                double rel = NormDeg(angle - startDeg);
                double slackDeg = r > 1e-12 ? tol / r * 180.0 / Math.PI : 360;
                return rel <= span + slackDeg || rel >= 360 - slackDeg;
            }

            case RenderKind.Polyline:
            {
                if (item.Points.Count == 0) return false;
                if (item.Points.Count == 1)
                    return Dist(x, y, item.Points[0]) <= tol;
                for (int s = 0; s + 1 < item.Points.Count; s++)
                    if (DistToSegment(x, y, item.Points[s], item.Points[s + 1]) <= tol)
                        return true;
                return false;
            }

            case RenderKind.Text:
            {
                if (item.Points.Count < 1) return false;
                (double ix, double iy) = item.Points[0];
                double w = (item.Text?.Length ?? 0) * item.TextHeight * TextWidthPerHeight;
                double h = item.TextHeight;
                return x >= ix - tol && x <= ix + w + tol && y >= iy - tol && y <= iy + h + tol;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 单图元包围盒：Line/Polyline/Hatch 顶点极值（跳过 NaN 路径分隔符）；Circle/Arc
    /// 圆心±半径（弧保守取整圆）；Text 插入点 + 字盒（宽=len×h×0.6，负尺寸归一）。
    /// 无有效坐标 → false（退化，不入索引）。
    /// </summary>
    private static bool TryBox(RenderItem item, out Box box)
    {
        box = default;
        switch (item.Kind)
        {
            case RenderKind.Circle:
            case RenderKind.Arc:
            {
                if (item.Points.Count < 2) return false;
                (double cx, double cy) = item.Points[0];
                double r = Math.Abs(item.Points[1].X);
                if (!IsFinite(cx) || !IsFinite(cy) || !IsFinite(r)) return false;
                box = new Box(cx - r, cy - r, cx + r, cy + r);
                return true;
            }

            case RenderKind.Text:
            {
                if (item.Points.Count < 1) return false;
                (double ix, double iy) = item.Points[0];
                if (!IsFinite(ix) || !IsFinite(iy)) return false;
                double h = item.TextHeight;
                double w = (item.Text?.Length ?? 0) * h * TextWidthPerHeight;
                if (!IsFinite(h) || !IsFinite(w)) { box = new Box(ix, iy, ix, iy); return true; }
                box = new Box(Math.Min(ix, ix + w), Math.Min(iy, iy + h),
                              Math.Max(ix, ix + w), Math.Max(iy, iy + h));
                return true;
            }

            default:
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                bool any = false;
                foreach ((double x, double y) in item.Points)
                {
                    if (double.IsNaN(x) || double.IsNaN(y)) continue; // Hatch 路径分隔符
                    any = true;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
                if (!any) return false;
                box = new Box(minX, minY, maxX, maxY);
                return true;
            }
        }
    }

    private static bool IsValidRect((double MinX, double MinY, double MaxX, double MaxY) rect) =>
        IsFinite(rect.MinX) && IsFinite(rect.MinY) && IsFinite(rect.MaxX) && IsFinite(rect.MaxY)
        && rect.MinX <= rect.MaxX && rect.MinY <= rect.MaxY;

    private static bool IsFinite(double v) => double.IsFinite(v);

    private static int AxisCells(double extent, double cell, int maxCells)
    {
        if (extent <= 0) return 1;
        double c = Math.Ceiling(extent / cell);
        return (int)Math.Clamp(c, 1, maxCells);
    }

    private int CellOfX(double x) =>
        _cellsX <= 1 ? 0 : Math.Clamp((int)Math.Floor((x - _gridMinX) / _cellSize), 0, _cellsX - 1);

    private int CellOfY(double y) =>
        _cellsY <= 1 ? 0 : Math.Clamp((int)Math.Floor((y - _gridMinY) / _cellSize), 0, _cellsY - 1);

    private static double Dist(double x, double y, (double X, double Y) p)
    {
        double dx = x - p.X, dy = y - p.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>点到线段最短距离（投影参数夹在 [0,1]，端点外取端点距离）。</summary>
    private static double DistToSegment(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        double t = len2 <= 1e-24 ? 0 : ((x - a.X) * abx + (y - a.Y) * aby) / len2;
        t = Math.Clamp(t, 0, 1);
        double px = a.X + t * abx, py = a.Y + t * aby;
        double dx = x - px, dy = y - py;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NormDeg(double deg) => ((deg % 360.0) + 360.0) % 360.0;
}
