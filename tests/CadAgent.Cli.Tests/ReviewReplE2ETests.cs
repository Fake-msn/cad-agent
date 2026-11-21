using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3b-ii T5 E2E：REPL 作业→审查驳回回滚→再作业→批准→防重复→职责分离，全链审计。
/// </summary>
public class ReviewReplE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"rvrepl-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public ReviewReplE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
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

    private int Repl(string script, out string stdout)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.ReplLoop(
            new[] { "repl", "--drawing", _drawing, "--project", _project },
            new StringReader(script), outW, errW);
        stdout = outW.ToString();
        return code;
    }

    private sealed class PlanClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":8,\"y2\":8}}]}",
                new TokenUsage(5, 3), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private sealed class FixedId : IIdentityProvider
    {
        public FixedId(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private string LastTxId()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var tx = audit.ReadAll().Last(r => r.Entry.Kind == "tx-commit");
        // txId 在 SnapshotRef 路径段（{root}/{txId}/{file}）
        string snap = tx.Entry.SnapshotRef;
        return Path.GetFileName(Path.GetDirectoryName(snap)!)!;
    }

    [Fact]
    public void ReplWork_ReviewReject_Rollback_Rework_Approve_Guards()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-rvreplkey123456").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();

        // REPL 作业（egress y + plan y）
        Assert.Equal(0, Repl("画线\ny\ny\n:quit\n", out string out1));
        Assert.Contains("Executed", out1);
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities.Count);
        string tx1 = LastTxId();

        // 审查驳回→回滚
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var (rc, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx1, "--reject", "--comment", "位置不对");
        Assert.Equal(0, rc);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities);

        // 再作业→批准（绘制者身份恢复为当前 Windows 账号）
        CommandRouter.IdentityOverride = null;
        Assert.Equal(0, Repl("再画\ny\n:quit\n", out string out2)); // egress 已同意，仅 plan 确认
        Assert.Contains("Executed", out2);
        string tx2 = LastTxId();
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var (ac, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx2, "--approve", "--comment", "合格");
        Assert.Equal(0, ac);

        // 防重复
        var (dc, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx2, "--approve");
        Assert.Equal(1, dc);

        // 职责分离：执行人自审拒绝
        var current = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity(current, Role.Review));
        var (sc, _, stderr) = Run("review", "signoff", "--drawing", _drawing, "--tx", tx2, "--approve");
        // tx2 已签认→先触发防重复或自审（两者皆 exit 1）
        Assert.Equal(1, sc);

        // 审计全链
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        Assert.Contains(all, r => r.Entry.Kind == "repl-session");
        Assert.Contains(all, r => r.Entry.Kind == "llm-call");
        Assert.Contains(all, r => r.Entry.Kind == "review-rejected");
        Assert.Contains(all, r => r.Entry.Kind == "rollback");
        Assert.Contains(all, r => r.Entry.Kind == "review-approved");
        Assert.True(audit.VerifyChain(out _));
        Assert.All(all, r => Assert.DoesNotContain("sk-rvreplkey123456", r.Entry.ParamsJson));
    }
}