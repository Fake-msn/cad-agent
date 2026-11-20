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

    [Fact]
    public void Run_CorruptOverlay_AuditedRefusal_NoExecution()
    {
        // overlay 存在但损坏：不得静默降级为 null 闸口，必须审计拒绝且不执行任何步骤。
        string agentDir = Path.Combine(_project, ".cadagent");
        Directory.CreateDirectory(agentDir);
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        File.WriteAllText(overlayPath, "{ not valid json !!!");
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        byte[] before = File.ReadAllBytes(_drawing);
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "run", planPath, "--drawing", _drawing, "--project", _project }, outW, errW);
        Assert.Equal(1, code);
        Assert.NotEmpty(errW.ToString());
        Assert.DoesNotContain("\"ok\"", outW.ToString());
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 图纸未被执行触碰
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(agentDir, "audit.db"));
        var rec = Assert.Single(audit.ReadAll());
        Assert.Equal("intake-rejected", rec.Entry.Kind);
        Assert.Equal("semantic", rec.Entry.Source);
        Assert.Equal(overlayPath, rec.Entry.Target);
        Assert.Equal("", rec.Entry.BeforeHash);
        Assert.Equal("", rec.Entry.AfterHash);
        Assert.Contains("Error", rec.Entry.ParamsJson);
        Assert.Contains("File", rec.Entry.ParamsJson);
        Assert.True(audit.VerifyChain(out _));
    }
}
