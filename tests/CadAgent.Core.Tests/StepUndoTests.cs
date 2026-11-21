using System.Text.Json;

namespace CadAgent.Core.Tests;

/// <summary>
/// 3b-ii T2：步级 undo——会话内模型快照栈，每步前捕获，undo 恢复+审计；空栈 false。
/// </summary>
public class StepUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"undo-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly ModelUndoStack _stack = new();
    private readonly StepEngine _engine;
    private readonly FakeSession _session;

    public StepUndoTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
        _session = new FakeSession(Path.Combine(_dir, "d.dxf"));
        _engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(_dir, "s")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AddHandler() }, null, _stack);
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FakeSession : IDrawingSession
    {
        private byte[] _state;
        public FakeSession(string path) { SourcePath = path; _state = new byte[] { 0 }; File.WriteAllBytes(path, _state); }
        public string SourcePath { get; }
        public string ComputeHash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_state));
        public void SaveWorking() => File.WriteAllBytes(SourcePath, _state);
        public void Reload() => _state = File.ReadAllBytes(SourcePath);
        public byte[] CaptureState() => _state;
        public void RestoreState(byte[] state) { _state = state; File.WriteAllBytes(SourcePath, state); }
    }
    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }
    private sealed class AddHandler : IStepHandler
    {
        public string Kind => "mutate";
        public StepResult Apply(IDrawingSession s, JsonElement p)
        {
            s.RestoreState(new byte[] { 1, 2, 3 });
            return new StepResult(true, "", "h1");
        }
    }

    private static StepPlan Plan(int n) =>
        new("p1", Enumerable.Range(0, n).Select(_ => new StepSpec("mutate", JsonDocument.Parse("{}").RootElement)).ToList());

    [Fact]
    public void Run_PushesPerStep_UndoRestoresPrevious()
    {
        var result = _engine.Run(Plan(2), _session, "s1");
        Assert.True(result.Ok);
        Assert.Equal(2, _stack.Count);
        Assert.True(_engine.UndoLastStep(_session, "s1"));
        Assert.Equal(1, _stack.Count);
        Assert.Contains(_audit.ReadAll(), r => r.Entry.Kind == "step-undone");
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Undo_EmptyStack_False_NoAudit()
    {
        Assert.False(_engine.UndoLastStep(_session, "s1"));
        Assert.DoesNotContain(_audit.ReadAll(), r => r.Entry.Kind == "step-undone");
    }

    [Fact]
    public void NoStack_RunUnchanged()
    {
        var engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(_dir, "s2")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AddHandler() });
        Assert.True(engine.Run(Plan(1), _session, "s2").Ok);
        Assert.False(engine.UndoLastStep(_session, "s2"));
    }
}