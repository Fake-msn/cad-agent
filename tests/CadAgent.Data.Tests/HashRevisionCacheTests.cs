using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 项3 §5 ComputeHash 修订缓存：缓存只对「经引擎/显式失效」的变更正确；
/// 绕过路径（直接改 Doc）必须 InvalidateHash，否则 ComputeHash 返回旧值（下文契约钉住）。
/// </summary>
public class HashRevisionCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cadagent-hashrev-{Guid.NewGuid():N}");
    private readonly List<string> _tempFiles = new();

    public HashRevisionCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        foreach (string f in _tempFiles)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private DrawingSession NewSession()
    {
        string path = DxfFixture.CreateTempDxf(doc => doc.Entities.Add(
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) }));
        _tempFiles.Add(path);
        return DrawingSession.Load(path);
    }

    private static StepPlan AddLinePlan() => StepPlanParser.Parse("""
        {"planId":"h1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
        """);

    [Fact]
    public void Load_InitialRevision_IsZero()
    {
        Assert.Equal(0, NewSession().Revision);
    }

    [Fact]
    public void ComputeHash_RepeatedCalls_ComputeOnce()
    {
        var s = NewSession();
        string h1 = s.ComputeHash();
        string h2 = s.ComputeHash();
        Assert.Equal(h1, h2);
        Assert.Equal(1, s.HashComputeCount);
    }

    [Fact]
    public void InvalidateHash_BumpsRevision_ClearsCache()
    {
        var s = NewSession();
        s.ComputeHash();
        s.InvalidateHash();
        Assert.Equal(1, s.Revision);
        s.ComputeHash();
        Assert.Equal(2, s.HashComputeCount);
        Assert.Equal(1, s.Revision); // 计算本身不递增
    }

    [Fact]
    public void CacheEntry_TaggedOldRevision_IsNotServed()
    {
        // 修订标签：陈旧条目（失效前写入/写读竞态）即便非空也不得命中。
        var s = NewSession();
        s.ComputeHash();
        s.InvalidateHash(); // rev=1
        s.SeedHashCacheForTests(0, "STALE-REV0");
        Assert.NotEqual("STALE-REV0", s.ComputeHash());
        Assert.Equal(2, s.HashComputeCount); // 重新计算，未被陈旧条目命中
    }

    [Fact]
    public void CacheEntry_TaggedCurrentRevision_IsServed()
    {
        var s = NewSession();
        s.SeedHashCacheForTests(s.Revision, "SEEDED-CURRENT");
        Assert.Equal("SEEDED-CURRENT", s.ComputeHash());
        Assert.Equal(0, s.HashComputeCount); // 命中即返回
    }

    [Fact]
    public void DirectMutation_StaleUntilInvalidate_ThenNewValue()
    {
        // 契约：绕过引擎的直接 Doc 变更在 InvalidateHash 之前对 ComputeHash 不可见。
        var s = NewSession();
        string before = s.ComputeHash();
        s.Doc.Entities.Add(new Line { StartPoint = new XYZ(9, 9, 0), EndPoint = new XYZ(10, 10, 0) });
        Assert.Equal(before, s.ComputeHash()); // 旧值（文档化契约，非遗漏）
        Assert.Equal(1, s.HashComputeCount);
        s.InvalidateHash();
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void EditViaEngine_ChangesHash()
    {
        var s = NewSession();
        string before = s.ComputeHash();
        Assert.Equal(0, s.Revision);
        using var audit = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        var engine = new StepEngine(audit, new SnapshotStore(Path.Combine(_dir, "snapshots")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AddLineHandler() });

        TxResult r = engine.Run(AddLinePlan(), s, "s1");

        Assert.True(r.Ok, r.Error);
        Assert.NotEqual(before, s.ComputeHash());
        string? after = audit.ReadAll().LastOrDefault(e => e.Entry.Kind == "step")?.Entry.AfterHash;
        Assert.Equal(s.ComputeHash(), after); // 引擎在算 afterHash 前已失效
        Assert.Equal(before, audit.ReadAll().First(e => e.Entry.Kind == "tx-start").Entry.BeforeHash);
    }

    [Fact]
    public void FailedStep_WithoutMutation_KeepsHash_AndRevision()
    {
        var s = NewSession();
        string before = s.ComputeHash();
        using var audit = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        var engine = new StepEngine(audit, new SnapshotStore(Path.Combine(_dir, "snapshots")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new AddLineHandler() });

        TxResult r = engine.Run(StepPlanParser.Parse("""
            {"planId":"h2","steps":[{"kind":"add-line","params":{}}]}
            """), s, "s1"); // params 缺 x1 → 处理器抛出 → 失败步
        Assert.False(r.Ok);

        var failed = Assert.Single(audit.ReadAll(), e => e.Entry.Kind == "step-failed");
        Assert.Equal(failed.Entry.BeforeHash, failed.Entry.AfterHash);
        Assert.Equal(before, failed.Entry.BeforeHash);
        Assert.Equal(0, s.Revision); // 仅成功步失效
    }

    [Fact]
    public void SaveWorking_DoesNotInvalidate()
    {
        var s = NewSession();
        string before = s.ComputeHash();
        int count = s.HashComputeCount;
        s.SaveWorking();
        Assert.Equal(0, s.Revision);
        Assert.Equal(before, s.ComputeHash());
        Assert.Equal(count, s.HashComputeCount);
    }

    [Fact]
    public void Reload_InvalidatesAndRecomputes()
    {
        var s = NewSession();
        string before = s.ComputeHash();
        s.Reload();
        Assert.Equal(1, s.Revision);
        Assert.Equal(before, s.ComputeHash()); // 内容未变 → 同值，但重新计算
        Assert.Equal(2, s.HashComputeCount);
    }

    [Fact]
    public void RestoreState_InvalidatesAndRecomputes()
    {
        var s = NewSession();
        string before = s.ComputeHash();
        byte[] state = s.CaptureState();
        s.RestoreState(state);
        Assert.Equal(1, s.Revision);
        Assert.Equal(before, s.ComputeHash());
        Assert.Equal(2, s.HashComputeCount);
    }
}
