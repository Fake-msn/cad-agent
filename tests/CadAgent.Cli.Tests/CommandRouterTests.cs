using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class CommandRouterTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"cadagent-cli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public CommandRouterTests()
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

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void Intake_PlainDxf_Green_AndAudited()
    {
        var (code, stdout, _) = Run("intake", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("Green", stdout);
        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
    }

    [Fact]
    public void Run_Plan_AddsLine_AndCommits()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        var session = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(2, session.Summarize().Entities.Count);
    }

    [Fact]
    public void Run_BadPlan_KindFails_ExitOne()
    {
        string planPath = Path.Combine(_project, "bad.json");
        File.WriteAllText(planPath, """{"planId":"p1","steps":[{"kind":"nope","params":{}}]}""");
        var (code, _, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(1, code);
    }

    [Fact]
    public void Rollback_RestoresDrawing()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"delete-entity","params":{"handle":HANDLE}}]}
            """.Replace("HANDLE", CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities[0].Handle));
        var (_, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        string txId = System.Text.Json.JsonDocument.Parse(runOut).RootElement.GetProperty("txId").GetString()!;
        var (code, _, _) = Run("rollback", txId, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities);
    }

    [Fact]
    public void Rollback_TxFromOtherDrawing_ExitOne_TargetUnchanged()
    {
        // Controller add-on 2: a txId whose snapshot belongs to drawing A must not
        // be restored over drawing B, even inside the same project.
        string drawingB = Path.Combine(_project, "drawingB.dxf");
        File.Move(DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine), drawingB);
        string planPath = Path.Combine(_project, "planA.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, runCode);
        string txId = System.Text.Json.JsonDocument.Parse(runOut).RootElement.GetProperty("txId").GetString()!;
        string beforeHash = CadAgent.Data.DrawingSession.Load(drawingB).ComputeHash();
        var (code, _, stderr) = Run("rollback", txId, "--drawing", drawingB);
        Assert.Equal(1, code);
        Assert.NotEqual("", stderr);
        Assert.Equal(beforeHash, CadAgent.Data.DrawingSession.Load(drawingB).ComputeHash());
    }

    [Fact]
    public void AuditVerify_TamperedDb_ExitOne()
    {
        Run("intake", _drawing);
        string db = Path.Combine(_project, ".cadagent", "audit.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audit SET operator='x' WHERE seq=1";
            cmd.ExecuteNonQuery();
        }
        var (code, _, stderr) = Run("audit", "verify");
        Assert.Equal(1, code);
        Assert.Contains("seq 1", stderr);
    }

    [Fact]
    public void RolesShow_PrintsCurrentIdentity()
    {
        var (code, stdout, _) = Run("roles", "show");
        Assert.Equal(0, code);
        Assert.Contains("Draw", stdout); // 未配置账号默认 Draw
    }

    [Fact]
    public void UnknownCommand_ExitOne_WithUsage()
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "frobnicate" }, outW, errW);
        Assert.Equal(1, code);
        Assert.Contains("intake", errW.ToString());
    }
}