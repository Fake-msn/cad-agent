using System.Collections.Concurrent;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Rules;
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

    // ---- 视图闭环 T6：check-run 审计 parity + in-flight 硬闸 + 审计失败降级 ----

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    /// <summary>审计写入必抛的 fake（模拟 audit.db 损坏/磁盘故障）。</summary>
    private sealed class ThrowingAuditStore : IAuditStore
    {
        public AuditRecord Append(AuditEntry e) => throw new InvalidOperationException("模拟审计库写入失败");
        public IReadOnlyList<AuditRecord> ReadAll() => Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error) { error = ""; return true; }
    }

    [Fact]
    public async Task RefreshAsync_WritesCheckRunAudit_MirroringCliShape()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.AgentDir);
        var audit = new SqliteAuditStore(Path.Combine(f.AgentDir, "audit.db"));
        try
        {
            var vm = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>(),
                audit: audit, identity: new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
            await vm.RefreshAsync();
            Assert.Null(vm.ErrorMessage);
            Assert.Equal(2, vm.Issues.Count);

            // 审计形状逐字镜像 CLI check（CommandRouter）：Source/Kind/target/params 字段。
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "check-run");
            AuditEntry e = rec.Entry;
            Assert.Equal("view-check", e.Source);
            Assert.Equal("view", e.Session); // IssuesVM 无 CLI session 注入 → "view" 常量口径
            Assert.Equal("tester", e.Operator);
            Assert.Equal("Draw", e.Role);
            Assert.Equal(f.Session.SourcePath, e.Target);
            // 只读检查前后哈希一致（镜像 CLI check）。
            Assert.Equal(e.BeforeHash, e.AfterHash);
            Assert.Equal(f.Session.ComputeHash(), e.AfterHash);
            Assert.Equal("", e.SnapshotRef);
            Assert.Equal("", e.PlanRef);

            using var doc = JsonDocument.Parse(e.ParamsJson);
            JsonElement p = doc.RootElement;
            Assert.True(p.GetProperty("rulesRun").GetInt32() > 0);
            Assert.Equal(3, p.GetProperty("rulesSkipped").GetInt32());
            Assert.Equal(2, p.GetProperty("issueCount").GetInt32());
            Assert.Equal(0, p.GetProperty("mandatoryViolations").GetInt32());
            Assert.False(p.GetProperty("overlayPresent").GetBoolean());
            // skippedReasons 字段名逐字镜像 CLI StructuredSkipAuditArray：[{category, ruleId, message}]。
            JsonElement skips = p.GetProperty("skippedReasons");
            Assert.Equal(3, skips.GetArrayLength());
            Assert.All(skips.EnumerateArray(), s =>
            {
                Assert.Equal(SkipCategories.NotEvaluable, s.GetProperty("category").GetString());
                Assert.False(string.IsNullOrEmpty(s.GetProperty("ruleId").GetString()));
                Assert.False(string.IsNullOrEmpty(s.GetProperty("message").GetString()));
            });
            // 复现证据字段与 CLI 同口径：裁定集 Fingerprint + SkillSet.RulePackVersion。
            var expectedAdj = AdjudicationSet.Load(Path.Combine(f.AgentDir, "adjudications.json"));
            Assert.Equal(expectedAdj.Fingerprint(), p.GetProperty("adjudicationHash").GetString());
            var expectedRuleSet = SkillSet.Compose(BuiltinRules.Load(), Array.Empty<SkillPackage>());
            Assert.Equal(expectedRuleSet.RulePackVersion, p.GetProperty("rulePackVersion").GetString());
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            audit.Dispose();
            // Microsoft.Data.Sqlite 连接池持有文件句柄，Windows 下删临时目录前须清池。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCalls_SecondReturnsImmediately()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.AgentDir);
        var audit = new SqliteAuditStore(Path.Combine(f.AgentDir, "audit.db"));
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            var vm = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>(),
                sessionGate: gate, audit: audit,
                identity: new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
            int raised = 0;
            vm.IssuesChanged += () => raised++;

            // 注入会话闸并持闸（镜像 MainWindowHeadlessTests 持闸先例）：
            // refresh1 确定性阻塞在 Evaluate 的 sessionGate 上。
            await gate.WaitAsync();
            Task refresh1 = vm.RefreshAsync();
            Task refresh2 = Task.CompletedTask;
            try
            {
                refresh2 = vm.RefreshAsync();
                // in-flight 硬闸结构性（WaitAsync(0)）：第二次立即返回，不依赖按钮 IsEnabled。
                await refresh2.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(refresh1.IsCompleted);
                Assert.Equal(0, raised); // 第二次未评估、未发布
            }
            finally
            {
                gate.Release();
                await Task.WhenAll(refresh1, refresh2).WaitAsync(TimeSpan.FromSeconds(15));
            }

            Assert.Null(vm.ErrorMessage);
            Assert.Equal(1, raised);
            // 审计 check-run 恰 1 条（第二次未评估）。
            Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "check-run");
        }
        finally
        {
            audit.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshAsync_AuditWriteFails_ErrorMessageNoCrash()
    {
        using var f = new Fixture();
        var vm = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>(),
            audit: new ThrowingAuditStore(),
            identity: new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        await vm.RefreshAsync(); // 不抛（不崩 UI）
        // 审计先行（spec §9）：检查审计写入失败=检查失败（镜像 CLI check exit 1 语义）。
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("审计", vm.ErrorMessage);
        Assert.Empty(vm.Issues);
        Assert.Equal(0, vm.MandatoryCount);
    }

    [Fact]
    public async Task RefreshAsync_NoAuditInjected_BehaviorUnchanged()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm(); // 不传 audit（既有测试口径）
        await vm.RefreshAsync();
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Issues.Count);
        // 无审计注入 → 不建库、不留痕，评估照常。
        Assert.False(File.Exists(Path.Combine(f.AgentDir, "audit.db")));
    }

    // ---- 合并前 ISTQB I2：Evaluate 失败路径审计 parity（spec §9 观察性检查故障也入审计） ----
    // 镜像 EditViewModel.RunRegressionCheck 失败路径口径：check-run + params{Error,overlayPresent}
    // 字段逐字一致；前后哈希空串（故障时口径，与 view-regression 失败审计同款）；链可验。

    [Fact]
    public async Task RefreshAsync_CorruptAdjudications_WritesFailedCheckRunAudit()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.AgentDir);
        // 损坏裁定文件：AdjudicationSet.Load 抛 JsonException → Evaluate 失败。
        File.WriteAllText(Path.Combine(f.AgentDir, "adjudications.json"), "{ 损坏的 JSON !!!");
        var audit = new SqliteAuditStore(Path.Combine(f.AgentDir, "audit.db"));
        try
        {
            var vm = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>(),
                audit: audit, identity: new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
            await vm.RefreshAsync(); // 不抛（不崩 UI）
            Assert.NotNull(vm.ErrorMessage);
            Assert.Empty(vm.Issues);

            // 失败也留 check-run 审计（I2：此前仅 ErrorMessage 无留痕，违反 §9）。
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "check-run");
            AuditEntry e = rec.Entry;
            Assert.Equal("view-check", e.Source);
            Assert.Equal("view", e.Session);
            Assert.Equal("tester", e.Operator);
            Assert.Equal("Draw", e.Role);
            Assert.Equal(f.Session.SourcePath, e.Target);
            // 故障时前后哈希空串（逐字镜像 RunRegressionCheck 失败路径），链仍绿。
            Assert.Equal("", e.BeforeHash);
            Assert.Equal("", e.AfterHash);
            using var doc = JsonDocument.Parse(e.ParamsJson);
            JsonElement p = doc.RootElement;
            Assert.False(string.IsNullOrEmpty(p.GetProperty("Error").GetString()));
            Assert.False(p.GetProperty("overlayPresent").GetBoolean());
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            audit.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshAsync_CorruptOverlay_WritesFailedCheckRunAudit()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.AgentDir);
        // 损坏 overlay：SemanticOverlay.Load 抛 JsonException → Evaluate 失败。
        File.WriteAllText(Path.Combine(f.AgentDir, "semantic.json"), "not-json-at-all");
        var audit = new SqliteAuditStore(Path.Combine(f.AgentDir, "audit.db"));
        try
        {
            var vm = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>(),
                audit: audit, identity: new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
            await vm.RefreshAsync();
            Assert.NotNull(vm.ErrorMessage);
            Assert.Empty(vm.Issues);

            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "check-run");
            AuditEntry e = rec.Entry;
            Assert.Equal("view-check", e.Source);
            Assert.Equal("check-run", e.Kind);
            Assert.Equal("", e.BeforeHash);
            Assert.Equal("", e.AfterHash);
            using var doc = JsonDocument.Parse(e.ParamsJson);
            JsonElement p = doc.RootElement;
            Assert.False(string.IsNullOrEmpty(p.GetProperty("Error").GetString()));
            // overlay Load 失败发生在 overlayPresent 赋值之前 → false（镜像 RunRegressionCheck 口径）。
            Assert.False(p.GetProperty("overlayPresent").GetBoolean());
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            audit.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshAsync_CorruptOverlay_NoAuditInjected_StillDegradesQuietly()
    {
        // 无审计注入（既有口径）：失败仅 ErrorMessage，不建库（行为保持）。
        using var f = new Fixture();
        Directory.CreateDirectory(f.AgentDir);
        File.WriteAllText(Path.Combine(f.AgentDir, "semantic.json"), "not-json-at-all");
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.Issues);
        Assert.False(File.Exists(Path.Combine(f.AgentDir, "audit.db")));
    }

    // ---- 修复轮1：哈希临界区 + RoomNameChecker parity ----

    /// <summary>
    /// 手动泵送同步上下文（修复轮1）：把 Post 的续延入队而非立即执行，使测试能确定性
    /// 控制 RefreshAsync 中 await Task.Run(Evaluate) 续延的恢复时机——在评估释闸后、
    /// 审计分支执行前落地一次"并发编辑"，钉住审计哈希必须来自评估临界区（eval.Hash），
    /// 而非释闸后重读 session（旧实现会读到编辑后的状态，复现证据失真）。
    /// </summary>
    private sealed class ManualPumpContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly ManualResetEventSlim _posted = new(false);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            _posted.Set();
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override SynchronizationContext CreateCopy() => this;

        /// <summary>等待至少一个续延入队（评估完成的确定性边界）。</summary>
        public bool WaitPost(TimeSpan timeout) => _posted.Wait(timeout);

        /// <summary>在调用线程同步执行已入队的续延。</summary>
        public void Pump()
        {
            while (_queue.TryDequeue(out var item)) item.Callback(item.State);
        }

        public void Dispose() => _posted.Dispose();
    }

    [Fact]
    public async Task RefreshAsync_HashComputedInsideSessionGate_ReflectsEvaluatedState()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.AgentDir);
        var audit = new SqliteAuditStore(Path.Combine(f.AgentDir, "audit.db"));
        var gate = new SemaphoreSlim(1, 1);
        SynchronizationContext? original = SynchronizationContext.Current;
        using var pump = new ManualPumpContext();
        try
        {
            var vm = new IssuesViewModel(f.Session, f.ProjectDir, Array.Empty<string>(),
                sessionGate: gate, audit: audit,
                identity: new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));

            // 持闸：refresh 的 Evaluate 确定性阻塞在 sessionGate（镜像既有并发测试持闸先例）。
            await gate.WaitAsync();
            Task refresh;
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                // await Task.Run(Evaluate) 的续延被 pump 入队，不立即执行。
                refresh = vm.RefreshAsync();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }

            // 编辑 E1 在持闸期间落地（评估之前）：被评估状态含 E1。
            f.Session.Doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            f.Session.InvalidateHash(); // 直接改 Doc：须显式失效（项3 §5）
            string hashAtEval = f.Session.ComputeHash();
            gate.Release(); // Evaluate 取闸→评估(含 E1)→同闸段取哈希→释闸→续延入队

            // 等待 Evaluate 完成且续延入队（确定性边界，非竞态）。
            Assert.True(pump.WaitPost(TimeSpan.FromSeconds(15)), "Evaluate 续延未在 15s 内入队");

            // 编辑 E2 在评估释闸后、审计分支执行前落地（模拟并发编辑）：
            // 审计哈希必须仍为被评估状态 hashAtEval，而非 E2 之后的状态。
            f.Session.Doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(2, 0, 0) });
            f.Session.InvalidateHash();
            string hashAfterEdit = f.Session.ComputeHash();
            Assert.NotEqual(hashAtEval, hashAfterEdit);

            pump.Pump(); // 恢复 RefreshAsync 续延 → 审计写入
            await refresh.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Null(vm.ErrorMessage);
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "check-run");
            // 前后哈希一致且等于被评估状态（修复轮1：hash 在 sessionGate 临界区内计算）。
            Assert.Equal(hashAtEval, rec.Entry.BeforeHash);
            Assert.Equal(hashAtEval, rec.Entry.AfterHash);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
            audit.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>仅含 room-name 规则的最小 skill 包（check spec 镜像 interior-244 R-244-5.2.5）。</summary>
    private static string MakeRoomNameSkillDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        File.WriteAllText(Path.Combine(dir, "skill.yaml"),
            "name: room-name-skill\nversion: \"1.0.0\"\ndiscipline: architecture\ndrawingType: any\n");
        File.WriteAllText(Path.Combine(dir, "rules", "r1.yaml"), """
            id: R-244-5.2.5
            source: { standard: "JGJ/T 244-2011", clause: "5.2.5", excerpt: "平面图中应注写房间的名称或编号", verification: source-text }
            modal: "应"
            force: recommended
            severity: medium
            scope: { discipline: any, drawingType: any }
            check: { type: room-name, params: {} }
            suggestion: "注写房间名称或编号"
            """);
        return dir;
    }

    // ---- §4.1④ 勾选→意图：IsChecked INPC + 跨刷新勾选保持（key=RuleId+Targets） ----

    [Fact]
    public async Task IsChecked_Set_RaisesPropertyChanged()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        IssueEntry entry = vm.Issues[0];
        Assert.False(entry.IsChecked); // 默认未勾选（忽略=正常决策，spec §6.2）
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        entry.IsChecked = true;
        Assert.Equal(new[] { nameof(IssueEntry.IsChecked) }, raised);
        entry.IsChecked = true; // 同值幂等，不重复通知
        Assert.Single(raised);
        entry.IsChecked = false;
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public async Task Refresh_SameKey_CheckPreservedAcrossRefresh()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        IssueEntry height = vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        height.IsChecked = true;
        await vm.RefreshAsync();
        IssueEntry rebuilt = vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        Assert.NotSame(height, rebuilt); // 刷新重建条目（新实例）
        Assert.True(rebuilt.IsChecked); // 同 RuleId+Targets → 勾选跨刷新保持
        Assert.False(vm.Issues.Single(i => i.RuleId == "R-50001-13.0.1").IsChecked); // 未勾选不受影响
    }

    [Fact]
    public async Task Refresh_DifferentTargetsKey_CheckNotPreserved()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2").IsChecked = true;
        // 替换违规文字：同 RuleId 但 Targets（句柄）变化 → key 不同，勾选不保持。
        TextEntity oldText = f.Session.Doc.Entities.OfType<TextEntity>().First();
        string oldHandle = oldText.Handle.ToString();
        f.Session.Doc.Entities.Remove(oldText);
        f.Session.Doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(30, 30, 0), Value = "新违规文字", Height = 4.2 });
        await vm.RefreshAsync();
        IssueEntry rebuilt = vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        Assert.DoesNotContain(oldHandle, rebuilt.Targets);
        Assert.False(rebuilt.IsChecked);
    }

    [Fact]
    public async Task Uncheck_RemovesKey_NextRefreshNotChecked()
    {
        using var f = new Fixture();
        IssuesViewModel vm = f.MakeVm();
        await vm.RefreshAsync();
        vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2").IsChecked = true;
        await vm.RefreshAsync();
        IssueEntry restored = vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        Assert.True(restored.IsChecked);
        restored.IsChecked = false; // 取消勾选 → key 同步移除
        await vm.RefreshAsync();
        Assert.False(vm.Issues.Single(i => i.RuleId == "R-50001-5.0.2").IsChecked);
    }

    /// <summary>标高规则 mode 省略的最小 skill 包（params 空 → 全查：小数位+符号，I1 碰撞夹具）。</summary>
    private static string MakeElevationSkillDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        File.WriteAllText(Path.Combine(dir, "skill.yaml"),
            "name: elevation-skill\nversion: \"1.0.0\"\ndiscipline: architecture\ndrawingType: any\n");
        File.WriteAllText(Path.Combine(dir, "rules", "r1.yaml"), """
            id: R-TEST-ELEV
            source: { standard: "院标", clause: "1.0", excerpt: "标高注写格式测试条款", verification: source-text }
            modal: "应"
            force: recommended
            severity: high
            scope: { discipline: any, drawingType: any }
            check: { type: elevation-format, params: {} }
            suggestion: "按 11.8.4/11.8.5 修正标高注写"
            """);
        return dir;
    }

    [Fact]
    public async Task Refresh_SameRuleSameTargets_DifferentMessage_CheckKeysIndependent()
    {
        // I1（合并前 ISTQB fixwave2）：skill 标高规则 mode 省略 → 同一 "+3.50" 文本产
        // 两条 issue（小数位 2 位 + 正数带 "+"），同 RuleId 同 Targets 异 Message。
        // 旧 key=RuleId|Targets 碰撞：勾其一刷新后两条全勾 / 取消其一两条全丢。
        // 修复后 key 加 Message 区分因子，两条勾选态互不牵连。
        string skillDir = MakeElevationSkillDir();
        string dxf = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "+3.50", Height = 3 }));
        string proj = Path.Combine(Path.GetTempPath(), "cadagent-elev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(proj, ".cadagent"));
        try
        {
            var session = DrawingSession.Load(dxf);
            string handle = session.Doc.Entities.OfType<TextEntity>().Single().Handle.ToString();
            var overlay = new SemanticOverlay();
            overlay.UpsertAnnotation(new SemanticAnnotation(handle, SemanticClass.Annotation,
                Provenance.HumanConfirmed, 0.9, "测试标注"));
            overlay.Save(Path.Combine(proj, ".cadagent", "semantic.json"));
            var vm = new IssuesViewModel(session, proj, new[] { skillDir });
            await vm.RefreshAsync();
            Assert.Null(vm.ErrorMessage);
            List<IssueEntry> two = vm.Issues.Where(i => i.RuleId == "R-TEST-ELEV").ToList();
            Assert.Equal(2, two.Count);
            Assert.Equal(two[0].Targets, two[1].Targets); // 同目标句柄
            Assert.NotEqual(two[0].Message, two[1].Message); // 异消息（小数位 vs 符号）

            // 勾其一（符号条）→ 刷新 → 仅该条保持勾选（小数位条不混入）。
            two.Single(i => i.Message.Contains("正数")).IsChecked = true;
            await vm.RefreshAsync();
            Assert.True(vm.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("正数")).IsChecked);
            Assert.False(vm.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("小数位")).IsChecked);

            // 两条皆勾后取消其一 → 刷新 → 另一条仍在（不漏记）。
            vm.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("小数位")).IsChecked = true;
            vm.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("正数")).IsChecked = false;
            await vm.RefreshAsync();
            Assert.True(vm.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("小数位")).IsChecked);
            Assert.False(vm.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("正数")).IsChecked);
        }
        finally
        {
            Directory.Delete(proj, true);
            Directory.Delete(skillDir, true);
            File.Delete(dxf);
        }
    }

    [Fact]
    public async Task Refresh_RoomNameRule_Evaluated_ParityWithCli()
    {
        // 修复轮1：ViewRuleEngine 须注册 RoomNameChecker（逐字镜像 CLI StandardsEngine 注册序）——
        // 含/不含房名标注夹具镜像 Cli.Tests Interior244PackageTests 与 Data.Tests RoomNameCheckerTests。
        // 缺 RoomNameChecker 时 room-name 规则被 NoChecker 跳过、无 issue（RED）。
        string skillDir = MakeRoomNameSkillDir();
        string noRoom = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) }));
        string withRoom = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "客厅", Height = 5 }));
        string projNo = Path.Combine(Path.GetTempPath(), "cadagent-room-no-" + Guid.NewGuid().ToString("N"));
        string projWith = Path.Combine(Path.GetTempPath(), "cadagent-room-with-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projNo);
        Directory.CreateDirectory(projWith);
        try
        {
            // 不含房名标注 → R-244-5.2.5 recommended issue（镜像 CLI Check_NoRoom）。
            var vmNo = new IssuesViewModel(DrawingSession.Load(noRoom), projNo, new[] { skillDir });
            await vmNo.RefreshAsync();
            Assert.Null(vmNo.ErrorMessage);
            IssueEntry room = Assert.Single(vmNo.Issues, i => i.RuleId == "R-244-5.2.5");
            Assert.Equal(RuleForce.Recommended, room.Force);
            Assert.Equal("5.2.5", room.Clause);
            Assert.Contains("房间的名称或编号", room.Message);
            Assert.DoesNotContain(vmNo.SkippedReasons,
                s => s.RuleId == "R-244-5.2.5" && s.Category == SkipCategories.NoChecker);

            // 含房名标注（"客厅"）→ 无房名 issue（镜像 CLI Check_WithRoom_NoRoomNameIssue）。
            var vmWith = new IssuesViewModel(DrawingSession.Load(withRoom), projWith, new[] { skillDir });
            await vmWith.RefreshAsync();
            Assert.Null(vmWith.ErrorMessage);
            Assert.DoesNotContain(vmWith.Issues, i => i.RuleId == "R-244-5.2.5");
        }
        finally
        {
            Directory.Delete(projNo, true);
            Directory.Delete(projWith, true);
            Directory.Delete(skillDir, true);
            File.Delete(noRoom);
            File.Delete(withRoom);
        }
    }
}
