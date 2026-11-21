using System.Text.Json;

namespace CadAgent.Core.Tests;

/// <summary>
/// Plan 3a T2: ISemanticContextGate (additive) — engine passes step target handle
/// to context gates for entity-level checks; plain ISemanticGate path unchanged.
/// Signature deviation from plan (recorded): CheckContext(required, handle) — the gate
/// owns its overlay; engine has none to pass.
/// </summary>
public class ContextGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ctx-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly ContextGate _gate = new();
    private readonly StepEngine _engine;

    public ContextGateTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
        _engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(_dir, "s")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AwareHandler() }, _gate);
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FakeSession : IDrawingSession
    {
        public FakeSession(string path) { SourcePath = path; File.WriteAllText(path, "v0"); }
        public string SourcePath { get; }
        public string ComputeHash() => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath)));
        public void SaveWorking() => File.WriteAllText(SourcePath, "v1");
        public void Reload() { }
    }
    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }
    private sealed class AwareHandler : IStepHandler, ISemanticAware
    {
        public string Kind => "need-lighting";
        public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Lighting };
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }
    private sealed class ContextGate : ISemanticContextGate
    {
        public string? LastHandle;
        public GateDecision Check(IReadOnlyList<SemanticClass> required) => new(true, "");
        public GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle)
        {
            LastHandle = targetHandle;
            return targetHandle == "h-ok" ? new GateDecision(true, "") : new GateDecision(false, $"实体 {targetHandle} 缺少语义");
        }
    }

    private static StepPlan Plan(string handleJson) =>
        new("p1", new[] { new StepSpec("need-lighting", JsonDocument.Parse(handleJson).RootElement) });

    [Fact]
    public void Engine_PassesStringHandle_ToContextGate()
    {
        var result = _engine.Run(Plan("""{"handle":"h-ok"}"""), new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.True(result.Ok);
        Assert.Equal("h-ok", _gate.LastHandle);
    }

    [Fact]
    public void Engine_PassesNumericHandle_AsString()
    {
        var result = _engine.Run(Plan("""{"handle":72}"""), new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.False(result.Ok); // fake refuses non "h-ok"
        Assert.Equal("72", _gate.LastHandle);
    }

    [Fact]
    public void Engine_ContextRefusal_AuditedWithHandle()
    {
        var result = _engine.Run(Plan("""{"handle":"h-bad"}"""), new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.False(result.Ok);
        Assert.Contains("h-bad", result.Error);
        var last = _audit.ReadAll()[^1];
        Assert.Equal("semantic-gate-refused", last.Entry.Kind);
        Assert.Contains("h-bad", last.Entry.ParamsJson);
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Engine_NoHandle_PassesNull()
    {
        var result = _engine.Run(Plan("{}"), new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.False(result.Ok);
        Assert.Null(_gate.LastHandle);
    }
}