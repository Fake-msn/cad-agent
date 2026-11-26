using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven Application（无 XAML）：Fluent 主题 + 主窗口装配。
/// 项3 T5：先显示「加载中…」占位窗口，后台 MainViewModel.LoadAsync 完成后在 UI 线程
/// 替换为 MainWindow（大图 Load 不再阻塞 UI 线程）；LoadAsync 的失败经 vm.ErrorMessage
/// 由 MainWindow 展示；意外异常 → 错误窗口。占位窗已被用户关闭（应用退出）→ 不替换。
/// </summary>
public sealed class App : Application
{
    private readonly string _drawingPath;
    private readonly string _projectDir;
    private readonly IReadOnlyList<string> _skillDirs;

    public App(string drawingPath, string projectDir, IReadOnlyList<string>? skillDirs = null)
    {
        _drawingPath = drawingPath;
        _projectDir = projectDir;
        _skillDirs = skillDirs ?? Array.Empty<string>();
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <summary>
    /// 生命周期回调（async void 因其为框架回调）：占位窗先上屏；LoadAsync 的续体不保证
    /// 回到 UI 线程，故替换窗口/建错误窗一律经 Dispatcher.UIThread.Post 显式编组。
    /// 占位窗已关闭或应用已在退出 → 放弃替换（不得再显示窗口）；异常不静默吞掉（展示错误窗）。
    /// </summary>
    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var placeholder = new Window
        {
            Title = "CadAgent — 加载中",
            Width = 360,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock
            {
                Text = "加载中…",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        desktop.MainWindow = placeholder;
        // 占位窗被用户关闭或应用已在退出 → 放弃替换（不得再显示窗口）。
        bool abandoned = false;
        desktop.ShutdownRequested += (_, _) => abandoned = true;
        placeholder.Closed += (_, _) => abandoned = true;
        try
        {
            MainViewModel vm = await MainViewModel.LoadAsync(_drawingPath, _projectDir, _skillDirs);
            Dispatcher.UIThread.Post(() =>
            {
                if (abandoned || placeholder.IsVisible == false) return;
                try
                {
                    var window = new MainWindow(vm);
                    desktop.MainWindow = window;
                    window.Show(); // 启动后的替换不会自动上屏，须显式 Show（先显示新窗再关占位，免触发退出）
                    placeholder.Close();
                }
                catch (Exception ex)
                {
                    // 主窗装配失败（异常图纸）→ 错误窗口而非静默/崩溃。
                    ShowError(desktop, placeholder, ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (abandoned || placeholder.IsVisible == false) return;
                ShowError(desktop, placeholder, ex.Message);
            });
        }
    }

    private static void ShowError(IClassicDesktopStyleApplicationLifetime desktop, Window placeholder, string message)
    {
        Window error = ErrorWindow(message);
        desktop.MainWindow = error;
        error.Show();
        placeholder.Close();
    }

    private static Window ErrorWindow(string message) => new()
    {
        Title = "CadAgent — 加载失败",
        Width = 520,
        Height = 200,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = new TextBlock
        {
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            Text = message
        }
    };
}
