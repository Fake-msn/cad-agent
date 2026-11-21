using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven 主窗口（无 XAML，计划4 T1）：TextBlock 占位内容，T3 换 Canvas。
/// 加载失败时直接展示 ErrorMessage。
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        Title = vm.Title;
        Width = 1024;
        Height = 768;
        Content = new TextBlock
        {
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
            Text = vm.ErrorMessage ?? $"已加载 {vm.Title}（画布占位，T3 接入 Canvas）"
        };
    }
}
