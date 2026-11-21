using System.Text.Json;
using System.Text.Json.Serialization;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Tests;
using CadAgent.View.Services;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 7: review signoff (pure C#, no Avalonia). Real SqliteAuditStore +
/// StepEngine transactions (operator "tester", Draw role) grouped by T6
/// AuditViewModel; SignoffViewModel gates on Role >= Review, prohibits
/// self-review (tx-start Operator == current account, case-insensitive),
/// appends a hash-chained "review-signed" audit record (Source="review",
/// paramsJson={txId,approved,comment}) and upserts the signoff into
/// .cadagent/signoffs.json (atomic tmp+Move persistence, same TxId+Operator
/// replaces). Audit append happens BEFORE persistence: a signoff must never
/// exist without its audit trail.
/// </summary>
public class SignoffTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public SnapshotStore Snapshots { get; }
        public string SignoffPath { get; }
        public string Tx1Id { get; }
        public string Tx2Id { get; }

        public Fixture()
        {
            Dir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) }));
            Session = DrawingSession.Load(DxfPath);
            Audit = new SqliteAuditStore(Path.Combine(Dir, "audit.db"));
            Snapshots = new SnapshotStore(Path.Combine(Dir, "snapshots"));
            SignoffPath = Path.Combine(Dir, "signoffs.json");
            var engine = new StepEngine(Audit, Snapshots,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), HandlerRegistry.All());
            Tx1Id = engine.Run(Plan("p1",
                ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}""")), Session, "s1").TxId;
            Tx2Id = engine.Run(Plan("p2",
                ("add-text", """{"x":1,"y":2,"text":"NEW","height":3.5}""")), Session, "s1").TxId;
        }

        public IReadOnlyList<TxGroup> TxGroups =>
            new AuditViewModel(Audit, Snapshots, Session).Transactions;

        public SignoffViewModel MakeVm(string account = "reviewer", Role role = Role.Review,
            IAuditStore? audit = null) =>
            new(audit ?? Audit, new ReviewSignoffStore(SignoffPath),
                new FixedIdentity(new OperatorIdentity(account, role)),
                new RoleChecker(), TxGroups, "s1");

        public SignoffViewModel MakeVm(ReviewSignoffStore store, string account = "reviewer",
            Role role = Role.Review) =>
            new(Audit, store, new FixedIdentity(new OperatorIdentity(account, role)),
                new RoleChecker(), TxGroups, "s1");

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

    /// <summary>审计写入失败的存储（签认不得先于审计留痕落盘）。</summary>
    private sealed class ThrowingAuditStore : IAuditStore
    {
        public AuditRecord Append(AuditEntry e) => throw new InvalidOperationException("audit down");
        public IReadOnlyList<AuditRecord> ReadAll() => Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error) { error = ""; return true; }
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

    // ---- Role gate + successful sign ----

    [Fact]
    public async Task ReviewRole_Signs_AppendsReviewSignedAudit_ChainValid()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm();
        Assert.True(vm.CanSign);
        Assert.Equal(2, vm.UnsignedTx.Count);

        await vm.SignAsync(f.Tx1Id, approved: true, comment: "looks good");

        ReviewSignoff s = Assert.Single(vm.Signoffs);
        Assert.Equal(f.Tx1Id, s.TxId);
        Assert.Equal("reviewer", s.Operator);
        Assert.Equal(Role.Review, s.Role);
        Assert.True(s.Approved);
        Assert.Equal("looks good", s.Comment);

        AuditRecord last = f.Audit.ReadAll()[^1];
        Assert.Equal("review-signed", last.Entry.Kind);
        Assert.Equal("review", last.Entry.Source);
        Assert.Equal("reviewer", last.Entry.Operator);
        Assert.Equal("Review", last.Entry.Role);
        Assert.Equal("s1", last.Entry.Session);
        Assert.Equal(f.Tx1Id, last.Entry.Target);
        using JsonDocument p = JsonDocument.Parse(last.Entry.ParamsJson);
        Assert.Equal(f.Tx1Id, p.RootElement.GetProperty("txId").GetString());
        Assert.True(p.RootElement.GetProperty("approved").GetBoolean());
        Assert.Equal("looks good", p.RootElement.GetProperty("comment").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);

        // 签认后该 tx 移出未签认列表；另一 tx 仍未签认。
        Assert.DoesNotContain(vm.UnsignedTx, g => g.TxId == f.Tx1Id);
        Assert.Contains(vm.UnsignedTx, g => g.TxId == f.Tx2Id);
        Assert.NotNull(vm.SignoffFor(f.Tx1Id));
        Assert.Null(vm.SignoffFor(f.Tx2Id));
    }

    [Fact]
    public async Task DrawRole_Refused_NoSignoffNoAudit()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm("drawer", Role.Draw);
        Assert.False(vm.CanSign);
        int auditCount = f.Audit.ReadAll().Count;

        await Assert.ThrowsAsync<RoleViolationException>(() => vm.SignAsync(f.Tx1Id, true, "nope"));

        Assert.Empty(vm.Signoffs);
        Assert.Equal(auditCount, f.Audit.ReadAll().Count);
        Assert.False(File.Exists(f.SignoffPath));
    }

    [Fact]
    public void CanSign_ApproveRole_True()
    {
        using var f = new Fixture();
        Assert.True(f.MakeVm("boss", Role.Approve).CanSign);
    }

    // ---- Self-review prohibition ----

    [Fact]
    public async Task SelfReview_Refused_WithMessage()
    {
        using var f = new Fixture();
        // tx 责任人 "tester" 本人即使持 Review 角色也不得自审。
        SignoffViewModel vm = f.MakeVm("tester", Role.Review);
        Assert.True(vm.CanSign);
        int auditCount = f.Audit.ReadAll().Count;

        RoleViolationException ex = await Assert.ThrowsAsync<RoleViolationException>(
            () => vm.SignAsync(f.Tx1Id, true, "self"));
        Assert.Equal("绘制者不能自审", ex.Message);

        Assert.Empty(vm.Signoffs);
        Assert.Equal(auditCount, f.Audit.ReadAll().Count);
        Assert.False(File.Exists(f.SignoffPath));
    }

    [Fact]
    public async Task SelfReview_AccountComparisonIsCaseInsensitive()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm("TESTER", Role.Review);
        RoleViolationException ex = await Assert.ThrowsAsync<RoleViolationException>(
            () => vm.SignAsync(f.Tx1Id, true, ""));
        Assert.Equal("绘制者不能自审", ex.Message);
        Assert.Empty(vm.Signoffs);
    }

    // ---- Refusals / edge cases ----

    [Fact]
    public async Task UnknownTx_Refused_NoAudit()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm();
        int auditCount = f.Audit.ReadAll().Count;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.SignAsync("ffffffffffffffffffffffffffffffff", true, ""));
        Assert.Empty(vm.Signoffs);
        Assert.Equal(auditCount, f.Audit.ReadAll().Count);
    }

    [Fact]
    public async Task AuditFailure_NoSignoffPersisted()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm(audit: new ThrowingAuditStore());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SignAsync(f.Tx1Id, true, ""));
        Assert.Empty(vm.Signoffs);
        Assert.False(File.Exists(f.SignoffPath));
    }

    [Fact]
    public async Task Reject_SignsApprovedFalse_AuditParamsMatch()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm();
        await vm.SignAsync(f.Tx1Id, approved: false, comment: "wrong layer");

        ReviewSignoff s = Assert.Single(vm.Signoffs);
        Assert.False(s.Approved);
        Assert.Equal("wrong layer", s.Comment);
        AuditRecord last = f.Audit.ReadAll()[^1];
        Assert.Equal("review-signed", last.Entry.Kind);
        using JsonDocument p = JsonDocument.Parse(last.Entry.ParamsJson);
        Assert.False(p.RootElement.GetProperty("approved").GetBoolean());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task SignAsync_FiresSignoffsChanged()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm();
        int fired = 0;
        vm.SignoffsChanged += () => fired++;
        await vm.SignAsync(f.Tx1Id, true, "");
        Assert.Equal(1, fired);
    }

    // ---- Upsert semantics ----

    [Fact]
    public async Task Upsert_SameTxSameOperator_ReplacesLatestWins()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm();
        await vm.SignAsync(f.Tx1Id, approved: false, comment: "first");
        await vm.SignAsync(f.Tx1Id, approved: true, comment: "second");

        ReviewSignoff s = Assert.Single(vm.Signoffs);
        Assert.True(s.Approved);
        Assert.Equal("second", s.Comment);
        // 两次签认均留审计痕迹（覆盖签认不抹历史）。
        Assert.Equal(2, f.Audit.ReadAll().Count(r => r.Entry.Kind == "review-signed"));
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Upsert_DifferentOperatorSameTx_KeepsBoth()
    {
        using var f = new Fixture();
        await f.MakeVm("reviewer", Role.Review).SignAsync(f.Tx1Id, true, "r");
        SignoffViewModel vm2 = f.MakeVm("boss", Role.Approve);
        await vm2.SignAsync(f.Tx1Id, true, "a");

        Assert.Equal(2, vm2.Signoffs.Count);
        Assert.Contains(vm2.Signoffs, s => s.Operator == "reviewer");
        Assert.Contains(vm2.Signoffs, s => s.Operator == "boss");
        Assert.Empty(vm2.UnsignedTx.Where(g => g.TxId == f.Tx1Id));
        // SignoffFor 返回最新一条。
        Assert.Equal("boss", vm2.SignoffFor(f.Tx1Id)!.Operator);
    }

    // ---- SignoffFor: time-latest (max Ts), not list position ----

    [Fact]
    public void SignoffFor_MultiSigner_ReturnsMaxTs_NotListPosition()
    {
        using var f = new Fixture();
        var store = new ReviewSignoffStore(f.SignoffPath);
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        // reviewer 的 Ts 更新但位于列表前部；boss 的 Ts 更旧却位于列表末位。
        store.Upsert(new ReviewSignoff(f.Tx1Id, "reviewer", Role.Review, true, "newer", ts.AddMinutes(2)));
        store.Upsert(new ReviewSignoff(f.Tx1Id, "boss", Role.Approve, false, "older", ts));
        SignoffViewModel vm = f.MakeVm(store);

        ReviewSignoff s = vm.SignoffFor(f.Tx1Id)!;
        Assert.Equal("reviewer", s.Operator);
        Assert.Equal("newer", s.Comment);
        Assert.True(s.Approved);
    }

    [Fact]
    public void SignoffFor_ResignUpsertInPlace_StillReturnsTimeLatest()
    {
        using var f = new Fixture();
        var store = new ReviewSignoffStore(f.SignoffPath);
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        // reviewer 签 → boss 签 → reviewer 重签（Upsert 原位替换 index 0，位置不变）。
        store.Upsert(new ReviewSignoff(f.Tx1Id, "reviewer", Role.Review, false, "first", ts));
        store.Upsert(new ReviewSignoff(f.Tx1Id, "boss", Role.Approve, true, "boss", ts.AddMinutes(1)));
        store.Upsert(new ReviewSignoff(f.Tx1Id, "reviewer", Role.Review, true, "resign", ts.AddMinutes(2)));
        SignoffViewModel vm = f.MakeVm(store);

        Assert.Equal(2, store.Items.Count);
        Assert.Equal("reviewer", store.Items[0].Operator); // 原位替换，列表位置未变
        ReviewSignoff s = vm.SignoffFor(f.Tx1Id)!;
        Assert.Equal("reviewer", s.Operator);
        Assert.Equal("resign", s.Comment);
        Assert.True(s.Approved);
    }

    [Fact]
    public void SignoffFor_EqualTs_TieGoesToLastInList()
    {
        using var f = new Fixture();
        var store = new ReviewSignoffStore(f.SignoffPath);
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        store.Upsert(new ReviewSignoff(f.Tx1Id, "reviewer", Role.Review, true, "r", ts));
        store.Upsert(new ReviewSignoff(f.Tx1Id, "boss", Role.Approve, false, "b", ts));
        SignoffViewModel vm = f.MakeVm(store);

        // Ts 相同 → 取列表中靠后的一条（稳定）。
        Assert.Equal("boss", vm.SignoffFor(f.Tx1Id)!.Operator);
    }

    // ---- Store persistence ----

    [Fact]
    public async Task Persistence_Roundtrip_NewStoreReadsSameSignoffs()
    {
        using var f = new Fixture();
        SignoffViewModel vm = f.MakeVm();
        await vm.SignAsync(f.Tx1Id, true, "roundtrip");
        await vm.SignAsync(f.Tx2Id, false, "reject");

        var reloaded = new ReviewSignoffStore(f.SignoffPath);
        Assert.Equal(2, reloaded.Items.Count);
        Assert.Equal(vm.Signoffs, reloaded.Items); // record 值相等（含 Ts 精确往返）
        Assert.False(File.Exists(f.SignoffPath + ".tmp")); // 原子写后无 tmp 残留
    }

    [Fact]
    public void Store_MissingFile_LoadsEmpty_SaveCreatesDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "nested", "signoffs.json");
            var store = new ReviewSignoffStore(path);
            Assert.Empty(store.Items);
            store.Upsert(new ReviewSignoff("tx1", "reviewer", Role.Review, true, "c", DateTimeOffset.UtcNow));
            store.Save();
            Assert.True(File.Exists(path));
            Assert.Single(new ReviewSignoffStore(path).Items);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Store_Load_DedupsSameTxOperator_LastWins()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "signoffs.json");
            var ts = DateTimeOffset.UtcNow;
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            // 磁盘上同 TxId+Operator 两条（历史遗留/手工编辑）→ Load 保留末条。
            string json = JsonSerializer.Serialize(new
            {
                Items = new[]
                {
                    new ReviewSignoff("tx1", "reviewer", Role.Review, false, "old", ts),
                    new ReviewSignoff("tx1", "reviewer", Role.Review, true, "new", ts.AddMinutes(1)),
                    new ReviewSignoff("tx1", "boss", Role.Approve, true, "other", ts)
                }
            }, options);
            File.WriteAllText(path, json);

            var store = new ReviewSignoffStore(path);
            Assert.Equal(2, store.Items.Count);
            Assert.Equal("new", store.Items.Single(s => s.Operator == "reviewer").Comment);
            Assert.True(store.Items.Single(s => s.Operator == "reviewer").Approved);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Store_Load_DedupOperatorIsCaseInsensitive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-dupci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "signoffs.json");
            var ts = DateTimeOffset.UtcNow;
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            // 同 TxId、账号仅大小写不同 → 与 Upsert 同一比较器（OrdinalIgnoreCase），保留末条。
            string json = JsonSerializer.Serialize(new
            {
                Items = new[]
                {
                    new ReviewSignoff("tx1", "reviewer", Role.Review, false, "old", ts),
                    new ReviewSignoff("tx1", "REVIEWER", Role.Review, true, "new", ts.AddMinutes(1))
                }
            }, options);
            File.WriteAllText(path, json);

            var store = new ReviewSignoffStore(path);
            ReviewSignoff s = Assert.Single(store.Items);
            Assert.Equal("REVIEWER", s.Operator); // 末条原样保留（含原始大小写）
            Assert.Equal("new", s.Comment);
            Assert.True(s.Approved);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Store_Upsert_ReplacesInMemory_WithoutSave()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "signoffs.json");
            var store = new ReviewSignoffStore(path);
            var ts = DateTimeOffset.UtcNow;
            store.Upsert(new ReviewSignoff("tx1", "reviewer", Role.Review, false, "old", ts));
            store.Upsert(new ReviewSignoff("tx1", "reviewer", Role.Review, true, "new", ts));
            ReviewSignoff s = Assert.Single(store.Items);
            Assert.True(s.Approved);
            Assert.False(File.Exists(path)); // Upsert 仅内存；Save 才落盘
            store.Save();
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
