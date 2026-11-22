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

    // ---------- T4：sheetId 传递（三参 CheckContext）+ 默认接口方法兼容 ----------

    private sealed class SheetRecordingGate : ISemanticContextGate
    {
        public string? LastHandle;
        public string? LastSheetId;
        public bool TwoArgCalled;
        public bool ThreeArgCalled;
        public GateDecision Check(IReadOnlyList<SemanticClass> required) => new(true, "");
        public GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle)
        {
            TwoArgCalled = true;
            LastHandle = targetHandle;
            return new(true, "");
        }
        public GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle, string? sheetId)
        {
            ThreeArgCalled = true;
            LastHandle = targetHandle;
            LastSheetId = sheetId;
            return new(true, "");
        }
    }

    private StepEngine EngineWith(ISemanticGate gate) =>
        new(_audit, new SnapshotStore(Path.Combine(_dir, "s")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AwareHandler() }, gate);

    [Fact]
    public void Engine_PassesSheetId_ToThreeArgCheckContext()
    {
        var gate = new SheetRecordingGate();
        var result = EngineWith(gate).Run(Plan("""{"handle":"h-ok","sheetId":"S1"}"""),
            new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.True(result.Ok);
        Assert.True(gate.ThreeArgCalled);
        Assert.False(gate.TwoArgCalled);
        Assert.Equal("h-ok", gate.LastHandle);
        Assert.Equal("S1", gate.LastSheetId);
    }

    [Fact]
    public void Engine_NoSheetId_UsesLegacyTwoArg()
    {
        // 无 sheetId → 两参 legacy 路径（行为保持）
        var gate = new SheetRecordingGate();
        var result = EngineWith(gate).Run(Plan("""{"handle":"h-ok"}"""),
            new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.True(result.Ok);
        Assert.True(gate.TwoArgCalled);
        Assert.False(gate.ThreeArgCalled);
    }

    [Fact]
    public void Engine_NumericSheetId_CoercedToThreeArg()
    {
        // R12（sanctioned 更新，原 Engine_NonStringSheetId_Ignored_LegacyTwoArg 意图作废）：
        // number sheetId 镜像 handle number→decimal string 强制转换 → 三参作用域生效，
        // 不再 fail-open 静默走两参 legacy（防 LLM 数值 sheetId 解除跨幅防护）。
        var gate = new SheetRecordingGate();
        var result = EngineWith(gate).Run(Plan("""{"handle":"h-ok","sheetId":42}"""),
            new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.True(result.Ok);
        Assert.True(gate.ThreeArgCalled);
        Assert.False(gate.TwoArgCalled);
        Assert.Equal("42", gate.LastSheetId);
    }

    [Fact]
    public void Engine_NonStringNonNumberSheetId_LegacyTwoArg()
    {
        // bool/null 等非法形态（非 number 非 string）→ 仍忽略走两参 legacy（行为保持，不抛）
        var gate = new SheetRecordingGate();
        var result = EngineWith(gate).Run(Plan("""{"handle":"h-ok","sheetId":true}"""),
            new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.True(result.Ok);
        Assert.True(gate.TwoArgCalled);
        Assert.False(gate.ThreeArgCalled);
    }

    [Fact]
    public void Engine_SheetId_TwoArgOnlyFake_DefaultInterfaceDelegates()
    {
        // 仅实现两参的既有 fake（ContextGate）：三参默认接口方法委托两参，不破坏
        var result = _engine.Run(Plan("""{"handle":"h-ok","sheetId":"S9"}"""),
            new FakeSession(Path.Combine(_dir, "f.dxf")), "s1");
        Assert.True(result.Ok);
        Assert.Equal("h-ok", _gate.LastHandle);
    }
}
