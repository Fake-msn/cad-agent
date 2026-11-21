using System.Text.Json;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Semantic;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 6: AuditViewModel (pure C#, no Avalonia). Real SqliteAuditStore +
/// StepEngine transactions over a DXF fixture (2 lines + 1 text): tx grouping by
/// PlanRef within tx-start boundaries (TxId = snapshot directory name), step
/// targets -> HighlightHandles, and snapshot-based before/after diff compared by
/// Handle + canonical geometry key (added/removed/changed). Replay is READ-ONLY:
/// the working file, the snapshot and the in-memory session must be untouched.
/// Missing snapshot -> graceful DiffResult.ErrorMessage (no throw).
/// </summary>
public class AuditViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public SnapshotStore Snapshots { get; }
        public StepEngine Engine { get; }
        public string Line1Handle { get; }
        public string Line2Handle { get; }
        public string TextHandle { get; }

        public Fixture()
        {
            Dir = Path.Combine(Path.GetTempPath(), "cadagent-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(100, 50, 0) });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "OLD", Height = 3.5 });
            });
            Session = DrawingSession.Load(DxfPath);
            var entities = Session.Doc.Entities.ToList();
            Line1Handle = entities[0].Handle.ToString();
            Line2Handle = entities[1].Handle.ToString();
            TextHandle = entities[2].Handle.ToString();
            Audit = new SqliteAuditStore(Path.Combine(Dir, "audit.db"));
            Snapshots = new SnapshotStore(Path.Combine(Dir, "snapshots"));
            Engine = new StepEngine(Audit, Snapshots,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), HandlerRegistry.All());
        }

        public AuditViewModel MakeVm(CanvasViewModel? canvas = null) =>
            new(Audit, Snapshots, Session, canvas);

        public void Dispose()
        {
            Audit.Dispose();
            // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(DxfPath);
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    /// <summary>声明语义需求的测试处理器（真实处理器均几何级；同 Core SemanticGateTests）。</summary>
    private sealed class AwareHandler : IStepHandler, ISemanticAware
    {
        public string Kind => "need-wall";
        public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Wall };
        public StepResult Apply(IDrawingSession s, JsonElement p) => new(true, "", "h1");
    }

    private static StepPlan Plan(string planId, params (string Kind, string Json)[] steps)
    {
        var specs = new List<StepSpec>();
        foreach ((string kind, string json) in steps)
        {
            using var doc = JsonDocument.Parse(json);
            specs.Add(new StepSpec(kind, doc.RootElement.Clone()));
        }
        return new StepPlan(planId, specs);
    }

    /// <summary>add-line (0,60)-(100,60) + move line1 by (5,0) + delete the text.</summary>
    private static StepPlan MixedPlan(Fixture f, string planId = "p1") => Plan(planId,
        ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}"""),
        ("move-entity", $$"""{"handle":{{f.Line1Handle}},"dx":5,"dy":0}"""),
        ("delete-entity", $$"""{"handle":{{f.TextHandle}}}"""));

    // ---- Tx grouping ----

    [Fact]
    public void Transactions_GroupByTx_WithStepsOperatorAndCommitStatus()
    {
        using var f = new Fixture();
        TxResult tx1 = f.Engine.Run(MixedPlan(f), f.Session, "s1");
        TxResult tx2 = f.Engine.Run(Plan("p2",
            ("add-text", """{"x":1,"y":2,"text":"NEW","height":3.5}""")), f.Session, "s1");
        Assert.True(tx1.Ok);
        Assert.True(tx2.Ok);

        AuditViewModel vm = f.MakeVm();
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(8, vm.Entries.Count); // tx1: start+3 steps+commit; tx2: start+step+commit
        Assert.Equal(2, vm.Transactions.Count);

        TxGroup g1 = vm.Transactions[0];
        Assert.Equal(tx1.TxId, g1.TxId);
        Assert.Equal("p1", g1.PlanRef);
        Assert.Equal("tester", g1.Operator);
        Assert.Equal(vm.Entries[0].Ts, g1.StartTs);
        Assert.True(g1.HasCommit);
        Assert.False(g1.HasFailed);
        Assert.Equal(3, g1.Steps.Count);
        Assert.All(g1.Steps, s => Assert.Equal("step", s.Kind));
        Assert.Contains("✓", g1.Status);

        TxGroup g2 = vm.Transactions[1];
        Assert.Equal(tx2.TxId, g2.TxId);
        Assert.Single(g2.Steps);
        Assert.True(g2.HasCommit);
        Assert.False(g2.HasFailed);
    }

    [Fact]
    public void Transactions_FailedStep_HasFailedNoCommit_OnlySuccessfulStepsListed()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(Plan("pf",
            ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}"""),
            ("delete-entity", """{"handle":999999}""")), f.Session, "s1");
        Assert.False(tx.Ok);

        AuditViewModel vm = f.MakeVm();
        TxGroup g = Assert.Single(vm.Transactions);
        Assert.Equal(tx.TxId, g.TxId);
        Assert.True(g.HasFailed);
        Assert.False(g.HasCommit);
        Assert.Contains("✗", g.Status);
        Assert.Single(g.Steps); // the failed step is NOT a "step" record
    }

    [Fact]
    public void Transactions_SemanticGateRefused_ClosesGroupAsFailed_LaterSamePlanRefDoesNotJoin()
    {
        using var f = new Fixture();
        // 真实 StepEngine + 真实 SemanticGate（空叠加层 → 必拒绝）+ 语义需求处理器。
        var gated = new StepEngine(f.Audit, f.Snapshots,
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
            new RoleChecker(), HandlerRegistry.All().Append(new AwareHandler()),
            new SemanticGate(new SemanticOverlay()));
        TxResult refused = gated.Run(Plan("pg", ("need-wall", "{}")), f.Session, "s1");
        Assert.False(refused.Ok);
        Assert.Equal("semantic-gate-refused", f.Audit.ReadAll()[^1].Entry.Kind);

        // 拒绝后同 PlanRef 的后续记录（无新 tx-start）不得并入已终结的分组。
        f.Audit.Append(new AuditEntry(DateTimeOffset.UtcNow, "tester", "Draw", "s1", "plan",
            "step", "h-late", "{}", "", "", "", "pg"));
        // 同 PlanRef 再次执行 → 全新分组。
        TxResult later = gated.Run(Plan("pg",
            ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}""")), f.Session, "s1");
        Assert.True(later.Ok);

        AuditViewModel vm = f.MakeVm();
        Assert.Equal(2, vm.Transactions.Count);
        TxGroup g1 = vm.Transactions[0];
        Assert.Equal(refused.TxId, g1.TxId);
        Assert.True(g1.HasFailed);
        Assert.False(g1.HasCommit);
        Assert.Contains("✗", g1.Status);
        Assert.Empty(g1.Steps); // 被拒步骤未执行；h-late 记录留在 Entries、不入组
        Assert.Contains(vm.Entries, e => e.Target == "h-late");

        TxGroup g2 = vm.Transactions[1];
        Assert.Equal(later.TxId, g2.TxId);
        Assert.NotEqual(g1.TxId, g2.TxId);
        Assert.True(g2.HasCommit);
        Assert.False(g2.HasFailed);
        Assert.Single(g2.Steps);
    }

    [Fact]
    public void Transactions_SamePlanRefRunTwice_StaysTwoSeparateGroups()
    {
        using var f = new Fixture();
        StepPlan plan = Plan("same", ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}"""));
        TxResult tx1 = f.Engine.Run(plan, f.Session, "s1");
        TxResult tx2 = f.Engine.Run(plan, f.Session, "s1");

        AuditViewModel vm = f.MakeVm();
        Assert.Equal(2, vm.Transactions.Count);
        Assert.Equal(tx1.TxId, vm.Transactions[0].TxId);
        Assert.Equal(tx2.TxId, vm.Transactions[1].TxId);
        Assert.NotEqual(vm.Transactions[0].TxId, vm.Transactions[1].TxId);
        Assert.All(vm.Transactions, g => Assert.Single(g.Steps));
    }

    [Fact]
    public void Transactions_RollbackEntry_StaysInEntriesButOutsideGroups()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(MixedPlan(f), f.Session, "s1");
        f.Engine.Rollback(tx.TxId, f.Session, "s1");

        AuditViewModel vm = f.MakeVm();
        Assert.Contains(vm.Entries, e => e.Kind == "rollback");
        TxGroup g = Assert.Single(vm.Transactions);
        Assert.Equal(3, g.Steps.Count); // rollback (PlanRef="") does not join the group
        Assert.True(g.HasCommit);
    }

    [Fact]
    public void EmptyStore_ProducesEmptyEntriesAndTransactions()
    {
        using var f = new Fixture();
        using var empty = new SqliteAuditStore(Path.Combine(f.Dir, "empty.db"));
        var vm = new AuditViewModel(empty, f.Snapshots, f.Session);
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.Entries);
        Assert.Empty(vm.Transactions);
    }

    // ---- SelectTx / replay highlight ----

    [Fact]
    public void SelectTx_ReturnsStepTargets_AndHighlightsCanvas()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(MixedPlan(f), f.Session, "s1");
        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(f.Session);
        var canvas = new CanvasViewModel(items);
        canvas.Fit(RenderModelBuilder.Bounds(items), 1000, 500);
        AuditViewModel vm = f.MakeVm(canvas);

        IReadOnlyList<string> handles = vm.SelectTx(tx.TxId);
        TxGroup g = vm.Transactions[0];
        Assert.Equal(g.Steps.Select(s => s.Target).ToArray(), handles);
        Assert.Equal(tx.TxId, vm.SelectedTxId);
        Assert.NotNull(vm.SelectedSummary);
        Assert.Contains("tester", vm.SelectedSummary);
        Assert.Contains("✓", vm.SelectedSummary);

        string addedHandle = g.Steps[0].Target;
        Assert.Contains(addedHandle, canvas.HighlightedHandles);
        Assert.Contains(f.Line1Handle, canvas.HighlightedHandles);
        // SelectHandles picks the first match in Items order (line1 precedes the added line).
        Assert.Equal(f.Line1Handle, canvas.SelectedHandle);
    }

    [Fact]
    public void SelectTx_UnknownTxId_ReturnsEmpty()
    {
        using var f = new Fixture();
        f.Engine.Run(MixedPlan(f), f.Session, "s1");
        AuditViewModel vm = f.MakeVm();
        Assert.Empty(vm.SelectTx("ffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void SelectTx_WithoutCanvas_DoesNotThrow()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(MixedPlan(f), f.Session, "s1");
        AuditViewModel vm = f.MakeVm();
        Assert.Equal(3, vm.SelectTx(tx.TxId).Count);
    }

    // ---- Diff ----

    [Fact]
    public async Task LoadDiff_AddedRemovedChanged_ByHandleAndGeometry()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(MixedPlan(f), f.Session, "s1");
        AuditViewModel vm = f.MakeVm();

        DiffResult d = await vm.LoadDiffAsync(tx.TxId);
        Assert.Null(d.ErrorMessage);
        Assert.Equal(3, d.BeforeItems.Count); // 2 lines + text (snapshot at tx-start)
        Assert.Equal(3, d.AfterItems.Count);  // 3 lines (added line, moved line1, line2)
        string addedHandle = vm.Transactions[0].Steps[0].Target;
        Assert.Equal(new[] { addedHandle }, d.AddedHandles);
        Assert.Equal(new[] { f.TextHandle }, d.RemovedHandles);
        Assert.Equal(new[] { f.Line1Handle }, d.ChangedHandles); // moved -> same handle, new points
    }

    [Fact]
    public async Task LoadDiff_MovedText_IsChanged()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(Plan("pt",
            ("move-entity", $$"""{"handle":{{f.TextHandle}},"dx":10,"dy":10}""")), f.Session, "s1");
        AuditViewModel vm = f.MakeVm();

        DiffResult d = await vm.LoadDiffAsync(tx.TxId);
        Assert.Null(d.ErrorMessage);
        Assert.Empty(d.AddedHandles);
        Assert.Empty(d.RemovedHandles);
        Assert.Equal(new[] { f.TextHandle }, d.ChangedHandles);
    }

    [Fact]
    public async Task LoadDiff_IdenticalSnapshot_EmptyDiff()
    {
        using var f = new Fixture();
        f.Engine.Run(MixedPlan(f), f.Session, "s1"); // session saved; file == in-memory doc
        f.Snapshots.Take(f.Session.SourcePath, "identicaltx");
        AuditViewModel vm = f.MakeVm();

        DiffResult d = await vm.LoadDiffAsync("identicaltx");
        Assert.Null(d.ErrorMessage);
        Assert.Equal(d.BeforeItems.Count, d.AfterItems.Count);
        Assert.Empty(d.AddedHandles);
        Assert.Empty(d.RemovedHandles);
        Assert.Empty(d.ChangedHandles); // geometry key stable across DXF roundtrip
    }

    [Fact]
    public async Task LoadDiff_IsReadOnly_DrawingSnapshotAndSessionUntouched()
    {
        using var f = new Fixture();
        TxResult tx = f.Engine.Run(MixedPlan(f), f.Session, "s1");
        AuditViewModel vm = f.MakeVm();

        byte[] drawingBefore = File.ReadAllBytes(f.DxfPath);
        byte[] snapshotBefore = File.ReadAllBytes(f.Snapshots.Find(tx.TxId));
        string hashBefore = f.Session.ComputeHash();
        int entityCountBefore = f.Session.Doc.Entities.Count();

        await vm.LoadDiffAsync(tx.TxId);
        vm.SelectTx(tx.TxId);

        Assert.Equal(drawingBefore, File.ReadAllBytes(f.DxfPath));
        Assert.Equal(snapshotBefore, File.ReadAllBytes(f.Snapshots.Find(tx.TxId)));
        Assert.Equal(hashBefore, f.Session.ComputeHash());
        Assert.Equal(entityCountBefore, f.Session.Doc.Entities.Count());
    }

    [Fact]
    public async Task LoadDiff_SnapshotMissing_GracefulError()
    {
        using var f = new Fixture();
        f.Engine.Run(MixedPlan(f), f.Session, "s1");
        AuditViewModel vm = f.MakeVm();

        DiffResult d = await vm.LoadDiffAsync("deadbeefdeadbeefdeadbeefdeadbeef");
        Assert.NotNull(d.ErrorMessage);
        Assert.NotEmpty(d.ErrorMessage);
        Assert.Empty(d.BeforeItems);
        Assert.Empty(d.AfterItems);
        Assert.Empty(d.AddedHandles);
        Assert.Empty(d.RemovedHandles);
        Assert.Empty(d.ChangedHandles);
    }
}
