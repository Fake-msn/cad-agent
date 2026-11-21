using System.Runtime.InteropServices;
using System.Text.Json;
using ACadSharp.Entities;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 6 headless coverage ([AvaloniaFact], real Skia frames):
/// MainWindow bottom audit timeline builds tx rows (operator + commit ✓ /
/// failed ✗), selecting a tx highlights its step target handles on the canvas
/// and updates the status summary, and the Diff toggle overlays the snapshot
/// before-state: gray ghosts, green added, red removed + strikethrough
/// (pixel-probed). Missing snapshot -> visible error, toggle reverts, no crash.
/// Fixture: real StepEngine transactions over 2 lines + 1 text (tx1 adds a line,
/// moves line1, deletes the text; tx2 fails on a nonexistent handle).
/// </summary>
public class AuditPanelHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public SnapshotStore Snapshots { get; }
        public string Tx1Id { get; }
        public string Tx2Id { get; }
        public string Line1Handle { get; }
        public string TextHandle { get; }
        public string AddedLineHandle { get; }

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-audit-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ProjectDir, ".cadagent"));
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(100, 50, 0) });
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "OLD", Height = 3.5 });
            });
            Session = DrawingSession.Load(DxfPath);
            var entities = Session.Doc.Entities.ToList();
            Line1Handle = entities[0].Handle.ToString();
            TextHandle = entities[2].Handle.ToString();
            Audit = new SqliteAuditStore(Path.Combine(ProjectDir, ".cadagent", "audit.db"));
            Snapshots = new SnapshotStore(Path.Combine(ProjectDir, ".cadagent", "snapshots"));
            var engine = new StepEngine(Audit, Snapshots,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), HandlerRegistry.All());
            Tx1Id = engine.Run(Plan("p1",
                ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}"""),
                ("move-entity", $$"""{"handle":{{Line1Handle}},"dx":5,"dy":0}"""),
                ("delete-entity", $$"""{"handle":{{TextHandle}}}""")), Session, "s1").TxId;
            Tx2Id = engine.Run(Plan("pf",
                ("delete-entity", """{"handle":999999}""")), Session, "s1").TxId;
            AddedLineHandle = Audit.ReadAll().First(r => r.Entry.Kind == "step").Entry.Target;
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
        f.Dispose();
    }

    /// <summary>
    /// Real left click on the Diff toggle. RaiseEvent(ClickEvent) would only invoke
    /// Click subscribers without Button.OnClick -> ToggleButton.Toggle, so the
    /// checked state must be driven through the actual pointer pipeline.
    /// </summary>
    private static void ClickDiff(MainWindow window)
    {
        ToggleButton toggle = window.DiffToggle!;
        Point p = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Select tx1, click Diff and await the VM's DiffChanged + UI continuation.</summary>
    private static async Task EnableDiffAsync(MainWindow window)
    {
        window.AuditList!.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        window.Audit!.DiffChanged += Handler;
        try
        {
            ClickDiff(window);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs(); // flush MainWindow's post-diff continuation
        }
        finally { window.Audit!.DiffChanged -= Handler; }
    }

    [AvaloniaFact]
    public void Timeline_BuildsTxRows_WithOperatorAndStatus()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.NotNull(window.Audit);
            Assert.Null(window.Audit!.ErrorMessage);
            Assert.NotNull(window.AuditList);
            Assert.NotNull(window.DiffToggle);
            Assert.Equal(2, window.AuditList!.ItemCount);
            List<TextBlock> texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text?.Contains("tester") == true && t.Text.Contains("commit ✓"));
            Assert.Contains(texts, t => t.Text?.Contains("failed ✗") == true);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void Timeline_SelectTx_HighlightsStepTargets_AndStatusSummary()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            window.AuditList!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Tx1Id, window.Audit!.SelectedTxId);
            Assert.Contains(f.AddedLineHandle, window.Canvas!.HighlightedHandles);
            Assert.Contains(f.Line1Handle, window.Canvas.HighlightedHandles);
            Assert.Contains("commit ✓", window.Status!.Text);
            Assert.Contains("tester", window.Status.Text);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task DiffToggle_OverlayPixels_GreenAddedRedRemovedGrayBefore()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            await EnableDiffAsync(window);
            Assert.False(window.AuditError!.IsVisible);
            Assert.NotNull(window.Canvas!.DiffOverlay);
            Assert.Contains(f.AddedLineHandle, window.Canvas.DiffOverlay!.AddedHandles);
            Assert.Contains(f.TextHandle, window.Canvas.DiffOverlay.RemovedHandles);
            Assert.Contains("+1 -1 ~1", window.Status!.Text);

            window.CaptureRenderedFrame(); // ensure at least one composed frame
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            DrawingCanvas canvasControl = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();

            // Added line (0,60)-(100,60): midpoint must be GREEN (not yellow highlight).
            (double ax, double ay) = window.Canvas.WorldToScreen(50, 60);
            Point added = canvasControl.TranslatePoint(new Point(ax, ay), window)!.Value;
            Assert.True(MaxChannelInRect(frame!, added.X - 3, added.Y - 3, added.X + 3, added.Y + 3, channel: 1) > 150,
                "added line must render green (GREEN channel present)");
            Assert.True(MaxChannelInRect(frame!, added.X - 3, added.Y - 3, added.X + 3, added.Y + 3, channel: 0) < 90,
                "added line must be green, not yellow selection highlight (RED low)");

            // Removed text "OLD" at (10,10): red strikethrough across the glyph box midline.
            (double tx, double ty) = window.Canvas.WorldToScreen(10, 10);
            Point insertion = canvasControl.TranslatePoint(new Point(tx, ty), window)!.Value;
            double fontSize = 3.5 * window.Canvas.Scale;
            double width = 3 * 3.5 * 0.6 * window.Canvas.Scale;
            double strikeY = insertion.Y - fontSize / 2;
            Assert.True(MaxChannelInRect(frame!, insertion.X + 1, strikeY - 2, insertion.X + width - 1, strikeY + 2, channel: 0) > 150,
                "removed text must show a red strikethrough (RED channel above gray ghost level)");

            // Before-ghost of moved line1: world (2,0) is covered ONLY by the gray
            // before-state (after-state starts at x=5) -> neutral gray pixel.
            (double gx, double gy) = window.Canvas.WorldToScreen(2, 0);
            Point ghost = canvasControl.TranslatePoint(new Point(gx, gy), window)!.Value;
            (int r, int g, int b) = BrightestPixel(frame!, (int)Math.Round(ghost.X), (int)Math.Round(ghost.Y));
            Assert.True(r > 90 && r < 220 && Math.Abs(r - g) <= 16 && Math.Abs(g - b) <= 16,
                $"before-state ghost must be neutral gray, got ({r},{g},{b})");
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task DiffToggle_Off_ClearsOverlay()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            await EnableDiffAsync(window);
            Assert.NotNull(window.Canvas!.DiffOverlay);
            ClickDiff(window); // uncheck
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(true, window.DiffToggle.IsChecked);
            Assert.Null(window.Canvas.DiffOverlay);
            Assert.DoesNotContain("Diff +", window.Status!.Text);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void DiffToggle_NoSelection_ShowsErrorAndReverts()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ClickDiff(window);
            Assert.True(window.AuditError!.IsVisible);
            Assert.Contains("Select a transaction", window.AuditError.Text);
            Assert.NotEqual(true, window.DiffToggle.IsChecked);
            Assert.Null(window.Canvas!.DiffOverlay);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task Diff_SnapshotMissing_ShowsErrorGracefully()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Directory.Delete(Path.Combine(f.ProjectDir, ".cadagent", "snapshots", f.Tx1Id), true);
            await EnableDiffAsync(window);
            Assert.True(window.AuditError!.IsVisible);
            Assert.NotEmpty(window.AuditError.Text);
            Assert.NotEqual(true, window.DiffToggle!.IsChecked);
            Assert.Null(window.Canvas!.DiffOverlay);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void NoAuditDb_OpensEmptyTimeline()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-audit-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        string path = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) }));
        var window = new MainWindow(new MainViewModel(path, projectDir));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.Audit);
            Assert.Null(window.Audit!.ErrorMessage);
            Assert.Equal(0, window.AuditList!.ItemCount);
        }
        finally
        {
            window.Close();
            File.Delete(path);
            Directory.Delete(projectDir, true);
        }
    }

    /// <summary>Max of one RGBA channel (0=R,1=G,2=B; headless frames are Rgba8888)
    /// over EVERY pixel in the given screen box, bounds-checked.</summary>
    private static int MaxChannelInRect(WriteableBitmap frame, double x0, double y0, double x1, double y1, int channel)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int max = 0;
        for (int y = (int)Math.Round(y0); y <= (int)Math.Round(y1); y++)
            for (int x = (int)Math.Round(x0); x <= (int)Math.Round(x1); x++)
            {
                if (x < 0 || y < 0 || x >= fb.Size.Width || y >= fb.Size.Height) continue;
                max = Math.Max(max, Marshal.ReadByte(fb.Address, y * fb.RowBytes + x * 4 + channel));
            }
        return max;
    }

    /// <summary>RGB of the brightest pixel in a 5x5 neighborhood (AA-tolerant probe).</summary>
    private static (int R, int G, int B) BrightestPixel(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int best = -1, br = 0, bg = 0, bb = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= fb.Size.Width || py >= fb.Size.Height) continue;
                int offset = py * fb.RowBytes + px * 4;
                int b = Marshal.ReadByte(fb.Address, offset);
                int g = Marshal.ReadByte(fb.Address, offset + 1);
                int r = Marshal.ReadByte(fb.Address, offset + 2);
                if (r + g + b > best) { best = r + g + b; br = r; bg = g; bb = b; }
            }
        return (br, bg, bb);
    }
}
