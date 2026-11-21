using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 8: SemanticViewModel (pure C#, no Avalonia). Mirrors the CLI
/// semantic scan/map path: SkillSet.Compose(builtin + skill dirs) ->
/// SemanticScanner.Scan(session, existing overlay, MergedAliases) ->
/// overlay.Save(.cadagent/semantic.json) -> audit semantic-scan (Source
/// "semantic", params {annotationCount, ambiguousCount, version,
/// rulePackVersion}, before/after hash = drawing hash). MapAsync ->
/// ConfirmMapping(operator) + ApplyMappingToAnnotations(handles on layer,
/// case-insensitive) -> Save -> audit semantic-map (params {layer, class,
/// operator}). IsolateLayer hides every other canvas layer.
/// Fixture: layer "W-墙体" (outside the convention library, hint 疑似墙体)
/// with 2 entities + known layer "WALL" with 1 entity.
/// </summary>
public class SemanticViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string OverlayPath => Path.Combine(AgentDir, "semantic.json");
        public IReadOnlyList<string> WallLayerHandles { get; }

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-semantic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                var known = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Layers.Add(known);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "房间", Height = 3.5, Layer = wall });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(100, 50, 0), Layer = known });
            });
            Session = DrawingSession.Load(DxfPath);
            WallLayerHandles = Session.Doc.Entities
                .Where(e => e.Layer?.Name == "W-墙体")
                .Select(e => e.Handle.ToString()).ToList();
            Audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db"));
        }

        public SemanticViewModel MakeVm(CanvasViewModel? canvas = null) =>
            new(Session, ProjectDir, canvas, Audit, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));

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

    /// <summary>Skill package with a layer alias (no rules) — mirrors CLI --skill.</summary>
    private static string MakeAliasSkillDir(string layerName, string cls)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-alias-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skill.yaml"),
            "name: alias-skill\nversion: \"1.0.0\"\ndiscipline: architecture\ndrawingType: any\n");
        File.WriteAllText(Path.Combine(dir, "aliases.yaml"),
            $"aliases:\n  - layerName: \"{layerName}\"\n    class: {cls}\n");
        return dir;
    }

    private static JsonElement Params(AuditRecord record) =>
        JsonDocument.Parse(record.Entry.ParamsJson).RootElement;

    // ---- Scan ----

    [Fact]
    public async Task Scan_ProducesAmbiguousList_WithEvidence()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        await vm.ScanAsync(Array.Empty<string>());

        Assert.Null(vm.ErrorMessage);
        AmbiguousLayer a = Assert.Single(vm.Ambiguous);
        Assert.Equal("W-墙体", a.LayerName);
        Assert.Equal(2, a.EntityCount);
        Assert.Equal(f.WallLayerHandles, a.SampleHandles);
        Assert.Equal("疑似墙体", a.NameHint);
        // Convention-library layer WALL is never ambiguous.
        Assert.DoesNotContain(vm.Ambiguous, x => x.LayerName == "WALL");
    }

    [Fact]
    public async Task Scan_PersistsOverlay_AndAuditsSemanticScan_MirroringCli()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        string hashBefore = f.Session.ComputeHash();
        await vm.ScanAsync(Array.Empty<string>());

        Assert.True(File.Exists(f.OverlayPath));
        SemanticOverlay overlay = SemanticOverlay.Load(f.OverlayPath);
        Assert.True(overlay.Version >= 1);
        Assert.Equal(overlay.Version, vm.Version);
        Assert.Equal(overlay.Annotations.Count, vm.AnnotationCount);

        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "semantic-scan");
        Assert.Equal("semantic", rec.Entry.Source);
        Assert.Equal(f.Session.SourcePath, rec.Entry.Target);
        Assert.Equal("tester", rec.Entry.Operator);
        Assert.Equal("Draw", rec.Entry.Role);
        Assert.Equal(hashBefore, rec.Entry.BeforeHash);
        Assert.Equal(hashBefore, rec.Entry.AfterHash); // scan never touches the drawing
        Assert.Equal("", rec.Entry.PlanRef);
        JsonElement p = Params(rec);
        Assert.Equal(overlay.Annotations.Count, p.GetProperty("annotationCount").GetInt32());
        Assert.Equal(vm.Ambiguous.Count, p.GetProperty("ambiguousCount").GetInt32());
        Assert.Equal(overlay.Version, p.GetProperty("version").GetInt32());
        Assert.Equal("gb50001-2017.r1", p.GetProperty("rulePackVersion").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Scan_SkillAlias_ResolvesAmbiguousLayer()
    {
        using var f = new Fixture();
        string skillDir = MakeAliasSkillDir("W-墙体", "Wall");
        try
        {
            SemanticViewModel vm = f.MakeVm();
            await vm.ScanAsync(new[] { skillDir });
            Assert.Null(vm.ErrorMessage);
            Assert.Empty(vm.Ambiguous); // alias hit -> no longer ambiguous
            AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "semantic-scan");
            Assert.Equal("gb50001-2017.r1|alias-skill@1.0.0",
                Params(rec).GetProperty("rulePackVersion").GetString());
        }
        finally { Directory.Delete(skillDir, true); }
    }

    [Fact]
    public async Task Scan_CorruptOverlay_AuditsIntakeRejected_NoScanRecord()
    {
        using var f = new Fixture();
        File.WriteAllText(f.OverlayPath, "{ not json");
        SemanticViewModel vm = f.MakeVm();
        await vm.ScanAsync(Array.Empty<string>());

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.Ambiguous);
        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("semantic", rec.Entry.Source);
        Assert.Equal(f.OverlayPath, rec.Entry.Target);
        Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "semantic-scan");
    }

    [Fact]
    public async Task Scan_RaisesSemanticChanged()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        int raised = 0;
        vm.SemanticChanged += () => raised++;
        await vm.ScanAsync(Array.Empty<string>());
        Assert.Equal(1, raised);
    }

    // ---- Map ----

    [Fact]
    public async Task Map_ConfirmsHumanConfirmed_AuditsAndPersists()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        await vm.ScanAsync(Array.Empty<string>());
        int versionAfterScan = vm.Version;
        string hashBefore = f.Session.ComputeHash();

        await vm.MapAsync("W-墙体", SemanticClass.Wall);

        Assert.Null(vm.ErrorMessage);
        SemanticOverlay overlay = SemanticOverlay.Load(f.OverlayPath);
        LayerMapping mapping = Assert.Single(overlay.Mappings, m => m.LayerName == "W-墙体");
        Assert.Equal(SemanticClass.Wall, mapping.Class);
        Assert.Equal("tester", mapping.ConfirmedBy);
        foreach (string handle in f.WallLayerHandles)
        {
            SemanticAnnotation a = Assert.Single(overlay.Annotations, x => x.TargetHandle == handle);
            Assert.Equal(SemanticClass.Wall, a.Class);
            Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
            Assert.Equal(1.0, a.Confidence);
        }
        Assert.True(overlay.Version > versionAfterScan);
        Assert.Equal(overlay.Version, vm.Version);
        // Mapped layer leaves the ambiguous list.
        Assert.DoesNotContain(vm.Ambiguous, a => a.LayerName == "W-墙体");

        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "semantic-map");
        Assert.Equal("semantic", rec.Entry.Source);
        Assert.Equal("W-墙体", rec.Entry.Target);
        Assert.Equal(hashBefore, rec.Entry.BeforeHash);
        Assert.Equal(hashBefore, rec.Entry.AfterHash); // map only changes the overlay
        JsonElement p = Params(rec);
        Assert.Equal("W-墙体", p.GetProperty("layer").GetString());
        Assert.Equal("Wall", p.GetProperty("class").GetString());
        Assert.Equal("tester", p.GetProperty("operator").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Map_LayerMatchIsCaseInsensitive()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        await vm.ScanAsync(Array.Empty<string>());
        await vm.MapAsync("w-墙体", SemanticClass.Annotation);

        Assert.Null(vm.ErrorMessage);
        SemanticOverlay overlay = SemanticOverlay.Load(f.OverlayPath);
        foreach (string handle in f.WallLayerHandles)
            Assert.Equal(SemanticClass.Annotation,
                overlay.Annotations.Single(x => x.TargetHandle == handle).Class);
    }

    [Fact]
    public async Task Map_WithoutOverlay_Fails_NoAuditNoFile()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        await vm.MapAsync("W-墙体", SemanticClass.Wall);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("未找到语义叠加层", vm.ErrorMessage);
        Assert.False(File.Exists(f.OverlayPath));
        Assert.Empty(f.Audit.ReadAll());
    }

    [Fact]
    public async Task Map_RaisesSemanticChanged()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        await vm.ScanAsync(Array.Empty<string>());
        int raised = 0;
        vm.SemanticChanged += () => raised++;
        await vm.MapAsync("W-墙体", SemanticClass.Wall);
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// Review fix (Important): concurrent overlay read-modify-write must be
    /// serialized. Two MapAsync on different layers race Load->mutate->Save:
    /// without a gate both load the same version and the last writer silently
    /// drops the other's LayerMapping/HumanConfirmed while BOTH audit records
    /// claim success (atomic Save prevents corruption, not lost updates).
    /// Post-fix determinism: the gate makes the second Load see the first
    /// Save, so Version advances exactly twice and both mappings survive.
    /// </summary>
    [Fact]
    public async Task Map_ConcurrentTwoLayers_NoLostUpdate_BothAudited_ChainValid()
    {
        using var f = new Fixture();
        SemanticViewModel vm = f.MakeVm();
        await vm.ScanAsync(Array.Empty<string>());
        int versionAfterScan = vm.Version;

        await Task.WhenAll(
            vm.MapAsync("W-墙体", SemanticClass.Wall),
            vm.MapAsync("WALL", SemanticClass.Wall));

        Assert.Null(vm.ErrorMessage);
        SemanticOverlay overlay = SemanticOverlay.Load(f.OverlayPath);
        // BOTH human confirmations present — no lost update in either direction.
        LayerMapping wall = Assert.Single(overlay.Mappings, m => m.LayerName == "W-墙体");
        Assert.Equal(SemanticClass.Wall, wall.Class);
        Assert.Equal("tester", wall.ConfirmedBy);
        LayerMapping known = Assert.Single(overlay.Mappings, m => m.LayerName == "WALL");
        Assert.Equal(SemanticClass.Wall, known.Class);
        Assert.Equal("tester", known.ConfirmedBy);
        foreach (string handle in f.Session.Doc.Entities.Select(e => e.Handle.ToString()))
        {
            SemanticAnnotation a = Assert.Single(overlay.Annotations, x => x.TargetHandle == handle);
            Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
            Assert.Equal(1.0, a.Confidence);
            Assert.Equal(SemanticClass.Wall, a.Class);
        }
        // Serialized RMW: each Save sees the prior one -> exactly +2 versions.
        Assert.Equal(versionAfterScan + 2, overlay.Version);
        Assert.Equal(overlay.Version, vm.Version);
        Assert.Empty(vm.Ambiguous);

        List<AuditRecord> maps = f.Audit.ReadAll().Where(r => r.Entry.Kind == "semantic-map").ToList();
        Assert.Equal(2, maps.Count);
        Assert.Contains(maps, r => r.Entry.Target == "W-墙体");
        Assert.Contains(maps, r => r.Entry.Target == "WALL");
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- Isolate ----

    [Fact]
    public void Isolate_HidesAllOtherLayers()
    {
        using var f = new Fixture();
        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(f.Session);
        var canvas = new CanvasViewModel(items);
        SemanticViewModel vm = f.MakeVm(canvas);

        vm.IsolateLayer("W-墙体");
        Assert.Contains("WALL", canvas.HiddenLayers);
        Assert.DoesNotContain("W-墙体", canvas.HiddenLayers);
        Assert.All(canvas.VisibleItems, i => Assert.Equal("W-墙体", i.Layer));

        vm.IsolateLayer("WALL");
        Assert.Contains("W-墙体", canvas.HiddenLayers);
        Assert.DoesNotContain("WALL", canvas.HiddenLayers);
        Assert.All(canvas.VisibleItems, i => Assert.Equal("WALL", i.Layer));
    }

    [Fact]
    public void Isolate_UnknownLayerOrNoCanvas_IsNoOp()
    {
        using var f = new Fixture();
        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(f.Session);
        var canvas = new CanvasViewModel(items);
        SemanticViewModel vm = f.MakeVm(canvas);
        vm.IsolateLayer("NO-SUCH-LAYER");
        Assert.Empty(canvas.HiddenLayers);

        SemanticViewModel noCanvas = f.MakeVm();
        noCanvas.IsolateLayer("W-墙体"); // must not throw
    }
}

/// <summary>
/// Plan 4 Task 8: AdjudicationViewModel (pure C#, no Avalonia). Mirrors the CLI
/// rule adjudicate path: validation order mutual-exclusion -> force/exempt
/// required -> reason required -> force legality -> orphan ruleId (fresh
/// SkillSet compose, builtin + skill dirs) — all refusals throw BEFORE any
/// audit/persistence side effect. Success: AdjudicationSet.Load -> Upsert ->
/// audit rule-adjudicated (Source "rule", params {ruleId, forceOverride,
/// exempt, reason}, empty hashes) -> Save (audit-first, CLI order).
/// AdjudicationsChanged links to IssuesViewModel.RefreshAsync.
/// </summary>
public class AdjudicationViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string AdjudicationsPath => Path.Combine(AgentDir, "adjudications.json");

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-adjudication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            });
            Session = DrawingSession.Load(DxfPath);
            Audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db"));
        }

        public AdjudicationViewModel MakeVm(params string[] skillDirs) =>
            new(ProjectDir, skillDirs, Audit, new FixedIdentity(new OperatorIdentity("tester", Role.Review)));

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

    private static string MakeSkillDir(string name, string ruleId)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-adj-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        File.WriteAllText(Path.Combine(dir, "skill.yaml"),
            $"name: {name}\nversion: \"1.0.0\"\ndiscipline: architecture\ndrawingType: any\n");
        File.WriteAllText(Path.Combine(dir, "rules", "r1.yaml"), $$"""
            id: {{ruleId}}
            source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
            modal: "应"
            force: recommended
            severity: high
            scope: { discipline: any, drawingType: any }
            check: { type: text-height-series, params: { hanVector: [3.5, 5, 7, 10, 14, 20], other: [3, 4, 6, 8, 10, 14, 20] } }
            suggestion: "测试建议"
            """);
        return dir;
    }

    private static JsonElement Params(AuditRecord record) =>
        JsonDocument.Parse(record.Entry.ParamsJson).RootElement;

    // ---- Rules + current adjudications ----

    [Fact]
    public void Ctor_LoadsBuiltinRules_AndEmptyAdjudications()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(7, vm.Rules.Count);
        Assert.Contains(vm.Rules, r => r.Id == "R-50001-5.0.2");
        Assert.Empty(vm.Adjudications);
    }

    [Fact]
    public void Ctor_WithSkillDir_RulesIncludeSkillRules()
    {
        using var f = new Fixture();
        string skillDir = MakeSkillDir("test-skill", "R-TEST-1");
        try
        {
            AdjudicationViewModel vm = f.MakeVm(skillDir);
            Assert.Null(vm.ErrorMessage);
            Assert.Contains(vm.Rules, r => r.Id == "R-TEST-1");
        }
        finally { Directory.Delete(skillDir, true); }
    }

    [Fact]
    public void Ctor_CorruptAdjudications_SurfacesErrorMessage()
    {
        using var f = new Fixture();
        File.WriteAllText(f.AdjudicationsPath, "{ not json");
        AdjudicationViewModel vm = f.MakeVm();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.Adjudications);
    }

    // ---- Upgrade / exempt success paths ----

    [Fact]
    public async Task Adjudicate_Upgrade_PersistsMandatory_AndAudits()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        await vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Mandatory, false, "测试升级");

        Adjudication a = Assert.Single(vm.Adjudications);
        Assert.Equal("R-50001-5.0.2", a.RuleId);
        Assert.Equal("tester", a.Operator);
        Assert.Equal(RuleForce.Mandatory, a.ForceOverride);
        Assert.False(a.Exempt);
        Assert.Equal("测试升级", a.Reason);

        Assert.True(File.Exists(f.AdjudicationsPath));
        AdjudicationSet reloaded = AdjudicationSet.Load(f.AdjudicationsPath);
        Rule rule = vm.Rules.Single(r => r.Id == "R-50001-5.0.2");
        Assert.Equal(RuleForce.Mandatory, reloaded.EffectiveForce(rule));

        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == AdjudicationSet.AuditKind);
        Assert.Equal("rule-adjudicated", rec.Entry.Kind);
        Assert.Equal("rule", rec.Entry.Source);
        Assert.Equal("R-50001-5.0.2", rec.Entry.Target);
        Assert.Equal("tester", rec.Entry.Operator);
        Assert.Equal("", rec.Entry.BeforeHash);
        Assert.Equal("", rec.Entry.AfterHash);
        JsonElement p = Params(rec);
        Assert.Equal("R-50001-5.0.2", p.GetProperty("ruleId").GetString());
        Assert.Equal("Mandatory", p.GetProperty("forceOverride").GetString());
        Assert.False(p.GetProperty("exempt").GetBoolean());
        Assert.Equal("测试升级", p.GetProperty("reason").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task Adjudicate_Interpretation_Persisted()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        await vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Informational, false, "降级", "本图按方案图深度");
        Assert.Equal("本图按方案图深度", vm.Adjudications.Single().Interpretation);
        Assert.Equal("本图按方案图深度",
            AdjudicationSet.Load(f.AdjudicationsPath).Items.Single().Interpretation);
    }

    [Fact]
    public async Task Adjudicate_UpsertSameRule_LatestWins_BothAudited()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        await vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Mandatory, false, "升级");
        await vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Informational, false, "降级");

        Adjudication a = Assert.Single(vm.Adjudications);
        Assert.Equal(RuleForce.Informational, a.ForceOverride);
        Assert.Equal("降级", a.Reason);
        Assert.Equal(2, f.Audit.ReadAll().Count(r => r.Entry.Kind == "rule-adjudicated"));
    }

    [Fact]
    public async Task Adjudicate_Exempt_IssueGoneAfterIssuesRefresh()
    {
        using var f = new Fixture();
        var issues = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>());
        await issues.RefreshAsync();
        Assert.Equal(2, issues.Issues.Count);

        AdjudicationViewModel vm = f.MakeVm();
        await vm.AdjudicateAsync("R-50001-13.0.1", null, true, "本图类不适用");
        await issues.RefreshAsync();

        Assert.DoesNotContain(issues.Issues, i => i.RuleId == "R-50001-13.0.1");
        Assert.Single(issues.Issues);
        Assert.Contains(issues.SkippedReasons,
            s => s.Category == SkipCategories.Exempt && s.RuleId == "R-50001-13.0.1");
        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adjudicated");
        Assert.True(Params(rec).GetProperty("exempt").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Params(rec).GetProperty("forceOverride").ValueKind);
    }

    [Fact]
    public async Task Adjudicate_RaisesAdjudicationsChanged_LinkedToIssuesRefresh()
    {
        using var f = new Fixture();
        var issues = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>());
        await issues.RefreshAsync();
        Assert.Equal(2, issues.Issues.Count);

        AdjudicationViewModel vm = f.MakeVm();
        var refreshed = new TaskCompletionSource();
        // The MainWindow linkage: event -> IssuesViewModel.RefreshAsync.
        vm.AdjudicationsChanged += async () =>
        {
            await issues.RefreshAsync();
            refreshed.TrySetResult();
        };
        await vm.AdjudicateAsync("R-50001-13.0.1", null, true, "豁免");
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(issues.Issues); // exempted issue gone without a manual refresh
    }

    [Fact]
    public async Task Adjudicate_SkillRule_Accepted_OrphanWithoutSkillDir()
    {
        using var f = new Fixture();
        string skillDir = MakeSkillDir("test-skill", "R-TEST-1");
        try
        {
            AdjudicationViewModel withSkill = f.MakeVm(skillDir);
            await withSkill.AdjudicateAsync("R-TEST-1", RuleForce.Mandatory, false, "院标升级");
            Assert.Equal(RuleForce.Mandatory, withSkill.Adjudications.Single().ForceOverride);

            AdjudicationViewModel builtinOnly = f.MakeVm();
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => builtinOnly.AdjudicateAsync("R-TEST-1", RuleForce.Mandatory, false, "孤儿"));
            Assert.Contains("未知规则 Id: R-TEST-1", ex.Message);
        }
        finally { Directory.Delete(skillDir, true); }
    }

    // ---- Validation parity with CLI (refusals: no audit, no file) ----

    [Fact]
    public async Task Adjudicate_OrphanRule_Refused_NoSideEffects()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.AdjudicateAsync("R-NOPE", RuleForce.Mandatory, false, "理由"));
        Assert.Contains("未知规则 Id: R-NOPE", ex.Message);
        Assert.Contains("拒绝孤儿裁定", ex.Message);
        Assert.False(File.Exists(f.AdjudicationsPath));
        Assert.Empty(f.Audit.ReadAll());
        Assert.Empty(vm.Adjudications);
    }

    [Fact]
    public async Task Adjudicate_ForceAndExempt_MutuallyExclusive_Refused()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Mandatory, true, "理由"));
        Assert.Contains("--force 与 --exempt 互斥", ex.Message);
        Assert.False(File.Exists(f.AdjudicationsPath));
        Assert.Empty(f.Audit.ReadAll());
    }

    [Fact]
    public async Task Adjudicate_NeitherForceNorExempt_Refused()
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.AdjudicateAsync("R-50001-5.0.2", null, false, "理由"));
        Assert.Contains("需要 --force", ex.Message);
        Assert.False(File.Exists(f.AdjudicationsPath));
        Assert.Empty(f.Audit.ReadAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Adjudicate_MissingReason_Refused(string reason)
    {
        using var f = new Fixture();
        AdjudicationViewModel vm = f.MakeVm();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Mandatory, false, reason));
        Assert.Contains("需要 --reason", ex.Message);
        Assert.False(File.Exists(f.AdjudicationsPath));
        Assert.Empty(f.Audit.ReadAll());
    }

    [Fact]
    public async Task Adjudicate_CorruptAdjudications_AuditsIntakeRejected()
    {
        using var f = new Fixture();
        File.WriteAllText(f.AdjudicationsPath, "{ not json");
        AdjudicationViewModel vm = f.MakeVm();
        await Assert.ThrowsAsync<JsonException>(
            () => vm.AdjudicateAsync("R-50001-5.0.2", RuleForce.Mandatory, false, "理由"));
        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("rule", rec.Entry.Source);
        Assert.Equal(f.AdjudicationsPath, rec.Entry.Target);
        Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adjudicated");
    }
}
