using CadAgent.Core;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3b-ii T3：审查签认——review 角色强制/职责分离/防重复/驳回回滚。
/// </summary>
public class ReviewCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public ReviewCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.IdentityOverride = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private string RunTx()
    {
        string plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(plan, """{"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":9,"y2":9}}]}""");
        var (code, stdout, _) = Run("run", plan, "--drawing", _drawing);
        Assert.Equal(0, code);
        return System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("txId").GetString()!;
    }

    private static IIdentityProvider Reviewer() =>
        new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
    private sealed class FixedId : IIdentityProvider
    {
        public FixedId(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    [Fact]
    public void Approve_ReviewerRole_Audited()
    {
        string tx = RunTx();
        CommandRouter.IdentityOverride = Reviewer();
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve", "--comment", "合格");
        Assert.Equal(0, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "review-approved" && r.Entry.ParamsJson.Contains(tx));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void SelfReview_Rejected()
    {
        string tx = RunTx();
        var current = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity(current, Role.Review));
        var (code, _, stderr) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        Assert.Equal(1, code);
        Assert.Contains("自审", stderr);
    }

    [Fact]
    public void Reject_RollsBackDrawing()
    {
        string tx = RunTx();
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities.Count);
        CommandRouter.IdentityOverride = Reviewer();
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--reject", "--comment", "重画");
        Assert.Equal(0, code);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "review-rejected");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "rollback");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void DuplicateSignoff_Rejected()
    {
        string tx = RunTx();
        CommandRouter.IdentityOverride = Reviewer();
        Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        Assert.Equal(1, code);
    }

    [Fact]
    public void DrawRole_CannotSignoff()
    {
        string tx = RunTx();
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\drawer", Role.Draw));
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx, "--approve");
        Assert.Equal(1, code);
    }

    [Fact]
    public void UnknownTx_ExitOne()
    {
        CommandRouter.IdentityOverride = Reviewer();
        var (code, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", "nonexistent", "--approve");
        Assert.Equal(1, code);
    }
}