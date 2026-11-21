using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven Application（无 XAML）：Fluent 主题 + 主窗口装配。
/// </summary>
public sealed class App : Application
{
    private readonly string _drawingPath;
    private readonly string _projectDir;

    public App(string drawingPath, string projectDir)
    {
        _drawingPath = drawingPath;
        _projectDir = projectDir;
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(new MainViewModel(_drawingPath, _projectDir));
        base.OnFrameworkInitializationCompleted();
    }
}
