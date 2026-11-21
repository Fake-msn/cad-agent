using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven 主窗口（无 XAML）：T4 起为左栏图层面板（复选框可见性 + 色样 +
/// 计数；双击隔离；Show all 恢复）+ 中央 DrawingCanvas + 底部状态栏
/// （选中实体显示真实 DXF 句柄/类型/图层）。加载失败时展示 ErrorMessage。
/// 面板/状态栏通过 CanvasViewModel.RenderInvalidated 与画布同步刷新。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string _baseStatus = "";

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        Title = vm.Title;
        Width = 1200;
        Height = 800;

        if (vm.ErrorMessage is not null)
        {
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = vm.ErrorMessage
            };
            return;
        }

        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(vm.Session);
        Canvas = new CanvasViewModel(items);
        var layers = new LayersViewModel(items, Canvas);
        Layers = layers;

        _baseStatus = $"{vm.Title} — {items.Count} render items";
        var status = new TextBlock { Margin = new Thickness(8, 4), Text = _baseStatus };
        Status = status;

        var showAll = new Button { Content = "Show all", Margin = new Thickness(4, 2) };
        showAll.Click += (_, _) => layers.ShowAll();

        var layerList = new ListBox
        {
            ItemsSource = layers.Layers,
            ItemTemplate = new FuncDataTemplate<LayerEntry>((entry, _) =>
                entry is null ? new Control() : BuildLayerRow(entry, layers))
        };
        layerList.DoubleTapped += OnLayerDoubleTapped;
        LayerList = layerList;

        var header = new TextBlock
        {
            Text = "Layers",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4)
        };
        var panel = new DockPanel { Width = 220 };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        DockPanel.SetDock(showAll, Dock.Top);
        panel.Children.Add(showAll);
        panel.Children.Add(layerList); // LastChildFill：列表占满面板其余区域

        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        DockPanel.SetDock(panel, Dock.Left);
        dock.Children.Add(panel);
        dock.Children.Add(new DrawingCanvas(Canvas)); // LastChildFill：画布占满其余区域
        Content = dock;

        Canvas.RenderInvalidated += OnCanvasInvalidated;
    }

    /// <summary>画布视图模型（加载成功时非 null）——图层面板/选择联动入口。</summary>
    public CanvasViewModel? Canvas { get; }

    /// <summary>图层面板视图模型（加载成功时非 null）。</summary>
    public LayersViewModel? Layers { get; }

    /// <summary>图层列表控件（headless 测试/后续面板任务入口）。</summary>
    public ListBox? LayerList { get; }

    /// <summary>底部状态栏文本控件。</summary>
    public TextBlock? Status { get; }

    /// <summary>行模板：复选框（可见性）+ 色样 + 名称（计数）。Click → Toggle。</summary>
    private static CheckBox BuildLayerRow(LayerEntry entry, LayersViewModel layers)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(Color.Parse(entry.ColorBrush)),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.Name} ({entry.EntityCount})",
            VerticalAlignment = VerticalAlignment.Center
        });
        var check = new CheckBox
        {
            Content = row,
            IsChecked = entry.IsVisible,
            Margin = new Thickness(4, 2)
        };
        check.Click += (_, _) => layers.Toggle(entry.Name);
        return check;
    }

    /// <summary>双击行 → 隔离该图层（ListBox 先选中行，DoubleTapped 取 SelectedItem）。</summary>
    private void OnLayerDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (LayerList?.SelectedItem is LayerEntry entry) Layers!.Isolate(entry.Name);
    }

    /// <summary>画布状态变化（选择/图层可见性/视图）→ 状态栏 + 复选框同步。</summary>
    private void OnCanvasInvalidated()
    {
        if (Status is not null)
        {
            RenderItem? selected = Canvas!.SelectedItem;
            Status.Text = selected is null
                ? _baseStatus
                : $"{_baseStatus} | Selected {selected.Handle} ({selected.Kind}, layer {selected.Layer})";
        }
        if (LayerList is not null)
        {
            foreach (CheckBox check in LayerList.GetVisualDescendants().OfType<CheckBox>())
                if (check.DataContext is LayerEntry entry)
                    check.IsChecked = entry.IsVisible;
        }
    }
}
