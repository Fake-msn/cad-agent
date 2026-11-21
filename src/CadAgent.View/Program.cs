using Avalonia;

namespace CadAgent.View;

/// <summary>
/// 入口（计划4 T1）：--drawing 必需（缺失→stderr + exit 1）；--project 默认 cwd。
/// T10：--skill 可重复（出现序）→ App → MainViewModel.SkillDirs →
/// Issues/Adjudication/Semantic 接线（包校验在 View 内降级为行内错误，不崩 UI）。
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
            Console.Error.WriteLine("用法: CadAgent.View --drawing <file> [--project <dir>] [--skill <dir>...]");
            return 1;
        }
        string project = GetOption(args, "--project") ?? Directory.GetCurrentDirectory();
        IReadOnlyList<string> skillDirs = GetOptionValues(args, "--skill");
        return AppBuilder.Configure(() => new App(drawing, project, skillDirs))
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// 可重复选项的全部值（按出现序；T10 --skill 直通）——与 CLI GetOptionValues
    /// 同口径：尾随无值选项忽略。
    /// </summary>
    internal static IReadOnlyList<string> GetOptionValues(string[] args, string name)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 < args.Length) values.Add(args[i + 1]);
        }
        return values;
    }
}
