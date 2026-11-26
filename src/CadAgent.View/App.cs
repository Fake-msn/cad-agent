using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven Application（无 XAML）：Fluent 主题 + 主窗口装配。
/// 项3 T5：先显示「加载中…」占位窗口，后台 MainViewModel.LoadAsync 完成后替换为 MainWindow
/// （大图 Load+Build 不再阻塞 UI 线程）；加载失败仍进 MainWindow 展示 ErrorMessage。
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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 占位窗口先上屏（同步赋值 → 框架启动时显示），加载在后台继续。
            var loading = new Window
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
            desktop.MainWindow = loading;
            _ = CompleteLoadAsync(desktop, loading);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 后台加载 → 替换主窗口。替换失败（异常/窗口已随占位窗关闭而退出）防御性收尾，
    /// 不静默崩溃：尽力展示错误窗口，最差保留占位窗。
    /// </summary>
    private async Task CompleteLoadAsync(IClassicDesktopStyleApplicationLifetime desktop, Window loading)
    {
        try
        {
            var vm = await MainViewModel.LoadAsync(_drawingPath, _projectDir, _skillDirs);
            var window = new MainWindow(vm);
            desktop.MainWindow = window;
            window.Show();
            if (loading.IsVisible) loading.Close();
        }
        catch (Exception ex)
        {
            try
            {
                var error = new Window
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
                        Text = ex.Message
                    }
                };
                desktop.MainWindow = error;
                error.Show();
                if (loading.IsVisible) loading.Close();
            }
            catch
            {
                // 兜底窗口也无法创建（应用已在关闭中）：保留占位窗，异常不逃逸。
            }
        }
    }
}
