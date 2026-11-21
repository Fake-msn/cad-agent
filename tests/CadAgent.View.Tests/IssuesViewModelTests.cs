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
/// Plan 4 Task 5: IssuesViewModel (pure C#, no Avalonia). In-process standards
/// check mirroring the CLI check path: BuiltinRules + SkillSet.Compose (id
/// collision -> ErrorMessage) + AdjudicationSet.Load(.cadagent/adjudications.json)
/// + SemanticOverlay.Load(.cadagent/semantic.json when present) -> RuleEngine
/// with ALL checkers registered (same list as CLI StandardsEngine).
/// Fixture: text height 4.2 (violates 5.0.2 series) on mixed-charset layer
/// "W-墙体" (violates 13.0.1) -> exactly 2 recommended issues with GB/T 50001
/// clause citations; 3 overlay-dependent rules skipped NotEvaluable.
/// LocateIssue -> canvas SelectHandles(Targets) + CenterOn(Targets).
/// </summary>
public class IssuesViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string TextHandle => Session.Doc.Entities.OfType<TextEntity>().First().Handle.ToString();
        public string LayerHandle => Session.Doc.Layers.First(l => l.Name == "W-墙体").Handle.ToString();

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-issues-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ProjectDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            });
            Session = DrawingSession.Load(DxfPath);
        }

        public IssuesViewModel MakeVm(CanvasViewModel? canvas = null, params string[] skillDirs) =>
            new(Session, ProjectDir, skillDirs, canvas);

        public void SaveAdjudication(Adjudication a)
        {
            var set = AdjudicationSet.Load(Path.Combine(AgentDir, "adjudications.json"));
            set.Upsert(a);
            set.Save(Path.Combine(AgentDir, "adjudications.json"));
        }

        public void Dispose()
        {
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }

    private static string MakeSkillDir(string name, string ruleId, string force = "recommended",
        string hanVector = "[3.5, 5, 7, 10, 14, 20]", string other = "[3, 4, 6, 8, 10, 14, 20]")
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        File.WriteAllText(Path.Combine(dir, "skill.yaml"),
            $"name: {name}\nversion: \"1.0.0\"\ndiscipline: architecture\ndrawingType: any\n");
        File.WriteAllText(Path.Combine(dir, "rules", "r1.yaml"), $$"""
            id: {{ruleId}}
            source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
            modal: "应"
            force: {{force}}
            severity: high
            scope: { discipline: any, drawingType: any }
            check: { type: text-height-series, params: { hanVector: {{hanVector}}, other: {{other}} } }
            suggestion: "测试建议"
            """);
        return dir;
    }

    // ---- Refresh: issues + citations ----

    [Fact]
    public async Task Refresh_Violations_ProduceIssues_WithClauseCitations()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Issues.Count);

        IssueEntry height = vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        Assert.Equal("GB/T 50001-2017", height.Standard);
        Assert.Equal("5.0.2", height.Clause);
        Assert.Contains("字高", height.Excerpt);
        Assert.Equal(RuleForce.Recommended, height.Force);
        Assert.Equal(RuleSeverity.High, height.Severity);
        Assert.Contains("4.2", height.Message);
        Assert.Equal(new[] { f.TextHandle }, height.Targets);
        Assert.NotEmpty(height.Suggestion);
        Assert.NotEmpty(height.DetectionBasis);

        IssueEntry layer = vm.Issues.Single(i => i.RuleId == "R-50001-13.0.1");
        Assert.Equal("GB/T 50001-2017", layer.Standard);
        Assert.Equal("13.0.1", layer.Clause);
        Assert.Contains("W-墙体", layer.Message);
        Assert.Equal(new[] { f.LayerHandle }, layer.Targets);

        // Citation fields complete on every entry.
        Assert.All(vm.Issues, i =>
        {
            Assert.NotEmpty(i.Standard);
            Assert.NotEmpty(i.Clause);
            Assert.NotEmpty(i.Excerpt);
            Assert.NotEmpty(i.Suggestion);
            Assert.NotEmpty(i.DetectionBasis);
        });
    }

    [Fact]
    public async Task Refresh_BuiltinDefaults_MandatoryCountZero()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        Assert.Equal(0, vm.MandatoryCount);
        Assert.All(vm.Issues, i => Assert.Equal(RuleForce.Recommended, i.Force));
    }

    [Fact]
    public async Task Refresh_RaisesIssuesChanged()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        int raised = 0;
        vm.IssuesChanged += () => raised++;
        await vm.RefreshAsync();
        Assert.Equal(1, raised);
    }

    // ---- Adjudication integration ----

    [Fact]
    public async Task Refresh_AdjudicatedMandatory_UpgradesForceAndCount()
    {
        using var f = new Fixture();
        f.SaveAdjudication(new Adjudication("R-50001-5.0.2", "tester", DateTimeOffset.UtcNow,
            "测试升级", ForceOverride: RuleForce.Mandatory));
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();

        Assert.Equal(1, vm.MandatoryCount);
        Assert.Equal(RuleForce.Mandatory, vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2").Force);
        Assert.Equal(RuleForce.Recommended, vm.Issues.Single(i => i.RuleId == "R-50001-13.0.1").Force);
    }

    [Fact]
    public async Task ForceBrush_MapsForceToBadgeColor()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        // Builtin defaults: Recommended -> yellow.
        Assert.All(vm.Issues, i => Assert.Equal("#F9A825", i.ForceBrush));

        // Adjudicated upgrade/downgrade covers the remaining two badge colors.
        f.SaveAdjudication(new Adjudication("R-50001-5.0.2", "tester", DateTimeOffset.UtcNow,
            "测试升级", ForceOverride: RuleForce.Mandatory));
        f.SaveAdjudication(new Adjudication("R-50001-13.0.1", "tester", DateTimeOffset.UtcNow,
            "测试降级", ForceOverride: RuleForce.Informational));
        await vm.RefreshAsync();
        Assert.Equal("#B71C1C", vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2").ForceBrush);
        Assert.Equal("#616161", vm.Issues.Single(i => i.RuleId == "R-50001-13.0.1").ForceBrush);
        Assert.Equal(1, vm.MandatoryCount); // informational downgrade does not count
    }

    [Fact]
    public async Task Refresh_ExemptAdjudication_IssueGone_SkipSurfaced()
    {
        using var f = new Fixture();
        f.SaveAdjudication(new Adjudication("R-50001-13.0.1", "tester", DateTimeOffset.UtcNow,
            "本图类不适用", Exempt: true));
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();

        Assert.DoesNotContain(vm.Issues, i => i.RuleId == "R-50001-13.0.1");
        Assert.Single(vm.Issues);
        Assert.Contains(vm.SkippedReasons,
            s => s.Category == SkipCategories.Exempt && s.RuleId == "R-50001-13.0.1");
        Assert.Equal(4, vm.RulesSkipped); // 3 NotEvaluable + 1 Exempt
    }

    // ---- Skill integration ----

    [Fact]
    public async Task Refresh_SkillIdCollision_SetsErrorMessage()
    {
        using var f = new Fixture();
        string skillDir = MakeSkillDir("test-skill", "R-50001-5.0.2");
        try
        {
            IssuesViewModel vm = f.MakeVm(skillDirs: skillDir);
            await vm.RefreshAsync();
            Assert.NotNull(vm.ErrorMessage);
            Assert.Contains("duplicate rule id 'R-50001-5.0.2'", vm.ErrorMessage);
            Assert.Empty(vm.Issues);
            Assert.Equal(0, vm.MandatoryCount);
        }
        finally { Directory.Delete(skillDir, true); }
    }

    [Fact]
    public async Task Refresh_SkillCollisionFixed_NextRefreshClearsError()
    {
        using var f = new Fixture();
        string skillDir = MakeSkillDir("test-skill", "R-50001-5.0.2");
        try
        {
            IssuesViewModel vm = f.MakeVm(skillDirs: skillDir);
            await vm.RefreshAsync();
            Assert.NotNull(vm.ErrorMessage);

            File.Delete(Path.Combine(skillDir, "rules", "r1.yaml"));
            await vm.RefreshAsync();
            Assert.Null(vm.ErrorMessage);
            Assert.Equal(2, vm.Issues.Count);
        }
        finally { Directory.Delete(skillDir, true); }
    }

    [Fact]
    public async Task Refresh_ValidSkillPackage_RuleEvaluatedWithRegisteredCheckers()
    {
        using var f = new Fixture();
        // Narrow series [3.5]/[3]: the 4.2mm text violates the skill rule too;
        // force mandatory proves effective-force overwrite applies to skill rules.
        string skillDir = MakeSkillDir("test-skill", "R-TEST-1", force: "mandatory",
            hanVector: "[3.5]", other: "[3]");
        try
        {
            IssuesViewModel vm = f.MakeVm(skillDirs: skillDir);
            await vm.RefreshAsync();
            Assert.Null(vm.ErrorMessage);
            IssueEntry skillIssue = vm.Issues.Single(i => i.RuleId == "R-TEST-1");
            Assert.Equal(RuleForce.Mandatory, skillIssue.Force);
            Assert.Equal("院标", skillIssue.Standard);
            Assert.Equal(new[] { f.TextHandle }, skillIssue.Targets);
            Assert.Equal(1, vm.MandatoryCount);
        }
        finally { Directory.Delete(skillDir, true); }
    }

    // ---- Skips + overlay integration ----

    [Fact]
    public async Task Refresh_NoOverlay_NotEvaluableSkipsSurfaced()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();

        Assert.Equal(3, vm.RulesSkipped);
        Assert.All(vm.SkippedReasons, s => Assert.Equal(SkipCategories.NotEvaluable, s.Category));
        Assert.Equal(
            new[] { "R-50001-11.8.4", "R-50001-11.8.5", "R-50001-8.0.4" },
            vm.SkippedReasons.Select(s => s.RuleId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Refresh_WithOverlay_OverlayRulesEvaluated()
    {
        using var f = new Fixture();
        // Second text "i" (lowercase axis letter, valid 3.5mm height) annotated Axis
        // in .cadagent/semantic.json -> 8.0.4 evaluated and violated; no NotEvaluable.
        string axisPath = DxfFixture.CreateTempDxf(doc =>
        {
            var wall = new Layer("W-墙体");
            doc.Layers.Add(wall);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(50, 50, 0), Value = "i", Height = 3.5, Layer = wall });
        });
        try
        {
            var session = DrawingSession.Load(axisPath);
            string axisHandle = session.Doc.Entities.OfType<TextEntity>().First(t => t.Value == "i").Handle.ToString();
            var overlay = new SemanticOverlay();
            overlay.UpsertAnnotation(new SemanticAnnotation(axisHandle, SemanticClass.Axis,
                Provenance.HumanConfirmed, 0.9, "测试标注"));
            overlay.Save(Path.Combine(f.AgentDir, "semantic.json"));

            var vm = new IssuesViewModel(session, f.ProjectDir, Array.Empty<string>());
            await vm.RefreshAsync();

            Assert.Equal(0, vm.RulesSkipped);
            Assert.Empty(vm.SkippedReasons);
            IssueEntry axis = vm.Issues.Single(i => i.RuleId == "R-50001-8.0.4");
            Assert.Equal(new[] { axisHandle }, axis.Targets);
            Assert.Contains("Axis", axis.DetectionBasis);
        }
        finally { File.Delete(axisPath); }
    }

    // ---- LocateIssue ----

    [Fact]
    public async Task LocateIssue_HighlightsTargetHandles_AndCentersView()
    {
        using var f = new Fixture();
        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(f.Session);
        var canvas = new CanvasViewModel(items);
        canvas.Fit(RenderModelBuilder.Bounds(items), 1000, 500);
        IssuesViewModel vm = f.MakeVm(canvas);
        await vm.RefreshAsync();

        IssueEntry height = vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        vm.LocateIssue(height);

        Assert.Contains(f.TextHandle, canvas.HighlightedHandles);
        Assert.Equal(f.TextHandle, canvas.SelectedHandle);
        // Text bounds = insertion point (10,10) -> centered in the 1000x500 viewport.
        (double sx, double sy) = canvas.WorldToScreen(10, 10);
        Assert.Equal(500, sx, 6);
        Assert.Equal(250, sy, 6);
    }

    [Fact]
    public async Task LocateIssue_NoMatchingRenderItems_KeepsView()
    {
        using var f = new Fixture();
        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(f.Session);
        var canvas = new CanvasViewModel(items);
        canvas.Fit(RenderModelBuilder.Bounds(items), 1000, 500);
        IssuesViewModel vm = f.MakeVm(canvas);
        await vm.RefreshAsync();

        // Layer-name issues target the LAYER handle — no render item carries it.
        IssueEntry layer = vm.Issues.Single(i => i.RuleId == "R-50001-13.0.1");
        (double X, double Y) panBefore = canvas.Pan;
        vm.LocateIssue(layer);
        Assert.Equal(panBefore, canvas.Pan);
        Assert.Null(canvas.SelectedHandle);
    }

    [Fact]
    public async Task LocateIssue_WithoutCanvas_IsNoOp()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        vm.LocateIssue(vm.Issues[0]); // no canvas wired — must not throw
    }
}
