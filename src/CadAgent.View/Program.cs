using Avalonia;

namespace CadAgent.View;

/// <summary>
/// 入口（计划4 T1）：--drawing 必需（缺失→stderr + exit 1）；--project 默认 cwd。
/// AppBuilder code-driven（无 XAML）；解析结果经 App 构造函数传入（无静态可变状态）。
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? drawing = GetOption(args, "--drawing");
        if (drawing is null)
        {
            Console.Error.WriteLine("用法: CadAgent.View --drawing <file> [--project <dir>]");
            return 1;
        }
        string project = GetOption(args, "--project") ?? Directory.GetCurrentDirectory();
        return AppBuilder.Configure(() => new App(drawing, project))
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
