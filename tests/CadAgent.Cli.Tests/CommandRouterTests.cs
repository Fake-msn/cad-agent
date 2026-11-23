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
    public void Intake_CorruptFile_ExitOne_IntakeRejectedAudited_ChainValid()
    {
        // Spec §9: error handling must all enter the audit. Load throws before any
        // inspection; the rejection must still be recorded.
        string corrupt = Path.Combine(_project, "corrupt.dxf");
        File.WriteAllBytes(corrupt, new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x42, 0x42, 0x42, 0x0D, 0x0A });
        var (code, _, stderr) = Run("intake", corrupt);
        Assert.Equal(1, code);
        Assert.NotEqual("", stderr);
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll());
        Assert.Equal("intake-rejected", rec.Entry.Kind);
        Assert.Equal(corrupt, rec.Entry.Target);
        Assert.Equal("", rec.Entry.BeforeHash);
        Assert.Equal("", rec.Entry.AfterHash);
        Assert.Contains("Error", rec.Entry.ParamsJson);
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_CorruptDrawing_ExitOne_RunRejectedAudited_EmptyHashes()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        string corrupt = Path.Combine(_project, "corrupt.dxf");
        File.WriteAllBytes(corrupt, new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x42, 0x42, 0x42, 0x0D, 0x0A });
        var (code, _, stderr) = Run("run", planPath, "--drawing", corrupt);
        Assert.Equal(1, code);
        Assert.NotEqual("", stderr);
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll());
        Assert.Equal("run-rejected", rec.Entry.Kind);
        Assert.Equal(corrupt, rec.Entry.Target);
        Assert.Equal("", rec.Entry.BeforeHash);
        Assert.Equal("", rec.Entry.AfterHash);
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Rollback_Mismatch_RecordsRollbackRejected_ChainValid()
    {
        // Same A-tx→B pattern as the target-unchanged test; the rejection itself
        // must leave an audit trace (rollback-rejected), not just stderr.
        string drawingB = Path.Combine(_project, "drawingB.dxf");
        File.Move(DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine), drawingB);
        string planPath = Path.Combine(_project, "planA.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, runCode);
        string txId = System.Text.Json.JsonDocument.Parse(runOut).RootElement.GetProperty("txId").GetString()!;
        var (code, _, stderr) = Run("rollback", txId, "--drawing", drawingB);
        Assert.Equal(1, code);
        Assert.NotEqual("", stderr);
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rej = audit.ReadAll().Single(r => r.Entry.Kind == "rollback-rejected");
        Assert.Equal(txId, rej.Entry.Target);
        Assert.Contains("Expected", rej.Entry.ParamsJson);
        Assert.Contains("Actual", rej.Entry.ParamsJson);
        Assert.Contains("drawingB.dxf", rej.Entry.ParamsJson);
        Assert.True(audit.VerifyChain(out _));
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
    public void Intake_MisusedOptionForm_UsageError_ExitOne_NoAuditDb()
    {
        // fix wave（intake 用法守卫）：误用选项形态 intake --drawing x——图纸路径是 positional
        // 而非 --drawing 选项。StripOptionValues 剥选项后 positional=["intake"]，此前
        // positional[1] 裸抛 IndexOutOfRange（Run 兜底打印原生英文消息 exit 1，无用法指引）。
        // 现守卫先于 Intake/OpenAudit：友好用法提示 + exit 1，audit.db 不创建
        // （用法错误不留审计——RequireOption/semantic 子命令守卫先例；.cadagent 目录
        // 由 Run 统一创建属既有行为，不在此断言）。
        var (code, stdout, stderr) = Run("intake", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.Contains("用法", stderr);
        Assert.Contains("intake", stderr);
        Assert.Equal("", stdout);
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
    }

    [Fact]
    public void Intake_NoPositional_UsageError_ExitOne_NoAuditDb()
    {
        // 同型误用：裸 intake（无图纸路径）——同样走用法守卫，不裸抛、不建 audit.db。
        var (code, stdout, stderr) = Run("intake");
        Assert.Equal(1, code);
        Assert.Contains("用法", stderr);
        Assert.Equal("", stdout);
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
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
