using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 8 headless coverage ([AvaloniaFact]): MainWindow left panel is a
/// 3-tab TabControl (图层/纠偏/裁定, layers default). Semantic tab: Scan click
/// populates the ambiguous list with evidence (count/hint/sample handles),
/// Isolate hides the other canvas layers, Map click persists HumanConfirmed
/// 1.0 annotations + semantic-map audit and empties the ambiguous list.
/// Adjudication tab: rule list + force override/exempt/reason/apply; a real
/// Apply click upgrades the issue force AND auto-refreshes the issues panel
/// (AdjudicationsChanged -> IssuesViewModel.RefreshAsync linkage); missing
/// reason surfaces the CLI refusal inline with no side effects. Without
/// audit.db the first scan lazily creates the hash-chained trail.
/// Fixture: layer W-墙体 (line + 4.2mm text -> 5.0.2 + 13.0.1 issues,
/// 1 ambiguous layer) plus known layer WALL (1 line -> 13.0.2 structure
/// issue, single-segment name) = 3 recommended issues total.
/// </summary>
public class SemanticAdjudicationPanelHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string AuditDbPath => Path.Combine(AgentDir, "audit.db");
        public string OverlayPath => Path.Combine(AgentDir, "semantic.json");
        public string AdjudicationsPath => Path.Combine(AgentDir, "adjudications.json");

        public Fixture(bool withAuditDb = true)
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-semadj-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                var known = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Layers.Add(known);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(100, 50, 0), Layer = known });
            });
            if (withAuditDb)
            {
                using var store = new SqliteAuditStore(AuditDbPath);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        public void Dispose()
        {
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

    private static (MainWindow Window, Fixture F) ShowWindow(bool withAuditDb = true)
    {
        var f = new Fixture(withAuditDb);
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Review)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, f);
    }

    private static void Cleanup(MainWindow window, Fixture f)
    {
        window.Close();
        f.Dispose();
    }

    private static void SelectTab(MainWindow window, int index)
    {
        window.LeftTabs!.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Click Scan/Map and await the VM's SemanticChanged (async Task.Run path).</summary>
    private static async Task ClickSemanticAsync(MainWindow window, Button button)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        window.Semantic!.SemanticChanged += Handler;
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Semantic!.SemanticChanged -= Handler; }
    }

    /// <summary>Click Refresh and await IssuesChanged (T5 pattern).</summary>
    private static async Task ClickRefreshAsync(MainWindow window)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        window.Issues!.IssuesChanged += Handler;
        try
        {
            window.RefreshIssuesButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Issues!.IssuesChanged -= Handler; }
    }

    /// <summary>Click Apply and await the linked issues auto-refresh (IssuesChanged).</summary>
    private static async Task ClickApplyAsync(MainWindow window)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        window.Issues!.IssuesChanged += Handler;
        try
        {
            window.AdjudicationApplyButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Issues!.IssuesChanged -= Handler; }
    }

    private static List<Border> Badges(MainWindow window, string hex) =>
        window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Background is SolidColorBrush scb && scb.Color == Color.Parse(hex))
            .ToList();

    [AvaloniaFact]
    public void LeftTabs_Build_FiveTabs_LayersDefault()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.NotNull(window.LeftTabs);
            // 视图闭环 T10：新增"计划"tab（④纠偏计划预览）——三 tab 扩为四 tab；
            // 天读证据：新增"天读证据"tab（只读 overlay 深读面板）——扩为五 tab。
            Assert.Equal(5, window.LeftTabs!.ItemCount);
            Assert.Equal("图层", ((TabItem)window.LeftTabs.Items[0]!).Header);
            Assert.Equal("纠偏", ((TabItem)window.LeftTabs.Items[1]!).Header);
            Assert.Equal("裁定", ((TabItem)window.LeftTabs.Items[2]!).Header);
            Assert.Equal("计划", ((TabItem)window.LeftTabs.Items[3]!).Header);
            Assert.Equal("天读证据", ((TabItem)window.LeftTabs.Items[4]!).Header);
            Assert.Equal(0, window.LeftTabs.SelectedIndex);
            Assert.NotNull(window.PlanPreview); // 生产装配（未注入 → writeAudit/signoffIdentity 同源）
            // Layers tab keeps T4 behavior; the other VMs are wired.
            Assert.Equal(3, window.LayerList!.ItemCount); // T8 图层表并集：含默认空层 "0"
            Assert.NotNull(window.Semantic);
            Assert.NotNull(window.Adjudication);
            Assert.Null(window.Adjudication!.ErrorMessage);
            Assert.Equal(7, window.AdjudicationRuleList!.ItemCount); // builtin rules
            Assert.NotNull(window.SemanticScanButton);
            Assert.NotNull(window.AmbiguousList);
            Assert.Equal(0, window.AmbiguousList!.ItemCount); // no auto-scan on open
            Assert.NotNull(window.ForceOverrideSelector);
            Assert.NotNull(window.ExemptCheck);
            Assert.NotNull(window.AdjudicationReason);
            Assert.NotNull(window.AdjudicationApplyButton);
            Assert.False(window.AdjudicationError!.IsVisible);
            Assert.False(window.SemanticError!.IsVisible);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task SemanticTab_ScanClick_PopulatesAmbiguous_WithEvidenceAndAudit()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            SelectTab(window, 1);
            await ClickSemanticAsync(window, window.SemanticScanButton!);

            Assert.Null(window.Semantic!.ErrorMessage);
            Assert.False(window.SemanticError!.IsVisible);
            Assert.Equal(1, window.AmbiguousList!.ItemCount);
            AmbiguousLayer a = Assert.Single(window.Semantic.Ambiguous);
            Assert.Equal("W-墙体", a.LayerName);
            Assert.Equal(2, a.EntityCount);

            // Evidence rendered in the row: name + count, hint, sample handles.
            var row = (Control)window.AmbiguousList.ContainerFromIndex(0)!;
            List<TextBlock> texts = row.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text?.Contains("W-墙体 (2)") == true);
            Assert.Contains(texts, t => t.Text == "疑似墙体");
            Assert.Contains(texts, t => t.Text?.Contains(a.SampleHandles[0]) == true);

            Assert.True(File.Exists(f.OverlayPath));
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-scan");
            Assert.Equal("semantic", rec.Entry.Source);
            Assert.Equal("tester", rec.Entry.Operator);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task SemanticTab_IsolateAndMapClick_PersistHumanConfirmed()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            SelectTab(window, 1);
            await ClickSemanticAsync(window, window.SemanticScanButton!);
            Assert.Equal(1, window.AmbiguousList!.ItemCount);

            var row = (Control)window.AmbiguousList.ContainerFromIndex(0)!;
            Button isolate = row.GetVisualDescendants().OfType<Button>()
                .First(b => (b.Content as string) == "Isolate");
            isolate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("WALL", window.Canvas!.HiddenLayers);
            Assert.DoesNotContain("W-墙体", window.Canvas.HiddenLayers);
            Assert.All(window.Canvas.VisibleItems, i => Assert.Equal("W-墙体", i.Layer));

            ComboBox combo = row.GetVisualDescendants().OfType<ComboBox>().Single();
            combo.SelectedItem = "Wall";
            Button map = row.GetVisualDescendants().OfType<Button>()
                .First(b => (b.Content as string) == "Map");
            await ClickSemanticAsync(window, map);

            Assert.Null(window.Semantic!.ErrorMessage);
            Assert.Equal(0, window.AmbiguousList!.ItemCount); // mapped layer leaves the list
            SemanticOverlay overlay = SemanticOverlay.Load(f.OverlayPath);
            Assert.Contains(overlay.Mappings, m => m.LayerName == "W-墙体" && m.ConfirmedBy == "tester");
            Assert.All(overlay.Annotations.Where(x => x.Provenance == Provenance.HumanConfirmed),
                x => Assert.Equal(1.0, x.Confidence));
            Assert.Equal(2, overlay.Annotations.Count(x => x.Provenance == Provenance.HumanConfirmed
                && x.Class == SemanticClass.Wall));
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map");
            Assert.Equal("W-墙体", rec.Entry.Target);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task AdjudicationTab_ApplyUpgrade_AutoRefreshesIssues_RedBadge()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            await ClickRefreshAsync(window);
            // 5.0.2 (text height) + 13.0.1 (W-墙体 charset) + 13.0.2 (WALL structure).
            Assert.Equal(3, window.Issues!.Issues.Count);
            Assert.Equal(0, window.Issues.MandatoryCount);

            SelectTab(window, 2);
            int idx = window.Adjudication!.Rules.ToList().FindIndex(r => r.Id == "R-50001-5.0.2");
            window.AdjudicationRuleList!.SelectedIndex = idx;
            window.ForceOverrideSelector!.SelectedIndex = 1; // Mandatory
            window.AdjudicationReason!.Text = "测试升级";
            await ClickApplyAsync(window);

            Assert.False(window.AdjudicationError!.IsVisible);
            // Issues auto-refreshed via AdjudicationsChanged linkage — no manual Refresh.
            Assert.Equal(1, window.Issues!.MandatoryCount);
            Assert.Contains("1 mandatory", window.IssuesHeader!.Text);
            Assert.Single(Badges(window, "#B71C1C"));
            Assert.Equal(2, Badges(window, "#F9A825").Count); // 13.0.1 + 13.0.2 stay Recommended

            Assert.True(File.Exists(f.AdjudicationsPath));
            Adjudication a = Assert.Single(window.Adjudication.Adjudications);
            Assert.Equal(RuleForce.Mandatory, a.ForceOverride);
            Assert.Equal("测试升级", a.Reason);
            Assert.Equal(1, window.AdjudicationList!.ItemCount);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "rule-adjudicated");
            Assert.Equal("rule", rec.Entry.Source);
            Assert.Equal("R-50001-5.0.2", rec.Entry.Target);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void AdjudicationTab_ApplyWithoutReason_InlineError_NoSideEffects()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            SelectTab(window, 2);
            int idx = window.Adjudication!.Rules.ToList().FindIndex(r => r.Id == "R-50001-5.0.2");
            window.AdjudicationRuleList!.SelectedIndex = idx;
            window.ForceOverrideSelector!.SelectedIndex = 1; // Mandatory
            // reason left empty -> CLI refusal surfaced inline.
            window.AdjudicationApplyButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.AdjudicationError!.IsVisible);
            Assert.Contains("需要 --reason", window.AdjudicationError.Text);
            Assert.Empty(window.Adjudication!.Adjudications);
            Assert.False(File.Exists(f.AdjudicationsPath));
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            Assert.Empty(audit.ReadAll());
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task NoAuditDb_ScanClick_LazilyCreatesAuditTrail()
    {
        (MainWindow window, Fixture f) = ShowWindow(withAuditDb: false);
        try
        {
            Assert.False(File.Exists(f.AuditDbPath)); // open must not create the db (T6)
            SelectTab(window, 1);
            await ClickSemanticAsync(window, window.SemanticScanButton!);

            Assert.Null(window.Semantic!.ErrorMessage);
            Assert.True(File.Exists(f.AuditDbPath)); // first write creates the trail
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-scan");
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    /// <summary>
    /// Review fix (Important): LazyWriteAuditStore.Ensure must double-check
    /// lock first-create. Semantic (Task.Run core) and Adjudication
    /// (synchronous core) share ONE lazy store; concurrent first appends
    /// without the lock create two SqliteAuditStore instances (two
    /// _writeLocks) whose transactions read the same chain head -> duplicate
    /// seq / broken chain. Post-fix: one shared instance, chain valid.
    /// </summary>
    [AvaloniaFact]
    public async Task NoAuditDb_ConcurrentScanAndAdjudicate_ShareOneStore_ChainValid()
    {
        (MainWindow window, Fixture f) = ShowWindow(withAuditDb: false);
        var refreshed = new TaskCompletionSource();
        void Handler() => refreshed.TrySetResult();
        window.Issues!.IssuesChanged += Handler;
        try
        {
            Assert.False(File.Exists(f.AuditDbPath)); // open must not create the db (T6)
            // Scan core appends on a thread-pool thread while AdjudicateAsync
            // appends synchronously on the UI thread -> genuine Ensure race.
            await Task.WhenAll(
                window.Semantic!.ScanAsync(Array.Empty<string>()),
                window.Adjudication!.AdjudicateAsync("R-50001-5.0.2", RuleForce.Mandatory, false, "并发首写"));
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(15)); // linked issues refresh done
            Dispatcher.UIThread.RunJobs();

            Assert.Null(window.Semantic!.ErrorMessage);
            Assert.True(File.Exists(f.AuditDbPath));
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-scan");
            Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "rule-adjudicated");
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally
        {
            window.Issues!.IssuesChanged -= Handler;
            Cleanup(window, f);
        }
    }
}
