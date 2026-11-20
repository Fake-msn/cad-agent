using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class RunGateTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"rungate-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public RunGateTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    [Fact]
    public void Run_WithOverlayPresent_GeometricPlanStillSucceeds()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        CommandRouter.Run(new[] { "semantic", "scan", "--drawing", _drawing, "--project", _project }, outW, errW);
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var outW2 = new StringWriter(); var errW2 = new StringWriter();
        int code = CommandRouter.Run(new[] { "run", planPath, "--drawing", _drawing, "--project", _project }, outW2, errW2);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", outW2.ToString().Replace(" ", ""));
    }
}