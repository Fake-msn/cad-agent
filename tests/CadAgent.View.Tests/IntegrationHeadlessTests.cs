using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Core;
using CadAgent.Data.Rules;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 10 headless integration coverage ([AvaloniaFact], real Skia frames):
/// 1) edit re-entrancy busy guard — a rapid second canvas click while an edit is
///    in flight is IGNORED (structural flag check, not dispatcher-order reliance;
///    the shared session gate is held by the test so the edit cannot complete);
/// 2) --skill passthrough — MainViewModel.SkillDirs wires Issues/Adjudication/
///    Semantic VMs (sample skill rule visible, mandatory issue evaluated, alias
///    layer no longer ambiguous);
/// 3) layout polish — status bar shows drawing/count/zoom/selection, toolbar
///    tooltips, window title, WCAG badge contrast (T5 minor).
/// </summary>
public class IntegrationHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");

        public Fixture(Action<ACadSharp.CadDocument>? populate = null)
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-t10-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(populate ?? (doc =>
            {
                var wall = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
            }));
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string SampleSkillDir() => Path.Combine(RepoRoot(), "examples", "skills", "sample-institute");

    private static Button ToolButton(MainWindow window, string label) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(b => b is not CheckBox && (b.Content as string) == label);

    private static void ClickCanvasAtWorld(MainWindow window, double wx, double wy)
    {
        DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
        (double sx, double sy) = window.Canvas!.WorldToScreen(wx, wy);
        Point p = canvas.TranslatePoint(new Point(sx, sy), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // ---- 1) busy guard ----

    [AvaloniaFact]
    public async Task RapidSecondClick_DuringInFlightEdit_Ignored()
    {
        using var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        // 注入共享会话闸：测试持闸 → 编辑确定性地停在闸上（不依赖调度时序）。
        var gate = new SemaphoreSlim(1, 1);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)), gate);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 20, 20); // anchor
            Assert.NotNull(window.Canvas!.PendingPoint);

            await gate.WaitAsync(); // hold the gate: the edit stays in flight
            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            window.Edit!.EditCompleted += Handler;
            try
            {
                ClickCanvasAtWorld(window, 80, 30); // commit -> busy, blocked on gate
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.EditToolbar!.IsEnabled); // busy UI state

                // Rapid second click while the edit is in flight: must be ignored
                // (no new preview anchor, no tool switch via toolbar presses).
                ClickCanvasAtWorld(window, 90, 40);
                Assert.Null(window.Canvas.PendingPoint);
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
                window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(EditTool.Line, window.Edit.ActiveTool);

                gate.Release();
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                window.Edit.EditCompleted -= Handler;
                if (gate.CurrentCount == 0) gate.Release();
            }

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Equal(2, window.Canvas.Items.Count); // exactly ONE line added
            Assert.Single(window.Edit.RecentTxIds);
            Assert.Null(window.Canvas.PendingPoint);
            Assert.True(window.EditToolbar.IsEnabled); // busy cleared
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ToolSwitch_DuringInFlightEdit_Ignored()
    {
        // Final review bundled minor: SetActiveTool must check _editBusy — toolbar
        // tool buttons route through it, and RaiseEvent bypasses IsEnabled=false.
        using var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var gate = new SemaphoreSlim(1, 1);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)), gate);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 20, 20); // anchor

            await gate.WaitAsync(); // hold the gate: the edit stays in flight
            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            window.Edit!.EditCompleted += Handler;
            try
            {
                ClickCanvasAtWorld(window, 80, 30); // commit -> busy, blocked on gate
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.EditToolbar!.IsEnabled);

                // Tool switch attempts while busy: ignored, ActiveTool unchanged.
                ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
                ToolButton(window, "Select").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(EditTool.Line, window.Edit.ActiveTool);

                gate.Release();
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                window.Edit.EditCompleted -= Handler;
                if (gate.CurrentCount == 0) gate.Release();
            }

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Equal(2, window.Canvas!.Items.Count); // exactly ONE line added
            // After completion tool switching works again.
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(EditTool.Text, window.Edit.ActiveTool);
        }
        finally { window.Close(); }
    }

    // ---- 2) --skill passthrough wiring ----

    [AvaloniaFact]
    public async Task SkillDirs_WireIssuesAdjudicationAndSemantic()
    {
        using var f = new Fixture(doc =>
        {
            var alias = new Layer("W-示例墙");   // sample skill alias -> Wall
            var mixed = new Layer("混用layer");  // charset violation (builtin + skill)
            doc.Layers.Add(alias);
            doc.Layers.Add(mixed);
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = alias });
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(100, 10, 0), Layer = mixed });
        });
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir, new[] { SampleSkillDir() });
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // Adjudication tab: skill rule loaded into the rule list.
            Assert.Contains(window.Adjudication!.Rules, r => r.Id == "SK-SAMPLE-001");
            Assert.Null(window.Adjudication.ErrorMessage);

            // Issues refresh: skill mandatory rule evaluated with citation.
            var issuesTcs = new TaskCompletionSource();
            void IssuesHandler() => issuesTcs.TrySetResult();
            window.Issues!.IssuesChanged += IssuesHandler;
            try
            {
                window.RefreshIssuesButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await issuesTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { window.Issues.IssuesChanged -= IssuesHandler; }
            Assert.Null(window.Issues.ErrorMessage);
            // 两个混用层（W-示例墙/混用layer）各命中一次 skill mandatory 规则。
            List<IssueEntry> skillIssues = window.Issues.Issues.Where(i => i.RuleId == "SK-SAMPLE-001").ToList();
            Assert.Equal(2, skillIssues.Count);
            Assert.All(skillIssues, i => Assert.Equal(RuleForce.Mandatory, i.Force));
            Assert.Contains("示例院标", skillIssues[0].Standard);
            Assert.True(window.Issues.MandatoryCount >= 2);

            // Semantic scan: skill alias layer no longer ambiguous.
            var semTcs = new TaskCompletionSource();
            void SemHandler() => semTcs.TrySetResult();
            window.Semantic!.SemanticChanged += SemHandler;
            try
            {
                window.SemanticScanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await semTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { window.Semantic.SemanticChanged -= SemHandler; }
            Assert.Null(window.Semantic.ErrorMessage);
            Assert.DoesNotContain(window.Semantic.Ambiguous, a => a.LayerName == "W-示例墙");
        }
        finally { window.Close(); }
    }

    // ---- 3) layout polish ----

    [AvaloniaFact]
    public void StatusBar_ShowsDrawingCountZoomAndSelection()
    {
        using var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Contains(Path.GetFileName(f.DxfPath), window.Status!.Text);
            Assert.Contains("1 render items", window.Status.Text);
            Assert.Contains("Zoom 1", window.Status.Text);

            window.Canvas!.ZoomAt(2, 100, 100);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Zoom 2", window.Status.Text);

            ClickCanvasAtWorld(window, 50, 0); // Select tool default path -> selection
            Assert.Contains("Selected", window.Status.Text);
            Assert.Contains("WALL", window.Status.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolbarButtons_AllHaveTooltips()
    {
        using var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            List<Button> buttons = window.EditToolbar!.Children.OfType<Button>().ToList();
            Assert.Equal(8, buttons.Count); // 7 tools + Undo
            Assert.All(buttons, b => Assert.NotNull(ToolTip.GetTip(b)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WindowTitle_ContainsAppNameAndDrawing()
    {
        using var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        var window = new MainWindow(vm);
        try
        {
            Assert.Contains("CadAgent", window.Title);
            Assert.Contains(Path.GetFileName(f.DxfPath), window.Title);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Canvas_ClipsDrawingToItsBounds()
    {
        // T10 真机验证发现：缩放后画布内容溢出到相邻面板（未裁剪）。
        using var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            Assert.True(canvas.ClipToBounds);
        }
        finally { window.Close(); }
    }

    private static double WcagContrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Lin(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        double l1 = Lum(a), l2 = Lum(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [AvaloniaFact]
    public async Task IssueBadges_AllThreeForces_MeetWcagContrast()
    {
        // Fixture: text 4.2 (5.0.2) + mixed layer (13.0.1) + WALL structure (13.0.2)
        // -> 3 recommended; adjudications upgrade 5.0.2 to Mandatory, downgrade
        // 13.0.1 to Informational, 13.0.2 stays Recommended -> all three colors.
        using var f = new Fixture(doc =>
        {
            var wall = new Layer("W-墙体");
            var known = new Layer("WALL");
            doc.Layers.Add(wall);
            doc.Layers.Add(known);
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(100, 50, 0), Layer = known });
        });
        var adjPath = Path.Combine(f.AgentDir, "adjudications.json");
        var set = AdjudicationSet.Load(adjPath);
        set.Upsert(new Adjudication("R-50001-5.0.2", "tester", DateTimeOffset.UtcNow, "升级", ForceOverride: RuleForce.Mandatory));
        set.Upsert(new Adjudication("R-50001-13.0.1", "tester", DateTimeOffset.UtcNow, "降级", ForceOverride: RuleForce.Informational));
        set.Save(adjPath);

        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
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
            finally { window.Issues.IssuesChanged -= Handler; }

            string[] badgeHexes = { "#B71C1C", "#F9A825", "#616161" }; // Mandatory/Recommended/Informational
            List<Border> badges = window.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Background is ISolidColorBrush scb
                    && badgeHexes.Any(h => scb.Color == Color.Parse(h))
                    && b.Child is TextBlock)
                .ToList();
            Assert.Equal(3, badges.Count);
            foreach (Border badge in badges)
            {
                var bg = (ISolidColorBrush)badge.Background!;
                var fg = (ISolidColorBrush)((TextBlock)badge.Child!).Foreground!;
                double ratio = WcagContrast(bg.Color, fg.Color);
                Assert.True(ratio >= 4.5,
                    $"badge {bg.Color} / {fg.Color} contrast {ratio:F2} < WCAG AA 4.5");
            }
        }
        finally { window.Close(); }
    }
}
