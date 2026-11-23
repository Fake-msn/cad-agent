using System.Text.Json;
using ACadSharp;
using ACadSharp.Classes;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 9: EditViewModel (pure C#, no Avalonia). ALL manual edits go
/// through StepEngine — no direct Doc mutation, no bypass of the audit
/// pipeline. ApplyAsync builds a one-step StepPlan (planId "view-edit-{guid:N}",
/// kind matching the six handler contracts), audits a "view-edit" REQUEST
/// (Source "view", params {tool, args, planId}) BEFORE execution, then runs
/// StepEngine.Run with the semantic gate injected when .cadagent/semantic.json
/// exists (mirrors CLI RunPlan; corrupt overlay -> intake-rejected, no tx).
/// Success: StepEngine already SaveWorking; RecentTxIds push. Failure:
/// ErrorMessage + session.Reload (mirrors CLI run) — no partial save.
/// UndoAsync mirrors CLI rollback (snapshot-belongs-to-drawing guard ->
/// rollback-rejected audit) then StepEngine.Rollback + refresh.
/// A shared SemaphoreSlim session gate (optional ctor param, wired by
/// MainWindow) serializes edits against off-thread session readers
/// (AuditViewModel.BuildDiff — T6 carryover).
/// Fixture: WALL line (0,0)-(100,0), TEXT "注记" h3.5 at (10,10), block LUM.
/// </summary>
public class EditViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public SnapshotStore Snapshots { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string OverlayPath => Path.Combine(AgentDir, "semantic.json");
        public string LineHandle { get; }
        public string TextHandle { get; }

        /// <summary>textHeight 默认 3.5（合规）；回归测试传 4.2 构造字高违规图纸
        /// （镜像 Cli.Tests RunRegressionTests 的违规夹具构造法）。</summary>
        public Fixture(double textHeight = 3.5)
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "注记", Height = textHeight, Layer = wall });
                DxfFixture.AddLumBlock(doc);
            });
            Session = DrawingSession.Load(DxfPath);
            LineHandle = Session.Doc.Entities.OfType<Line>().First().Handle.ToString();
            TextHandle = Session.Doc.Entities.OfType<TextEntity>().First().Handle.ToString();
            Audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db"));
            Snapshots = new SnapshotStore(Path.Combine(AgentDir, "snapshots"));
        }

        public EditViewModel MakeVm(Role role = Role.Draw, SemaphoreSlim? sessionGate = null) =>
            new(Session, Audit, Snapshots,
                new FixedIdentity(new OperatorIdentity("tester", role)),
                new RoleChecker(), ProjectDir, sessionGate: sessionGate);

        /// <summary>裁定夹具（镜像 IssuesViewModelTests.SaveAdjudication）：Load→Upsert→Save。</summary>
        public void SaveAdjudication(Adjudication a)
        {
            string path = Path.Combine(AgentDir, "adjudications.json");
            var set = AdjudicationSet.Load(path);
            set.Upsert(a);
            set.Save(path);
        }

        public void Dispose()
        {
            Audit.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private static JsonElement Params(AuditRecord record) =>
        JsonDocument.Parse(record.Entry.ParamsJson).RootElement;

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- Line: entity +1, request audit BEFORE tx, chain valid ----

    [Fact]
    public async Task DrawLine_AddsEntity_RequestAuditBeforeTx_ChainValid()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        int countBefore = f.Session.Doc.Entities.Count;
        CadDocument docBefore = f.Session.Doc;

        EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("", r.Error);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(countBefore + 1, f.Session.Doc.Entities.Count);
        Assert.Same(docBefore, f.Session.Doc); // success: no Reload (StepEngine already SaveWorking)
        // Persisted to disk by StepEngine.SaveWorking — a fresh load sees the line.
        Assert.Equal(countBefore + 1, DrawingSession.Load(f.DxfPath).Doc.Entities.Count);
        Line added = f.Session.Doc.Entities.OfType<Line>()
            .Single(l => l.EndPoint.X == 50 && l.EndPoint.Y == 50);
        Assert.Equal("0", added.Layer.Name); // default layer

        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        AuditRecord req = Assert.Single(records, x => x.Entry.Kind == "view-edit");
        AuditRecord start = Assert.Single(records, x => x.Entry.Kind == "tx-start");
        AuditRecord step = Assert.Single(records, x => x.Entry.Kind == "step");
        AuditRecord commit = Assert.Single(records, x => x.Entry.Kind == "tx-commit");
        // REQUEST audit strictly before the transaction starts.
        Assert.True(req.Seq < start.Seq, $"view-edit Seq {req.Seq} must precede tx-start Seq {start.Seq}");
        Assert.True(start.Seq < step.Seq);
        Assert.True(step.Seq < commit.Seq);
        Assert.Equal("view", req.Entry.Source);
        Assert.Equal("tester", req.Entry.Operator);
        Assert.Equal("Draw", req.Entry.Role);
        Assert.Equal(f.Session.SourcePath, req.Entry.Target);
        Assert.Equal(req.Entry.BeforeHash, req.Entry.AfterHash); // request itself changes nothing
        Assert.StartsWith("view-edit-", start.Entry.PlanRef);
        Assert.Equal(start.Entry.PlanRef, req.Entry.PlanRef);
        JsonElement p = Params(req);
        Assert.Equal("Line", p.GetProperty("tool").GetString());
        Assert.Equal(start.Entry.PlanRef, p.GetProperty("planId").GetString());
        JsonElement a = p.GetProperty("args");
        Assert.Equal(0, a.GetProperty("x1").GetDouble());
        Assert.Equal(50, a.GetProperty("x2").GetDouble());
        Assert.Equal(50, a.GetProperty("y2").GetDouble());
        Assert.Equal("add-line", Params(step).GetProperty("Kind").GetString());
        Assert.Equal(added.Handle.ToString(), step.Entry.Target);
        Assert.Equal(new[] { r.TxId }, vm.RecentTxIds);
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task DrawLine_WithLayer_HandlerResolvesLayer()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.DrawLineAsync(0, 0, 10, 10, "E-LITE");
        Assert.True(r.Ok, r.Error);
        Line added = f.Session.Doc.Entities.OfType<Line>().Single(l => l.EndPoint.X == 10);
        Assert.Equal("E-LITE", added.Layer.Name);
    }

    [Fact]
    public async Task ApplyAsync_RaisesEditCompleted_OnSuccessAndFailure()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        int raised = 0;
        vm.EditCompleted += () => raised++;
        await vm.DrawLineAsync(0, 0, 1, 1);
        Assert.Equal(1, raised);
        await vm.MoveAsync("999999", 1, 1); // failure also notifies (view must refresh)
        Assert.Equal(2, raised);
        await vm.UndoAsync(vm.RecentTxIds[0]);
        Assert.Equal(3, raised);
    }

    // ---- Undo: entity restored + rollback audit ----

    [Fact]
    public async Task Undo_RestoresEntity_AuditsRollback_ClearsStack()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        int countBefore = f.Session.Doc.Entities.Count;
        byte[] fileBefore = await File.ReadAllBytesAsync(f.DxfPath);
        EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(countBefore + 1, f.Session.Doc.Entities.Count);

        await vm.UndoAsync(r.TxId);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(countBefore, f.Session.Doc.Entities.Count);
        Assert.Equal(fileBefore, await File.ReadAllBytesAsync(f.DxfPath)); // snapshot restored on disk
        Assert.Empty(vm.RecentTxIds);
        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "rollback");
        Assert.Equal("plan", rec.Entry.Source);
        Assert.Equal(f.Session.SourcePath, rec.Entry.Target);
        Assert.Equal(r.TxId, rec.Entry.ParamsJson);
        Assert.Equal(r.TxId, rec.Entry.SnapshotRef);
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Undo_UnknownTx_ErrorMessage_NoAudit()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        await vm.UndoAsync("nosuchtx");
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("无快照", vm.ErrorMessage);
        Assert.Empty(f.Audit.ReadAll()); // CLI parity: Find fails before any side effect
        Assert.Empty(vm.RecentTxIds);
    }

    [Fact]
    public async Task Undo_ForeignSnapshot_Refused_Audited_DrawingUntouched()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        string otherDxf = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0) }));
        try
        {
            f.Snapshots.Take(otherDxf, "foreigntx");
            byte[] fileBefore = await File.ReadAllBytesAsync(f.DxfPath);
            await vm.UndoAsync("foreigntx");
            Assert.NotNull(vm.ErrorMessage);
            Assert.Contains("已拒绝撤回", vm.ErrorMessage);
            Assert.Equal(fileBefore, await File.ReadAllBytesAsync(f.DxfPath));
            AuditRecord rec = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "rollback-rejected");
            Assert.Equal("rollback", rec.Entry.Source);
            Assert.Equal("foreigntx", rec.Entry.Target);
            JsonElement p = Params(rec);
            Assert.Equal(Path.GetFileName(f.Session.SourcePath), p.GetProperty("Expected").GetString());
            Assert.Equal(Path.GetFileName(otherDxf), p.GetProperty("Actual").GetString());
            Assert.True(f.Audit.VerifyChain(out string error), error);
        }
        finally { File.Delete(otherDxf); }
    }

    // ---- The other five handlers: correct effect + tool/kind audit ----

    [Fact]
    public async Task Delete_RemovesEntity_AuditsDeleteEntity()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        int countBefore = f.Session.Doc.Entities.Count;
        EditResult r = await vm.DeleteAsync(f.LineHandle);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(countBefore - 1, f.Session.Doc.Entities.Count);
        Assert.DoesNotContain(f.Session.Doc.Entities, e => e.Handle.ToString() == f.LineHandle);
        AuditRecord req = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "view-edit");
        Assert.Equal("Delete", Params(req).GetProperty("tool").GetString());
        AuditRecord step = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "step");
        Assert.Equal("delete-entity", Params(step).GetProperty("Kind").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Move_ShiftsLineEndpoints_AuditsMoveEntity()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.MoveAsync(f.LineHandle, 10, 20);
        Assert.True(r.Ok, r.Error);
        Line line = f.Session.Doc.Entities.OfType<Line>().Single(l => l.Handle.ToString() == f.LineHandle);
        Assert.Equal(10, line.StartPoint.X);
        Assert.Equal(20, line.StartPoint.Y);
        Assert.Equal(110, line.EndPoint.X);
        Assert.Equal(20, line.EndPoint.Y);
        AuditRecord req = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "view-edit");
        Assert.Equal("Move", Params(req).GetProperty("tool").GetString());
        AuditRecord step = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "step");
        Assert.Equal("move-entity", Params(step).GetProperty("Kind").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task ChangeLayer_MovesEntityToLayer_AuditsChangeLayer()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.ChangeLayerAsync(f.TextHandle, "E-TEXT");
        Assert.True(r.Ok, r.Error);
        TextEntity text = f.Session.Doc.Entities.OfType<TextEntity>().Single();
        Assert.Equal("E-TEXT", text.Layer.Name);
        AuditRecord req = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "view-edit");
        Assert.Equal("ChangeLayer", Params(req).GetProperty("tool").GetString());
        Assert.Equal("E-TEXT", Params(req).GetProperty("args").GetProperty("layer").GetString());
        AuditRecord step = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "step");
        Assert.Equal("change-layer", Params(step).GetProperty("Kind").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task InsertBlock_AddsInsert_AuditsInsertBlock()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        int countBefore = f.Session.Doc.Entities.Count;
        EditResult r = await vm.InsertBlockAsync("LUM", 5, 6);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(countBefore + 1, f.Session.Doc.Entities.Count);
        Insert insert = Assert.Single(f.Session.Doc.Entities.OfType<Insert>());
        Assert.Equal(5, insert.InsertPoint.X);
        Assert.Equal(6, insert.InsertPoint.Y);
        Assert.Equal(1, insert.XScale);
        AuditRecord req = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "view-edit");
        Assert.Equal("InsertBlock", Params(req).GetProperty("tool").GetString());
        AuditRecord step = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "step");
        Assert.Equal("insert-block", Params(step).GetProperty("Kind").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task InsertBlock_UnknownBlock_Fails_Reload()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.InsertBlockAsync("NO-SUCH-BLOCK", 0, 0);
        Assert.False(r.Ok);
        Assert.Contains("块不存在", r.Error);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.RecentTxIds);
        Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "step-failed");
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task AddText_AddsTextEntity_AuditsAddText()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.AddTextAsync(20, 30, "新注记", 3.5);
        Assert.True(r.Ok, r.Error);
        TextEntity added = f.Session.Doc.Entities.OfType<TextEntity>().Single(t => t.Value == "新注记");
        Assert.Equal(20, added.InsertPoint.X);
        Assert.Equal(30, added.InsertPoint.Y);
        Assert.Equal(3.5, added.Height);
        AuditRecord req = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "view-edit");
        Assert.Equal("Text", Params(req).GetProperty("tool").GetString());
        AuditRecord step = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "step");
        Assert.Equal("add-text", Params(step).GetProperty("Kind").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- Failure: ErrorMessage + Reload + no partial save; request still before tx ----

    [Fact]
    public async Task BadHandle_Fails_ErrorMessage_Reload_NoPartialSave_RequestBeforeTx()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        CadDocument docBefore = f.Session.Doc;
        byte[] fileBefore = await File.ReadAllBytesAsync(f.DxfPath);

        EditResult r = await vm.MoveAsync("999999", 10, 20);

        Assert.False(r.Ok);
        Assert.Contains("句柄不存在", r.Error);
        Assert.Equal(r.Error, vm.ErrorMessage);
        Assert.NotSame(docBefore, f.Session.Doc); // failure -> Reload (mirrors CLI run)
        Assert.Equal(fileBefore, await File.ReadAllBytesAsync(f.DxfPath)); // no partial save
        Assert.Empty(vm.RecentTxIds);
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        AuditRecord req = Assert.Single(records, x => x.Entry.Kind == "view-edit");
        AuditRecord start = Assert.Single(records, x => x.Entry.Kind == "tx-start");
        Assert.True(req.Seq < start.Seq); // request audit even on the failure path precedes tx
        Assert.Single(records, x => x.Entry.Kind == "step-failed");
        Assert.DoesNotContain(records, x => x.Entry.Kind == "tx-commit");
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- Validation before side effects (no audit, no tx) ----

    [Fact]
    public async Task SelectTool_NoEdit_NoAudit()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.ApplyAsync(EditTool.Select, Args("{}"));
        Assert.False(r.Ok);
        Assert.Empty(f.Audit.ReadAll());
        Assert.Empty(vm.RecentTxIds);
    }

    [Fact]
    public async Task NonObjectArgs_Refused_NoAudit()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.ApplyAsync(EditTool.Line, Args("[]"));
        Assert.False(r.Ok);
        Assert.Empty(f.Audit.ReadAll());
    }

    [Fact]
    public async Task InvalidHandleString_Refused_NoAudit()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.MoveAsync("not-a-handle", 1, 2);
        Assert.False(r.Ok);
        Assert.Contains("非法句柄", r.Error);
        Assert.Empty(f.Audit.ReadAll());
    }

    // ---- Role gate: edit is Draw-level (all roles >= Draw allowed) ----

    [Theory]
    [InlineData(Role.Draw)]
    [InlineData(Role.Review)]
    [InlineData(Role.Approve)]
    public async Task DrawLevelEdit_AllRolesAllowed(Role role)
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm(role);
        EditResult r = await vm.DrawLineAsync(0, 0, 1, 1);
        Assert.True(r.Ok, r.Error);
        AuditRecord req = Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "view-edit");
        Assert.Equal(role.ToString(), req.Entry.Role);
    }

    // ---- Semantic gate injection mirrors CLI RunPlan ----

    [Fact]
    public async Task CorruptOverlay_AuditsIntakeRejected_NoTx_DrawingUntouched()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(f.OverlayPath, "{ not json");
        EditViewModel vm = f.MakeVm();
        byte[] fileBefore = await File.ReadAllBytesAsync(f.DxfPath);

        EditResult r = await vm.DrawLineAsync(0, 0, 1, 1);

        Assert.False(r.Ok);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(fileBefore, await File.ReadAllBytesAsync(f.DxfPath));
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        Assert.Single(records, x => x.Entry.Kind == "view-edit"); // request was audited first
        AuditRecord rejected = Assert.Single(records, x => x.Entry.Kind == "intake-rejected");
        Assert.Equal("semantic", rejected.Entry.Source); // CLI LoadOrReject parity
        Assert.Equal(f.OverlayPath, rejected.Entry.Target);
        Assert.DoesNotContain(records, x => x.Entry.Kind == "tx-start");
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task ValidOverlay_GeometricEditSucceeds_GateInjected()
    {
        using var f = new Fixture();
        new SemanticOverlay().Save(f.OverlayPath); // exists but empty: geometric handlers declare no semantics
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.DrawLineAsync(0, 0, 1, 1);
        Assert.True(r.Ok, r.Error);
        Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "tx-commit");
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- Session gate: edits serialized; diff reads share the gate (T6 carryover) ----

    [Fact]
    public async Task ConcurrentEdits_Serialized_BothCommitted_ChainValid()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        int countBefore = f.Session.Doc.Entities.Count;

        EditResult[] results = await Task.WhenAll(
            vm.DrawLineAsync(0, 0, 10, 10),
            vm.DrawLineAsync(20, 20, 30, 30));

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(countBefore + 2, f.Session.Doc.Entities.Count);
        Assert.Equal(2, vm.RecentTxIds.Count);
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        Assert.Equal(2, records.Count(x => x.Entry.Kind == "view-edit"));
        Assert.Equal(2, records.Count(x => x.Entry.Kind == "tx-start"));
        Assert.Equal(2, records.Count(x => x.Entry.Kind == "tx-commit"));
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task DiffDuringEdit_SharedSessionGate_BothSucceed()
    {
        using var f = new Fixture();
        var gate = new SemaphoreSlim(1, 1);
        EditViewModel vm = f.MakeVm(sessionGate: gate);
        EditResult first = await vm.DrawLineAsync(0, 0, 10, 10);
        Assert.True(first.Ok, first.Error);
        var auditVm = new AuditViewModel(f.Audit, f.Snapshots, f.Session, sessionGate: gate);

        Task<DiffResult> diffTask = auditVm.LoadDiffAsync(first.TxId);
        Task<EditResult> editTask = vm.DrawLineAsync(20, 20, 30, 30);
        DiffResult diff = await diffTask;
        EditResult second = await editTask;

        Assert.Null(diff.ErrorMessage); // off-thread session read never sees a torn Doc
        Assert.True(second.Ok, second.Error);
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- Admission gate (final review fix 1): mirror CLI RunPlan at the edit boundary ----

    /// <summary>
    /// Red fixture: a proxy-only drawing with NO native entities (R6). ACadSharp 3.8.35
    /// ProxyEntity has a public ctor + settable DxfClass; the DxfWriter/DxfReader roundtrip
    /// preserves it as a ProxyEntity (verified). Summarize() puts the proxy only into
    /// Unknowns (Entities stays empty), so GateDetector.Inspect with EmptySupportMatrix
    /// yields Red (all unknowns unsupported AND nativeCount==Entities.Count==0).
    /// A native Line was removed in the R6 fix round: with a Line present the drawing is
    /// mixed (nativeCount>0) and correctly classifies Yellow, not Red.
    /// </summary>
    private static string RedDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new ProxyEntity
        {
            DxfClass = new DxfClass
            {
                DxfName = "TCH_WALL",
                CppClassName = "TCH_WALL",
                ApplicationName = "Tangent",
                ClassNumber = 500,
                IsAnEntity = true,
                DwgVersion = ACadVersion.AC1027
            }
        });
    });

    [Fact]
    public async Task RedDrawing_Refused_RunRejectedAudit_NoTx_DrawingUntouched()
    {
        string dxf = RedDxf();
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-edit-red-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".cadagent"));
        try
        {
            DrawingSession session = DrawingSession.Load(dxf);
            // Fixture sanity: the proxy survives the roundtrip and the gate decision is Red.
            GateReport report = GateDetector.Inspect(session.Summarize(), new EmptySupportMatrix());
            Assert.Equal(GateLevel.Red, report.Level);
            using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
            var snapshots = new SnapshotStore(Path.Combine(projectDir, ".cadagent", "snapshots"));
            var vm = new EditViewModel(session, audit, snapshots,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), projectDir);
            byte[] fileBefore = await File.ReadAllBytesAsync(dxf);
            int countBefore = session.Doc.Entities.Count;

            EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);

            Assert.False(r.Ok);
            Assert.Equal("", r.TxId);
            Assert.Contains("T3", r.Error); // T3 conversion guidance (CLI parity)
            Assert.Equal(r.Error, vm.ErrorMessage);
            Assert.Empty(vm.RecentTxIds);
            Assert.Equal(fileBefore, await File.ReadAllBytesAsync(dxf)); // no execution, no save
            Assert.Equal(countBefore, session.Doc.Entities.Count);
            List<AuditRecord> records = audit.ReadAll().ToList();
            AuditRecord req = Assert.Single(records, x => x.Entry.Kind == "view-edit");
            AuditRecord rejected = Assert.Single(records, x => x.Entry.Kind == "run-rejected");
            Assert.True(req.Seq < rejected.Seq); // request audit first, then refusal
            Assert.Equal("view", rejected.Entry.Source);
            Assert.Equal(session.SourcePath, rejected.Entry.Target);
            Assert.Equal(req.Entry.BeforeHash, rejected.Entry.BeforeHash);
            Assert.Equal(rejected.Entry.BeforeHash, rejected.Entry.AfterHash); // refusal changes nothing
            JsonElement p = Params(rejected);
            Assert.Equal("Red", p.GetProperty("level").GetString());
            Assert.Equal(report.Unsupported.Count, p.GetProperty("unsupportedCount").GetInt32());
            Assert.DoesNotContain(records, x => x.Entry.Kind == "tx-start");
            Assert.DoesNotContain(records, x => x.Entry.Kind == "step");
            Assert.DoesNotContain(records, x => x.Entry.Kind == "tx-commit");
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dxf);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }

    [Fact]
    public async Task RedDrawing_GateCheckedOnEveryApply_EachRefusalAudited()
    {
        string dxf = RedDxf();
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-edit-red2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".cadagent"));
        try
        {
            DrawingSession session = DrawingSession.Load(dxf);
            using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
            var vm = new EditViewModel(session, audit, new SnapshotStore(Path.Combine(projectDir, ".cadagent", "snapshots")),
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), projectDir);

            Assert.False((await vm.DrawLineAsync(0, 0, 1, 1)).Ok);
            Assert.False((await vm.AddTextAsync(0, 0, "x", 3.5)).Ok);

            Assert.Equal(2, audit.ReadAll().Count(x => x.Entry.Kind == "run-rejected"));
            Assert.DoesNotContain(audit.ReadAll(), x => x.Entry.Kind == "tx-start");
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dxf);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }

    [Fact]
    public async Task NormalDrawing_PassesAdmissionGate_NoRunRejected()
    {
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();
        EditResult r = await vm.DrawLineAsync(0, 0, 1, 1);
        Assert.True(r.Ok, r.Error);
        Assert.DoesNotContain(f.Audit.ReadAll(), x => x.Entry.Kind == "run-rejected");
        Assert.Single(f.Audit.ReadAll(), x => x.Entry.Kind == "tx-commit");
    }

    // ---- 视图闭环 T7：编辑后 run-regression（镜像 CLI RunRegressionCheck，观察性） ----

    [Fact]
    public async Task Edit_Success_MandatoryViolation_RegressionWarningAndAudit()
    {
        // 夹具：字高 4.2 违规 TextEntity（镜像 Cli.Tests RunRegressionTests）+
        // adjudications.json 升级 R-50001-5.0.2 为 mandatory（镜像既有裁定夹具）。
        using var f = new Fixture(textHeight: 4.2);
        f.SaveAdjudication(new Adjudication("R-50001-5.0.2", "tester", DateTimeOffset.UtcNow,
            "院标升级", ForceOverride: RuleForce.Mandatory));
        EditViewModel vm = f.MakeVm();

        EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);

        // tx 仍 Ok（观察性不回滚）；警告含 mandatory 违规与"不自动回滚"（决策权归人）。
        Assert.True(r.Ok, r.Error);
        Assert.Contains("强制(mandatory)规范违规", r.Warning);
        Assert.Contains("不自动回滚", r.Warning);
        Assert.Contains("R-50001-5.0.2", r.Warning);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new[] { r.TxId }, vm.RecentTxIds);
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        AuditRecord commit = Assert.Single(records, x => x.Entry.Kind == "tx-commit");
        Assert.DoesNotContain(records, x => x.Entry.Kind == "rollback");
        // 无论有无违规均审计 check-run（Source="view-regression"），且在 tx 提交之后。
        AuditRecord reg = Assert.Single(records,
            x => x.Entry.Source == "view-regression" && x.Entry.Kind == "check-run");
        Assert.True(commit.Seq < reg.Seq);
        Assert.Equal(f.Session.SourcePath, reg.Entry.Target);
        Assert.Equal(reg.Entry.BeforeHash, reg.Entry.AfterHash); // 只读检查：前后哈希一致
        Assert.Equal(f.Session.ComputeHash(), reg.Entry.AfterHash); // 提交后图纸哈希
        JsonElement p = Params(reg);
        Assert.True(p.GetProperty("mandatoryViolations").GetInt32() >= 1);
        Assert.False(p.GetProperty("overlayPresent").GetBoolean()); // 夹具无 overlay
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Edit_Success_CleanDrawing_RegressionAuditNoWarning()
    {
        // 合规图纸（字高 3.5）+ 无裁定（全 recommended）→ 无警告，但 check-run 仍留痕。
        using var f = new Fixture();
        EditViewModel vm = f.MakeVm();

        EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("", r.Warning); // Yellow 在 EmptySupportMatrix 下不可达 → Warning 空
        AuditRecord reg = Assert.Single(f.Audit.ReadAll(),
            x => x.Entry.Source == "view-regression" && x.Entry.Kind == "check-run");
        JsonElement p = Params(reg);
        Assert.Equal(0, p.GetProperty("mandatoryViolations").GetInt32());
        Assert.Equal(reg.Entry.BeforeHash, reg.Entry.AfterHash);
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Edit_Success_AdjudicationsCorrupt_RegressionSkippedAuditErrorTxUnaffected()
    {
        // adjudications.json 垃圾字节 → Load 抛 → catch 审计 Error 路径（观察性不影响 tx）。
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.AgentDir, "adjudications.json"), "{corrupt json!!");
        EditViewModel vm = f.MakeVm();
        int countBefore = f.Session.Doc.Entities.Count;

        EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);

        Assert.True(r.Ok, r.Error); // tx 仍 Ok
        Assert.Contains("回归检查未完成", r.Warning);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(countBefore + 1, f.Session.Doc.Entities.Count);
        Assert.Equal(countBefore + 1, DrawingSession.Load(f.DxfPath).Doc.Entities.Count); // 已落盘不回滚
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        Assert.Single(records, x => x.Entry.Kind == "tx-commit");
        Assert.DoesNotContain(records, x => x.Entry.Kind == "rollback");
        AuditRecord reg = Assert.Single(records,
            x => x.Entry.Source == "view-regression" && x.Entry.Kind == "check-run");
        JsonElement p = Params(reg);
        Assert.True(p.TryGetProperty("Error", out JsonElement err)); // 故障也入审计（spec §9）
        Assert.False(string.IsNullOrEmpty(err.GetString()));
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- 防假 Green 切片 T3：Yellow（有原生+未解析 census）修改拒绝（只读审查） ----

    /// <summary>
    /// Yellow fixture：原生 Line（nativeCount&gt;0）+ census TCH_WALL×2（Load persisted 参注入，
    /// 等价 sidecar 恢复路径）→ GateDetector 三分判 Yellow。T3 前 Yellow 放行+警示；
    /// T3 起与 Red 同样拒绝：run-rejected 留痕（params 含 level/unparsedCount/guidance）、无 tx。
    /// </summary>
    [Fact]
    public async Task Edit_YellowUnparsed_RefusedNoTx_RunRejectedAudit()
    {
        string dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-edit-yellow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".cadagent"));
        var census = new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) });
        try
        {
            DrawingSession session = DrawingSession.Load(dxf, census);
            // Fixture sanity：有原生实体 + 未解析 census → Yellow（T2 三分），guidance 含只读审查。
            GateReport report = GateDetector.Inspect(session.Summarize(), new EmptySupportMatrix());
            Assert.Equal(GateLevel.Yellow, report.Level);
            Assert.Contains("只读审查", report.Guidance);
            using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
            var vm = new EditViewModel(session, audit, new SnapshotStore(Path.Combine(projectDir, ".cadagent", "snapshots")),
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), projectDir);
            byte[] fileBefore = await File.ReadAllBytesAsync(dxf);
            int countBefore = session.Doc.Entities.Count;

            EditResult r = await vm.DrawLineAsync(0, 0, 50, 50);

            Assert.False(r.Ok);
            Assert.Equal("", r.TxId); // 拒绝先于 tx
            Assert.Contains("只读审查", r.Error); // Yellow guidance（拒绝理由）
            Assert.Equal(r.Error, vm.ErrorMessage);
            Assert.Empty(vm.RecentTxIds);
            Assert.Equal(fileBefore, await File.ReadAllBytesAsync(dxf)); // 无执行、无保存
            Assert.Equal(countBefore, session.Doc.Entities.Count);
            List<AuditRecord> records = audit.ReadAll().ToList();
            AuditRecord req = Assert.Single(records, x => x.Entry.Kind == "view-edit");
            AuditRecord rejected = Assert.Single(records, x => x.Entry.Kind == "run-rejected");
            Assert.True(req.Seq < rejected.Seq); // 请求审计先行，拒绝留痕随后
            Assert.Equal("view", rejected.Entry.Source);
            Assert.Equal(session.SourcePath, rejected.Entry.Target);
            Assert.Equal(req.Entry.BeforeHash, rejected.Entry.BeforeHash);
            Assert.Equal(rejected.Entry.BeforeHash, rejected.Entry.AfterHash); // 拒绝不改图
            JsonElement p = Params(rejected);
            Assert.Equal("Yellow", p.GetProperty("level").GetString());
            Assert.Equal(2, p.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.GetProperty("guidance").GetString());
            Assert.DoesNotContain(records, x => x.Entry.Kind == "tx-start");
            Assert.DoesNotContain(records, x => x.Entry.Kind == "step");
            Assert.DoesNotContain(records, x => x.Entry.Kind == "tx-commit");
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dxf);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }
}
