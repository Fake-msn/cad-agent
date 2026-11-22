using Avalonia.Controls;
using Avalonia.Layout;

namespace CadAgent.View.Services;

/// <summary>
/// Delete 确认模态（T9 承接遗留闭环）：ShowDialog&lt;bool&gt; 阻塞 owner，
/// 确认删除→Close(true)，取消→Close(false)。headless 测试经 MainWindow
/// ConfirmDeleteOverride seam 注入，不依赖模态消息泵；真机模态走验证协议。
/// </summary>
internal sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string message)
    {
        Title = "确认删除";
        Width = 360;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确认删除" };
        ok.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close(false);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
