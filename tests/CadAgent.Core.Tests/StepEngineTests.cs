using System.Text.Json;

namespace CadAgent.Core.Tests;

public class StepEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cadagent-eng-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly FakeSession _session;
    private readonly StepEngine _engine;

    public StepEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        _snapshots = new SnapshotStore(Path.Combine(_dir, "snapshots"));
        _session = new FakeSession(Path.Combine(_dir, "drawing.dxf"));
        var identity = new FixedIdentity(new OperatorIdentity("TEST\\drawer", Role.Draw));
        _engine = new StepEngine(_audit, _snapshots, identity, new RoleChecker(),
            new IStepHandler[] { new OkHandler("add-line"), new FailHandler("bad-op") });
    }

    public void Dispose()
    {
        _audit.Dispose();
        // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private sealed class OkHandler : IStepHandler
    {
        public OkHandler(string kind) => Kind = kind;
        public string Kind { get; }
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private sealed class FailHandler : IStepHandler
    {
        public FailHandler(string kind) => Kind = kind;
        public string Kind { get; }
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(false, "模拟失败", "");
    }

    private sealed class ThrowingHandler : IStepHandler
    {
        public string Kind => "throw-op";
        public StepResult Apply(IDrawingSession s, JsonElement p) => throw new InvalidOperationException("处理器爆炸");
    }

    private sealed class FakeSession : IDrawingSession
    {
        public FakeSession(string path) { SourcePath = path; File.WriteAllText(path, "v0"); }
        public string SourcePath { get; }
        public int SaveCount { get; private set; }
        public int ReloadCount { get; private set; }
        public bool FailSave { get; set; }
        public bool FailReload { get; set; }
        public string ComputeHash() => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath)));
        public void SaveWorking()
        {
            if (FailSave) throw new IOException("模拟磁盘锁");
            SaveCount++; File.WriteAllText(SourcePath, "v" + SaveCount);
        }
        public void Reload()
        {
            ReloadCount++;
            if (FailReload) throw new IOException("模拟 reload 失败");
        }
        public byte[] CaptureState() => System.Array.Empty<byte>();
        public void RestoreState(byte[] state) { }
    }

    private static StepPlan Plan(params string[] kinds)
    {
        using var empty = JsonDocument.Parse("{}");
        var paramsEl = empty.RootElement.Clone();
        return new("p1", kinds.Select(k => new StepSpec(k, paramsEl)).ToList());
    }

    [Fact]
    public void Parse_ValidPlan_Ok()
    {
        var plan = StepPlanParser.Parse("""
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0}}]}
            """);
        Assert.Equal("p1", plan.PlanId);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void Parse_MissingSteps_Throws()
    {
        Assert.Throws<InvalidPlanException>(() => StepPlanParser.Parse("""{"planId":"p1"}"""));
    }

    [Fact]
    public void Run_AllStepsOk_AuditsEachStep_AndSaves()
    {
        var result = _engine.Run(Plan("add-line", "add-line"), _session, "s1");
        Assert.True(result.Ok);
        Assert.Equal(2, result.StepsDone);
        Assert.Equal(1, _session.SaveCount);
        var log = _audit.ReadAll();
        Assert.Equal(4, log.Count); // tx-start + 2 步 + tx-commit
        Assert.All(log, r => Assert.Equal("TEST\\drawer", r.Entry.Operator));
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_FailingStep_HaltsAndAuditsFailure()
    {
        var result = _engine.Run(Plan("add-line", "bad-op", "add-line"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Equal(1, result.StepsDone);
        Assert.Contains("模拟失败", result.Error);
        var log = _audit.ReadAll();
        Assert.Equal("step-failed", log[^1].Entry.Kind);
        Assert.Equal(0, _session.SaveCount); // 失败事务不落盘
    }

    [Fact]
    public void Run_UnknownKind_Fails()
    {
        var result = _engine.Run(Plan("no-such-op"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Contains("no-such-op", result.Error);
    }

    [Fact]
    public void Run_HigherRoleAllowed_StampsActualRole()
    {
        // 角色是最低权限语义：Review >= Draw，可执行；职责分离（绘制者不能自审）在签认流程强制
        var reviewer = new FixedIdentity(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var engine = new StepEngine(_audit, _snapshots, reviewer, new RoleChecker(),
            new IStepHandler[] { new OkHandler("add-line") });
        var result = engine.Run(Plan("add-line"), _session, "s1");
        Assert.True(result.Ok);
        Assert.Equal("Review", _audit.ReadAll()[^1].Entry.Role);
    }

    [Fact]
    public void Rollback_RestoresSnapshot_AndAudits()
    {
        var result = _engine.Run(Plan("add-line"), _session, "s1");
        File.WriteAllText(_session.SourcePath, "corrupted");
        _engine.Rollback(result.TxId, _session, "s1");
        Assert.Equal("v0", File.ReadAllText(_session.SourcePath));
        Assert.Equal("rollback", _audit.ReadAll()[^1].Entry.Kind);
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_HandlerThrows_RecordsStepFailed()
    {
        var engine = new StepEngine(_audit, _snapshots, new FixedIdentity(new OperatorIdentity("TEST\\drawer", Role.Draw)),
            new RoleChecker(), new IStepHandler[] { new ThrowingHandler() });
        var result = engine.Run(Plan("throw-op"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Contains("处理器爆炸", result.Error);
        Assert.Equal("step-failed", _audit.ReadAll()[^1].Entry.Kind);
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_SaveWorkingThrows_RecordsTxFailed_Terminal()
    {
        _session.FailSave = true;
        var result = _engine.Run(Plan("add-line"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Contains("模拟磁盘锁", result.Error);
        var log = _audit.ReadAll();
        Assert.Equal("tx-failed", log[^1].Entry.Kind);
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Rollback_CallsReload_AndRestoreFailureRecordsRollbackFailed()
    {
        var result = _engine.Run(Plan("add-line"), _session, "s1");
        int reloadsBefore = _session.ReloadCount;
        _engine.Rollback(result.TxId, _session, "s1");
        Assert.Equal(reloadsBefore + 1, _session.ReloadCount);

        // 同一 txId 二次回滚：快照仍在，Restore 成功，Reload 抛错 → rollback-failed 终结记录
        _session.FailReload = true;
        Assert.Throws<IOException>(() => _engine.Rollback(result.TxId, _session, "s1"));
        Assert.Equal("rollback-failed", _audit.ReadAll()[^1].Entry.Kind);
        Assert.True(_audit.VerifyChain(out _));
    }
}
