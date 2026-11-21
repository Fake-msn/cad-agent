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
}
