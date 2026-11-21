using CadAgent.View.Rendering;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 事务对比叠加层（计划4 T6）：快照 before 图元 + 增/删句柄集。
/// 画布渲染分支：before 灰幽灵；removed 红 + 删除线；after 正常，added 绿
/// （added 绿优先于选中黄——对比模式下差异可视化优先）。纯数据，无 Avalonia 依赖。
/// </summary>
public sealed class DiffOverlay
{
    public DiffOverlay(IReadOnlyList<RenderItem> beforeItems,
        IReadOnlyList<string> addedHandles, IReadOnlyList<string> removedHandles)
    {
        BeforeItems = beforeItems;
        AddedHandles = new HashSet<string>(addedHandles.Where(h => h.Length > 0), StringComparer.Ordinal);
        RemovedHandles = new HashSet<string>(removedHandles.Where(h => h.Length > 0), StringComparer.Ordinal);
    }

    /// <summary>快照（事务前）渲染图元——只读展示，不进入 Items/命中/选择。</summary>
    public IReadOnlyList<RenderItem> BeforeItems { get; }

    public IReadOnlyCollection<string> AddedHandles { get; }
    public IReadOnlyCollection<string> RemovedHandles { get; }
}

/// <summary>
/// 画布视图模型（计划4 T3）：纯 C#，无 Avalonia 依赖，可单测。
/// 变换约定（brief）：
/// screenX = (worldX-minX)*baseScale*Zoom + panX + offsetX；
/// screenY = vpH - ((worldY-minY)*baseScale*Zoom + panY) - offsetY（CAD Y-up → 屏幕 Y-down 翻转）。
/// Fit：baseScale = min(vpW/宽, vpH/高) * 0.9（留白），内容居中；Zoom=1 即 fit 视图。
/// ZoomAt：光标下的世界点在缩放前后保持不动（锚点不变式，调整 Pan 实现）。
/// PanBy 接收屏幕空间增量（+dy 内容向下移动）；Pan 本身存储未翻转的内容偏移。
/// HitTest 容差为世界单位；只命中可见图层，返回 Items 原始索引（首个命中）。
/// 选择（T4）：SelectedHandle 为选中图元的真实 DXF 句柄字符串（RenderItem.Handle，
/// Insert 展开子图元为 "{insertHandle}:{childIndex}"），画布按句柄匹配高亮。
/// 问题定位（T5）：SelectHandles 多句柄高亮 + CenterOn 居中（保持 Zoom）；
/// Select（用户点击）清空定位高亮集。
/// </summary>
public sealed class CanvasViewModel
{
    /// <summary>Fit 留白系数（brief：0.9）。</summary>
    private const double FitMargin = 0.9;

    /// <summary>缩放上下限，防止浮点退化/溢出。</summary>
    private const double MinZoom = 1e-6, MaxZoom = 1e6;

    /// <summary>文本命中盒宽度估算：每字符 0.6×字高（等宽近似，无字形度量）。</summary>
    private const double TextWidthPerHeight = 0.6;

    private double _baseScale = 1;
    private double _minX, _minY;
    private double _viewportW, _viewportH;
    private double _offsetX, _offsetY;
    private readonly HashSet<string> _highlightedHandles = new(StringComparer.Ordinal);

    public CanvasViewModel(IReadOnlyList<RenderItem> items) => Items = items;

    /// <summary>渲染图元（T2 RenderModelBuilder 产物，绘制坐标系）。T9 Refresh 可替换。</summary>
    public IReadOnlyList<RenderItem> Items { get; private set; }

    /// <summary>
    /// 编辑后重建（T9）：替换渲染图元（StepEngine 事务提交/撤回后由 UI 调
    /// RenderModelBuilder.Build 重建）。索引选择/定位高亮/对比叠加全部失效清除
    /// （句柄集可能已不存在；diff 与编辑互斥，编辑后不得残留旧对比）。触发一次重绘。
    /// </summary>
    public void Refresh(IReadOnlyList<RenderItem> items)
    {
        Items = items;
        SelectedIndex = null;
        _highlightedHandles.Clear();
        DiffOverlay = null;
        RenderInvalidated?.Invoke();
    }

    /// <summary>绘制预览锚点（T9 Line 工具第一击）；null=无待定点。画布画十字标记。</summary>
    public (double X, double Y)? PendingPoint { get; private set; }

    /// <summary>设置/清除绘制预览锚点，触发一次重绘。</summary>
    public void SetPendingPoint((double X, double Y)? point)
    {
        PendingPoint = point;
        RenderInvalidated?.Invoke();
    }

    /// <summary>
    /// 工具点击拦截（T9）：MainWindow 按 ActiveTool 路由画布左击（世界坐标）。
    /// 返回 true=已消费（跳过默认选择）；false/null=走默认 HitTest→Select。
    /// </summary>
    public Func<double, double, bool>? ToolClickHandler { get; set; }

    /// <summary>隐藏图层集合（大小写不敏感，CAD 图层名约定）。直接增删后调用 <see cref="Invalidate"/>。</summary>
    public HashSet<string> HiddenLayers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>相对 fit 视图的缩放倍率（1=fit）。</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>内容偏移（未翻转屏幕空间：Pan.Y 增大 → 内容向上移动）。</summary>
    public (double X, double Y) Pan { get; private set; }

    /// <summary>有效世界→屏幕缩放：baseScale*Zoom（画布线宽/字高/命中容差换算用）。</summary>
    public double Scale => _baseScale * Zoom;

    /// <summary>选中图元索引；null=无选择。</summary>
    public int? SelectedIndex { get; private set; }

    /// <summary>选中图元的真实 DXF 句柄（RenderItem.Handle）；null=无选择。</summary>
    public string? SelectedHandle => SelectedItem?.Handle;

    /// <summary>选中图元（画布按 Handle 匹配高亮）。</summary>
    public RenderItem? SelectedItem => SelectedIndex is int i && i >= 0 && i < Items.Count ? Items[i] : null;

    /// <summary>
    /// 问题定位高亮句柄集（T5）：SelectHandles 设置，画布对命中的图元同样施加
    /// 选中高亮；用户点击画布（Select）接管选择时清空。空句柄不参与匹配。
    /// </summary>
    public IReadOnlyCollection<string> HighlightedHandles => _highlightedHandles;

    /// <summary>过滤 HiddenLayers 后的可见图元。</summary>
    public IEnumerable<RenderItem> VisibleItems => Items.Where(i => !HiddenLayers.Contains(i.Layer));

    /// <summary>
    /// 事务对比叠加层（T6）：null=关闭。SetDiffOverlay 设置并触发重绘；
    /// 只读回放——不改变 Items/选择/视图变换。
    /// </summary>
    public DiffOverlay? DiffOverlay { get; private set; }

    /// <summary>设置/清除对比叠加层（T6），触发一次重绘。</summary>
    public void SetDiffOverlay(DiffOverlay? overlay)
    {
        DiffOverlay = overlay;
        RenderInvalidated?.Invoke();
    }

    /// <summary>视图状态变化（Fit/ZoomAt/PanBy/Select/Invalidate）→ 画布 InvalidateVisual。</summary>
    public event Action? RenderInvalidated;

    /// <summary>
    /// 适配视口：baseScale = min(vpW/宽, vpH/高)*0.9，内容居中，Zoom/Pan 复位。
    /// 零宽/零高退化：该轴不参与 min；两轴皆退化（点）→ baseScale=1。
    /// 非正视口：baseScale=1、偏移 0（等待有效尺寸）。
    /// </summary>
    public void Fit((double MinX, double MinY, double MaxX, double MaxY) bounds, double viewportW, double viewportH)
    {
        double width = bounds.MaxX - bounds.MinX;
        double height = bounds.MaxY - bounds.MinY;
        _minX = bounds.MinX;
        _minY = bounds.MinY;
        _viewportW = viewportW;
        _viewportH = viewportH;

        if (viewportW <= 0 || viewportH <= 0)
        {
            _baseScale = 1;
            _offsetX = _offsetY = 0;
        }
        else
        {
            double sx = width > 1e-12 ? viewportW / width : double.PositiveInfinity;
            double sy = height > 1e-12 ? viewportH / height : double.PositiveInfinity;
            double s = Math.Min(sx, sy);
            _baseScale = double.IsFinite(s) ? s * FitMargin : 1;
            _offsetX = (viewportW - width * _baseScale) / 2;
            _offsetY = (viewportH - height * _baseScale) / 2;
        }

        Zoom = 1;
        Pan = (0, 0);
        RenderInvalidated?.Invoke();
    }

    /// <summary>
    /// 以屏幕点 (screenX, screenY) 为锚缩放：锚点下的世界点保持不动。
    /// factor 非正/非有限忽略；Zoom 夹在 [1e-6, 1e6]，按实际生效倍率调整 Pan。
    /// </summary>
    public void ZoomAt(double factor, double screenX, double screenY)
    {
        if (factor <= 0 || !double.IsFinite(factor)) return;
        double newZoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        double f = newZoom / Zoom;
        // 锚点不变式推导：s' = s*f 时 pan' = anchor - (anchor - pan)*f，
        // X 锚 = screenX - offsetX；Y 锚 = vpH - screenY - offsetY（翻转后）。
        double ax = screenX - _offsetX;
        double ay = _viewportH - screenY - _offsetY;
        Pan = (ax - (ax - Pan.X) * f, ay - (ay - Pan.Y) * f);
        Zoom = newZoom;
        RenderInvalidated?.Invoke();
    }

    /// <summary>屏幕空间平移：+dx 内容右移，+dy 内容下移（内部对 Y 取反存入 Pan）。</summary>
    public void PanBy(double dx, double dy)
    {
        Pan = (Pan.X + dx, Pan.Y - dy);
        RenderInvalidated?.Invoke();
    }

    /// <summary>设置选中索引；null/越界 → 清除选择。用户接管选择 → 清空问题定位高亮集（T5）。</summary>
    public void Select(int? index)
    {
        SelectedIndex = index is int i && i >= 0 && i < Items.Count ? i : null;
        _highlightedHandles.Clear();
        RenderInvalidated?.Invoke();
    }

    /// <summary>
    /// 问题定位多选高亮（T5）：替换高亮句柄集；SelectedIndex 指向 Items 中首个
    /// 句柄命中的图元（状态栏联动），无命中 → null。空句柄不参与匹配。触发一次重绘。
    /// </summary>
    public void SelectHandles(IEnumerable<string> handles)
    {
        _highlightedHandles.Clear();
        foreach (string h in handles)
            if (h.Length > 0) _highlightedHandles.Add(h);
        SelectedIndex = null;
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Handle.Length > 0 && _highlightedHandles.Contains(Items[i].Handle))
            {
                SelectedIndex = i;
                break;
            }
        RenderInvalidated?.Invoke();
    }

    /// <summary>
    /// 视图居中到句柄命中图元的并集包围盒中心（T5）：保持当前 Zoom，仅调整 Pan。
    /// 无命中图元或视口尺寸未知（未 Fit）→ 无操作（不触发事件）。
    /// </summary>
    public void CenterOn(IEnumerable<string> handles)
    {
        var wanted = new HashSet<string>(handles.Where(h => h.Length > 0), StringComparer.Ordinal);
        if (wanted.Count == 0 || _viewportW <= 0 || _viewportH <= 0) return;
        List<RenderItem> matched = Items.Where(i => i.Handle.Length > 0 && wanted.Contains(i.Handle)).ToList();
        if (matched.Count == 0) return;

        (double minX, double minY, double maxX, double maxY) = RenderModelBuilder.Bounds(matched);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        double s = Scale;
        // WorldToScreen 逆变换：令 (cx,cy) 落在视口中心 (vpW/2, vpH/2)。
        Pan = (_viewportW / 2 - _offsetX - (cx - _minX) * s,
               _viewportH / 2 - _offsetY - (cy - _minY) * s);
        RenderInvalidated?.Invoke();
    }

    /// <summary>外部修改（如 HiddenLayers 增删）后手动触发重绘。</summary>
    public void Invalidate() => RenderInvalidated?.Invoke();

    /// <summary>世界坐标 → 屏幕坐标（Y 翻转）。</summary>
    public (double X, double Y) WorldToScreen(double worldX, double worldY)
    {
        double s = Scale;
        return ((worldX - _minX) * s + Pan.X + _offsetX,
                _viewportH - ((worldY - _minY) * s + Pan.Y) - _offsetY);
    }

    /// <summary>屏幕坐标 → 世界坐标（WorldToScreen 的逆）。</summary>
    public (double X, double Y) ScreenToWorld(double screenX, double screenY)
    {
        double s = Scale;
        return ((screenX - Pan.X - _offsetX) / s + _minX,
                (_viewportH - screenY - _offsetY - Pan.Y) / s + _minY);
    }

    /// <summary>
    /// 世界坐标命中测试（容差=世界单位）：Line/Polyline 线段距离；Circle/Arc 边界
    /// 径向距离（Arc 另需角度落在 CCW 扫掠范围内，端点带容差松弛）；Text 轴对齐
    /// 包围盒（宽=字符数×字高×0.6，与 T3 画布的水平文本渲染一致，旋转暂不参与）。
    /// 跳过隐藏图层；返回 Items 中首个命中的原始索引。
    /// </summary>
    public int? HitTest(double worldX, double worldY, double tolerance)
    {
        double tol = Math.Max(0, tolerance);
        for (int i = 0; i < Items.Count; i++)
        {
            RenderItem item = Items[i];
            if (HiddenLayers.Contains(item.Layer)) continue;
            if (Hits(item, worldX, worldY, tol)) return i;
        }
        return null;
    }

    private static bool Hits(RenderItem item, double x, double y, double tol)
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
