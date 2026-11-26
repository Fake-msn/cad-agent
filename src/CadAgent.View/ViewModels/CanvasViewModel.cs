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
/// 中心线叠加段（天读证据）：世界坐标直段 + 来源实体句柄（只读叠加，不参与命中/选择）。
/// 数据来自 ExplodeEvidence.CenterlineJson（kind=line），由 DeepReadViewModel 解析。
/// </summary>
public sealed record CenterlineSeg(string Handle, double X1, double Y1, double X2, double Y2);

/// <summary>
/// 选中目标来源投影（项2 Plan-3 T6）：真实实体句柄（块子图元不含子索引后缀）、
/// 最内层块名/属性标签（非块内为 null）、所属空间与只读标志。纯数据可单测。
/// </summary>
public sealed record SelectedSource(
    string Handle, string? BlockName, string? AttributeTag, RenderSpace Space, bool Locked);

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

    private double _baseScale = 1;
    private double _minX, _minY;
    private double _viewportW, _viewportH;
    private double _offsetX, _offsetY;
    private readonly HashSet<string> _highlightedHandles = new(StringComparer.Ordinal);
    private bool _showCenterlines;

    public CanvasViewModel(IReadOnlyList<RenderItem> items)
    {
        Items = items;
        Index = new RenderItemIndex(items);
    }

    /// <summary>渲染图元（T2 RenderModelBuilder 产物，绘制坐标系）。T9 Refresh 可替换。</summary>
    public IReadOnlyList<RenderItem> Items { get; private set; }

    /// <summary>
    /// 渲染图元空间索引（项3 T1）：构造/Refresh 时重建，供视口裁剪与命中宽相位。
    /// 与 <see cref="Items"/> 同步替换，隐藏层在查询内过滤。
    /// </summary>
    public RenderItemIndex Index { get; private set; }

    /// <summary>
    /// 编辑后重建（T9）：替换渲染图元（StepEngine 事务提交/撤回后由 UI 调
    /// RenderModelBuilder.Build 重建）。索引选择/定位高亮/对比叠加全部失效清除
    /// （句柄集可能已不存在；diff 与编辑互斥，编辑后不得残留旧对比）。触发一次重绘。
    /// </summary>
    public void Refresh(IReadOnlyList<RenderItem> items)
    {
        Items = items;
        Index = new RenderItemIndex(items);
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

    /// <summary>选中目标来源投影（块名/属性标签/空间）；null=无选择。</summary>
    public SelectedSource? SelectedSource => SelectedItem is { } it
        ? new SelectedSource(it.SourceHandle ?? it.Handle, it.BlockName, it.AttributeTag, it.Space, it.Locked)
        : null;

    /// <summary>
    /// 问题定位高亮句柄集（T5）：SelectHandles 设置，画布对命中的图元同样施加
    /// 选中高亮；用户点击画布（Select）接管选择时清空。空句柄不参与匹配。
    /// </summary>
    public IReadOnlySet<string> HighlightedHandles => _highlightedHandles;

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

    /// <summary>
    /// 中心线叠加段（天读证据，只读）：SetCenterlines 替换并触发重绘；
    /// 仅 <see cref="ShowCenterlines"/> 时绘制，不参与命中/选择/包围盒。
    /// </summary>
    public IReadOnlyList<CenterlineSeg> Centerlines { get; private set; } = Array.Empty<CenterlineSeg>();

    /// <summary>是否绘制中心线叠加（默认关）；变化触发一次重绘。</summary>
    public bool ShowCenterlines
    {
        get => _showCenterlines;
        set
        {
            if (_showCenterlines == value) return;
            _showCenterlines = value;
            RenderInvalidated?.Invoke();
        }
    }

    /// <summary>替换中心线叠加段（只读预览数据），触发一次重绘。</summary>
    public void SetCenterlines(IReadOnlyList<CenterlineSeg> segs)
    {
        Centerlines = segs ?? Array.Empty<CenterlineSeg>();
        RenderInvalidated?.Invoke();
    }

    /// <summary>
    /// 按真实句柄定位（天读证据行点击）：命中图元含 Insert 展开子图元
    /// "{handle}:{i}"（与 RenderModel 展开口径一致）与块子实体真实句柄
    /// （RenderItem.SourceHandle 精确相等，深读/问题定位用）→ 沿用 T5 定位模式
    /// SelectHandles 高亮 + CenterOn 居中（命中集存渲染句柄，去重）。
    /// 无命中 → 返回 false（不改选择）。
    /// </summary>
    public bool SelectByHandle(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return false;
        List<string> matched = Items
            .Where(i => i.Handle.Length > 0
                && (string.Equals(i.Handle, handle, StringComparison.Ordinal)
                    || i.Handle.StartsWith(handle + ":", StringComparison.Ordinal)
                    || string.Equals(i.SourceHandle, handle, StringComparison.Ordinal)))
            .Select(i => i.Handle)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (matched.Count == 0) return false;
        SelectHandles(matched);
        CenterOn(matched);
        return true;
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

    // 项3 T2：绘制器投影快照（CanvasProjection.From）读取的变换原语，语义同 WorldToScreen。
    internal double OriginX => _minX;
    internal double OriginY => _minY;
    internal double OffsetX => _offsetX;
    internal double OffsetY => _offsetY;
    internal double ViewportWidth => _viewportW;
    internal double ViewportHeight => _viewportH;

    /// <summary>
    /// 当前视口对应的世界矩形（屏幕 0..viewport 逆变换，min/max 归一——屏幕 Y-down 转
    /// 世界 Y-up 后上下翻转，不可直接取两角顺序）。未 Fit（viewport≤0）→ 反相空矩形
    /// (0,0,-1,-1)，调用方按 Min&gt;Max 判空（<see cref="QueryViewport"/> 对其返回空）。
    /// </summary>
    public (double MinX, double MinY, double MaxX, double MaxY) WorldViewport()
    {
        if (_viewportW <= 0 || _viewportH <= 0) return (0, 0, -1, -1);
        (double x0, double y0) = ScreenToWorld(0, 0);
        (double x1, double y1) = ScreenToWorld(_viewportW, _viewportH);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>世界矩形内的可见（非隐藏层）图元索引，升序——委托 <see cref="Index"/>。</summary>
    public IReadOnlyList<int> QueryViewport((double MinX, double MinY, double MaxX, double MaxY) rect) =>
        Index.Query(rect, HiddenLayers);

    /// <summary>
    /// 世界坐标命中测试（容差=世界单位）：委托 <see cref="RenderItemIndex.FirstHit"/>
    /// （bbox 宽相位 + 既有精确判定数学）。跳过隐藏图层；返回 Items 中首个命中的原始索引。
    /// </summary>
    public int? HitTest(double worldX, double worldY, double tolerance) =>
        Index.FirstHit(worldX, worldY, tolerance, HiddenLayers);
}
