using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven 主窗口（无 XAML）：T3 起内容为 DrawingCanvas + 底部状态栏骨架
/// （DockPanel，后续任务向左/右面板扩展）。加载失败时展示 ErrorMessage。
/// </summary>
public sealed class MainWindow : Window
{
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
        var status = new TextBlock
        {
            Margin = new Thickness(8, 4),
            Text = $"{vm.Title} — {items.Count} render items"
        };
        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        dock.Children.Add(new DrawingCanvas(Canvas)); // LastChildFill：画布占满其余区域
        Content = dock;
    }

    /// <summary>画布视图模型（加载成功时非 null）——T4 图层面板/选择联动入口。</summary>
    public CanvasViewModel? Canvas { get; }
}
