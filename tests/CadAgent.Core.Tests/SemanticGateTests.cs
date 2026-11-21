using System.Text.Json;

namespace CadAgent.Core.Tests;

public class SemanticGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"semgate-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly FakeSession _session;

    public SemanticGateTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        _snapshots = new SnapshotStore(Path.Combine(_dir, "snapshots"));
        _session = new FakeSession(Path.Combine(_dir, "drawing.dxf"));
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
        public int SaveCount { get; private set; }
        public string ComputeHash() => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SourcePath)));
        public void SaveWorking() { SaveCount++; File.WriteAllText(SourcePath, "v" + SaveCount); }
        public void Reload() { }
        public byte[] CaptureState() => System.Array.Empty<byte>();
        public void RestoreState(byte[] state) { }
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private sealed class AwareHandler : IStepHandler, ISemanticAware
    {
        public string Kind => "need-wall";
        public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Wall };
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private sealed class PlainHandler : IStepHandler
    {
        public string Kind => "plain";
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private sealed class OverlayGate : ISemanticGate
    {
        private readonly SemanticOverlay _o;
        private readonly double _min;
        public OverlayGate(SemanticOverlay o, double min = 0.8) { _o = o; _min = min; }
        public GateDecision Check(IReadOnlyList<SemanticClass> required)
        {
            foreach (var c in required)
                if (!_o.Annotations.Any(a => a.Class == c && a.Confidence >= _min))
                    return new GateDecision(false, $"缺少语义 {c}");
            return new GateDecision(true, "");
        }
    }

    private static StepPlan Plan(string kind) =>
        new("p1", new[] { new StepSpec(kind, JsonDocument.Parse("{}").RootElement) });

    private StepEngine Engine(ISemanticGate? gate, params IStepHandler[] handlers) =>
        new(_audit, _snapshots, new FixedIdentity(new OperatorIdentity("TEST\\u", Role.Draw)),
            new RoleChecker(), handlers, gate);

    [Fact]
    public void Gate_Refuses_WhenSemanticsMissing_AuditsAndHalts()
    {
        var result = Engine(new OverlayGate(new SemanticOverlay()), new AwareHandler())
            .Run(Plan("need-wall"), _session, "s1");
        Assert.False(result.Ok);
        Assert.Contains("Wall", result.Error);
        Assert.Equal("semantic-gate-refused", _audit.ReadAll()[^1].Entry.Kind);
        Assert.Equal(0, _session.SaveCount);
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void Gate_Allows_WithHumanConfirmed()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        var result = Engine(new OverlayGate(o), new AwareHandler()).Run(Plan("need-wall"), _session, "s1");
        Assert.True(result.Ok);
    }

    [Fact]
    public void Gate_Refuses_LowConfidence()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.6, "推断"));
        var result = Engine(new OverlayGate(o), new AwareHandler()).Run(Plan("need-wall"), _session, "s1");
        Assert.False(result.Ok);
    }

    [Fact]
    public void NullGate_Additive_DoesNotBlock()
    {
        var result = Engine(null, new AwareHandler()).Run(Plan("need-wall"), _session, "s1");
        Assert.True(result.Ok);
    }

    [Fact]
    public void NonAwareHandler_UnaffectedByGate()
    {
        var result = Engine(new OverlayGate(new SemanticOverlay()), new PlainHandler())
            .Run(Plan("plain"), _session, "s1");
        Assert.True(result.Ok);
    }
}
