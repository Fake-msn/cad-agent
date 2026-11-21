namespace CadAgent.Cli.Tests;

public class ViewCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"cadagent-view-{Guid.NewGuid():N}");
    public ViewCommandTests() => Directory.CreateDirectory(_project);
    public void Dispose() => Directory.Delete(_project, true);

    [Fact]
    public void View_MissingDrawing_ExitOne()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "view", "--drawing", "no-such.dxf", "--project", _project }, outW, errW);
        Assert.Equal(1, code);
        Assert.NotEmpty(errW.ToString());
        // 加强断言（真 RED）：stderr 必须指认缺失的图纸文件，而非落入未知命令 Usage。
        Assert.Contains("no-such.dxf", errW.ToString());
    }

    [Fact]
    public void View_NoDrawingOption_ExitOne()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "view", "--project", _project }, outW, errW);
        Assert.Equal(1, code);
        Assert.NotEmpty(errW.ToString());
    }

    // ---- T10：--skill 直通（arg-builder 单测；进程拉起不做真启动） ----

    [Fact]
    public void View_BuildArgs_NoSkill_LegacyStringUnchanged()
    {
        string args = CommandRouter.BuildViewArgs(@"C:\bin\CadAgent.View.dll", @"C:\d\f.dxf", @"C:\proj",
            Array.Empty<string>());
        Assert.Equal("\"C:\\bin\\CadAgent.View.dll\" --drawing \"C:\\d\\f.dxf\" --project \"C:\\proj\"", args);
    }

    [Fact]
    public void View_BuildArgs_RepeatableSkills_AppendedInOrder()
    {
        string args = CommandRouter.BuildViewArgs(@"C:\bin\CadAgent.View.dll", @"C:\d\f.dxf", @"C:\proj",
            new[] { @"C:\skills\a", @"C:\skills\b" });
        Assert.Equal("\"C:\\bin\\CadAgent.View.dll\" --drawing \"C:\\d\\f.dxf\" --project \"C:\\proj\" " +
            "--skill \"C:\\skills\\a\" --skill \"C:\\skills\\b\"", args);
    }

    [Fact]
    public void View_InvalidSkillDir_ExitOneBeforeLaunch()
    {
        // 图纸存在（ViewLaunch 只查存在性）；skill 目录非法 → 用法错误先于进程拉起
        // （check --skill 先例：校验先于任何副作用，Run 兜底 stderr + exit 1）。
        string drawing = Path.Combine(_project, "empty.dxf");
        File.WriteAllText(drawing, "");
        string badSkill = Path.Combine(_project, "no-such-skill");
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(
            new[] { "view", "--drawing", drawing, "--project", _project, "--skill", badSkill }, outW, errW);
        Assert.Equal(1, code);
        Assert.Contains("no-such-skill", errW.ToString());
    }
}
