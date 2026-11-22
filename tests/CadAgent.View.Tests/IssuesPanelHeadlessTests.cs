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
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 5 headless coverage ([AvaloniaFact], real Skia frames):
/// MainWindow right-hand issues panel builds empty, the Refresh button runs the
/// in-process check and populates the list with force badges (Recommended
/// yellow / Mandatory red), the header shows the mandatory count, and clicking
/// an issue row highlights its target handles on the canvas + centers the view.
/// Fixture: text height 4.2 + mixed-charset layer W-墙体 -> 2 recommended issues.
/// </summary>
public class IssuesPanelHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-issues-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ProjectDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            });
        }

        public void Dispose()
        {
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }

    private static (MainWindow Window, Fixture F) ShowWindow()
    {
        var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, f);
    }

    private static void Cleanup(MainWindow window, Fixture f)
    {
        window.Close();
        // 视图闭环 T6：刷新留 check-run 审计 → 可能已惰性创建 audit.db；
        // Microsoft.Data.Sqlite 连接池持有文件句柄，Windows 下删临时目录前须清池。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        f.Dispose();
    }

    /// <summary>Click Refresh and await the VM's IssuesChanged (async Task.Run path).</summary>
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

    private static List<Border> Badges(MainWindow window, string hex) =>
        window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Background is SolidColorBrush scb && scb.Color == Color.Parse(hex))
            .ToList();

    [AvaloniaFact]
    public async Task IssuesPanel_RefreshButton_PopulatesList_WithBadgesAndCount()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.NotNull(window.Issues);
            Assert.NotNull(window.IssueList);
            Assert.NotNull(window.RefreshIssuesButton);
            Assert.Equal(0, window.IssueList!.ItemCount); // no auto-check on open

            await ClickRefreshAsync(window);

            Assert.Null(window.Issues!.ErrorMessage);
            Assert.Equal(2, window.IssueList.ItemCount);
            Assert.Contains("0 mandatory", window.IssuesHeader!.Text);
            Assert.Contains("3 skipped", window.IssuesHeader.Text);
            // Both builtin issues are Recommended -> yellow badges, no red.
            Assert.Equal(2, Badges(window, "#F9A825").Count);
            Assert.Empty(Badges(window, "#B71C1C"));
            // Clause citation visible in the row text.
            List<TextBlock> texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text?.Contains("R-50001-5.0.2") == true && t.Text.Contains("5.0.2"));
            Assert.Contains(texts, t => t.Text?.Contains("R-50001-13.0.1") == true);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task IssuesPanel_ClickIssue_HighlightsTargetsAndCenters()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            await ClickRefreshAsync(window);
            string textHandle = window.Issues!.Issues.Single(i => i.RuleId == "R-50001-5.0.2").Targets[0];
            int idx = window.Issues.Issues.ToList().FindIndex(i => i.RuleId == "R-50001-5.0.2");

            // Real left click on the issue row -> row-level PointerPressed -> LocateIssue
            // (the Expander header ToggleButton marks the event handled, so the row
            // root handles it with handledEventsToo).
            var row = (Control)window.IssueList!.ContainerFromIndex(idx)!;
            Point p = row.TranslatePoint(new Point(row.Bounds.Width - 30, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(textHandle, window.Canvas!.HighlightedHandles);
            Assert.Equal(textHandle, window.Canvas.SelectedHandle);
            Assert.Contains(textHandle, window.Status!.Text);
            // Header click also expanded the row -> citation details visible.
            List<TextBlock> texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text?.StartsWith("Detection basis:") == true);
            Assert.Contains(texts, t => t.Text?.Contains("GB/T 50001-2017 5.0.2") == true);
            // View centered on the text insertion point (10,10).
            DrawingCanvas canvasControl = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            (double sx, double sy) = window.Canvas.WorldToScreen(10, 10);
            Assert.Equal(canvasControl.Bounds.Width / 2, sx, 1);
            Assert.Equal(canvasControl.Bounds.Height / 2, sy, 1);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task IssuesPanel_AdjudicatedMandatory_RedBadgeAndHeaderCount()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            var set = new AdjudicationSet();
            set.Upsert(new Adjudication("R-50001-5.0.2", "tester", DateTimeOffset.UtcNow,
                "测试升级", ForceOverride: RuleForce.Mandatory));
            set.Save(Path.Combine(f.ProjectDir, ".cadagent", "adjudications.json"));

            await ClickRefreshAsync(window);

            Assert.Equal(1, window.Issues!.MandatoryCount);
            Assert.Contains("1 mandatory", window.IssuesHeader!.Text);
            Assert.Single(Badges(window, "#B71C1C")); // Mandatory red
            Assert.Single(Badges(window, "#F9A825")); // remaining Recommended yellow
        }
        finally { Cleanup(window, f); }
    }
}
