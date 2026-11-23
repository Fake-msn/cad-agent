using System.Globalization;
using CadAgent.Data;
using CadAgent.View.Rendering;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 图层条目（计划4 T4）：纯 C#，无 Avalonia 依赖。<see cref="IsVisible"/> 是
/// CanvasViewModel.HiddenLayers 的实时视图（外部增删同样反映）；
/// <see cref="ColorBrush"/> 为 "#RRGGBB" 画刷字符串（取该图层首个图元的解析色；
/// 空图层取图层表颜色，T8；UI 层用 Color.Parse 转 SolidColorBrush）。
/// </summary>
public sealed class LayerEntry
{
    private readonly CanvasViewModel _canvas;

    internal LayerEntry(string name, int entityCount, uint colorRgb, CanvasViewModel canvas)
    {
        _canvas = canvas;
        Name = name;
        EntityCount = entityCount;
        ColorBrush = "#" + colorRgb.ToString("X6", CultureInfo.InvariantCulture);
    }

    /// <summary>图层名（首次出现的原始大小写）。</summary>
    public string Name { get; }

    /// <summary>该图层的渲染图元数（Insert 展开子图元计入其自身图层）。</summary>
    public int EntityCount { get; }

    /// <summary>色样画刷字符串 "#RRGGBB"。</summary>
    public string ColorBrush { get; }

    /// <summary>可见 = 不在画布 HiddenLayers 中（大小写不敏感）。</summary>
    public bool IsVisible => !_canvas.HiddenLayers.Contains(Name);
}

/// <summary>
/// 图层面板视图模型（计划4 T4）：纯 C#，可单测。图层按名序（OrdinalIgnoreCase，
/// CAD 图层名大小写不敏感约定）分组计数。Toggle/Isolate/ShowAll 直接增删
/// CanvasViewModel.HiddenLayers 并触发 RenderInvalidated（画布重绘 + UI 刷新）。
/// 未知图层名：Toggle/Isolate 均为无操作（不触发事件）。
/// T8：传 session 时图层列表为 renderItems ∪ 图层表（空图层计数 0+表色样）；
/// 不传 session 保持 legacy 口径（仅 renderItems 图层）。
/// </summary>
public sealed class LayersViewModel
{
    private readonly CanvasViewModel _canvas;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public LayersViewModel(IReadOnlyList<RenderItem> renderItems, CanvasViewModel canvasVM, DrawingSession? session = null)
    {
        _canvas = canvasVM;
        Layers = BuildEntries(renderItems, canvasVM, session);
        foreach (LayerEntry entry in Layers) _names.Add(entry.Name);
    }

    /// <summary>按名序的图层列表（构造时快照；可见性实时）。T9 Refresh 可重建。</summary>
    public IReadOnlyList<LayerEntry> Layers { get; private set; }

    /// <summary>
    /// 编辑后重建（T9 承接）：按新渲染图元重算图层名/计数/色样（新建图层出现、
    /// 消失图层移除）。传 session 时并入图层表（T8：空图层保留，计数 0+表色样）。
    /// HiddenLayers 归画布所有，隐藏状态原样保留；触发一次重绘。
    /// </summary>
    public void Refresh(IReadOnlyList<RenderItem> renderItems, DrawingSession? session = null)
    {
        Layers = BuildEntries(renderItems, _canvas, session);
        _names.Clear();
        foreach (LayerEntry entry in Layers) _names.Add(entry.Name);
        _canvas.Invalidate();
    }

    private static IReadOnlyList<LayerEntry> BuildEntries(
        IReadOnlyList<RenderItem> renderItems, CanvasViewModel canvasVM, DrawingSession? session)
    {
        var groups = new Dictionary<string, (int Count, uint Color)>(StringComparer.OrdinalIgnoreCase);
        foreach (RenderItem item in renderItems)
        {
            groups[item.Layer] = groups.TryGetValue(item.Layer, out var g)
                ? (g.Count + 1, g.Color)
                : (1, item.ColorRgb);
        }
        // 图层表并集（T9 承接闭环）：空图层也显示（计数 0，色样取图层表颜色）。
        if (session is not null)
        {
            foreach (ACadSharp.Tables.Layer layer in session.Doc.Layers)
            {
                if (!groups.ContainsKey(layer.Name))
                    groups[layer.Name] = (0, RenderModelBuilder.LayerColorRgb(layer));
            }
        }
        return groups
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new LayerEntry(kv.Key, kv.Value.Count, kv.Value.Color, canvasVM))
            .ToList();
    }

    /// <summary>切换图层可见性：HiddenLayers 增删 + RenderInvalidated。</summary>
    public void Toggle(string name)
    {
        if (!_names.Contains(name)) return;
        if (!_canvas.HiddenLayers.Remove(name)) _canvas.HiddenLayers.Add(name);
        _canvas.Invalidate();
    }

    /// <summary>隔离图层：仅保留该图层可见，其余已知图层全部隐藏。</summary>
    public void Isolate(string name)
    {
        if (!_names.Contains(name)) return;
        _canvas.HiddenLayers.Clear();
        foreach (LayerEntry entry in Layers)
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                _canvas.HiddenLayers.Add(entry.Name);
        _canvas.Invalidate();
    }

    /// <summary>显示全部图层。</summary>
    public void ShowAll()
    {
        _canvas.HiddenLayers.Clear();
        _canvas.Invalidate();
    }
}
