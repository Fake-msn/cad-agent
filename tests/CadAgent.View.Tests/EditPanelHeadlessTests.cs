using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// Plan 4 Task 9 headless coverage ([AvaloniaFact], real Skia frames):
/// MainWindow edit toolbar builds all seven tools + Undo (disabled until a
/// transaction exists). Line tool: two real canvas clicks add an entity via
/// StepEngine (no bypass) — canvas items, layer panel (T4 carryover Refresh)
/// and status bar all rebuild; without audit.db the first edit lazily creates
/// the hash-chained trail (view-edit request BEFORE tx-start, tx-commit).
/// Undo button restores the drawing + rollback audit. Delete tool: click
/// selects, second toolbar press confirms. Text tool: toolbar input + click.
/// Fixture: one WALL line (0,0)-(100,0); identity FixedIdentity Draw.
/// </summary>
public class EditPanelHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string AuditDbPath => Path.Combine(AgentDir, "audit.db");

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-edit-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
            });
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

    private static (MainWindow Window, Fixture F) ShowWindow()
    {
        var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, f);
    }

    private static void Cleanup(MainWindow window, Fixture f)
    {
        window.Close();
        f.Dispose();
    }

    private static Button ToolButton(MainWindow window, string label) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(b => b is not CheckBox && (b.Content as string) == label);

    /// <summary>Run a UI action that triggers an edit and await EditCompleted (T8 pattern).</summary>
    private static async Task ActAndWaitForEditAsync(MainWindow window, Action action)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        window.Edit!.EditCompleted += Handler;
        try
        {
            action();
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Edit!.EditCompleted -= Handler; }
    }

    private static void ClickCanvasAtWorld(MainWindow window, double wx, double wy)
    {
        DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
        (double sx, double sy) = window.Canvas!.WorldToScreen(wx, wy);
        Point p = canvas.TranslatePoint(new Point(sx, sy), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Toolbar_BuildsAllTools_UndoDisabledByDefault()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.NotNull(window.Edit);
            Assert.NotNull(window.EditToolbar);
            Assert.Equal(EditTool.Select, window.Edit!.ActiveTool);
            foreach (string label in new[] { "Select", "Line", "Text", "Move", "Delete", "ChangeLayer", "InsertBlock", "Undo" })
                Assert.True(ToolButton(window, label).IsVisible, $"toolbar button {label}");
            Assert.False(window.UndoButton!.IsEnabled); // no transaction yet
            Assert.NotNull(window.EditTextInput);
            Assert.NotNull(window.EditBlockInput);
            Assert.NotNull(window.EditLayerSelector);
            Assert.False(window.EditError!.IsVisible);
            Assert.False(File.Exists(f.AuditDbPath)); // opening the window creates nothing (T6/T8)

            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
            ToolButton(window, "Select").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Select, window.Edit.ActiveTool);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task LineTool_TwoClicks_AddsEntity_RefreshesViews_LazyAuditTrail()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.Single(window.Canvas!.Items);
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ClickCanvasAtWorld(window, 20, 20); // first click -> pending preview point
            Assert.NotNull(window.Canvas.PendingPoint);
            (double px, double py) = window.Canvas.PendingPoint!.Value;
            Assert.Equal(20, px, 3); // screen<->world roundtrip tolerance
            Assert.Equal(20, py, 3);
            Assert.Single(window.Canvas.Items); // nothing committed yet

            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 80, 30));

            Assert.Null(window.Canvas.PendingPoint); // preview cleared
            Assert.Null(window.Edit!.ErrorMessage);
            Assert.False(window.EditError!.IsVisible);
            // Entity added through StepEngine and persisted; views rebuilt (T4 carryover).
            Assert.Equal(2, window.Canvas.Items.Count);
            Assert.Equal(2, window.Layers!.Layers.Count);
            Assert.Equal(new[] { "0", "WALL" }, window.Layers.Layers.Select(l => l.Name).ToArray());
            Assert.Equal(new[] { 1, 1 }, window.Layers.Layers.Select(l => l.EntityCount).ToArray());
            Assert.Equal(2, window.LayerList!.ItemCount);
            Assert.Contains("2 render items", window.Status!.Text);
            Assert.True(window.UndoButton!.IsEnabled);
            Assert.Single(window.Edit.RecentTxIds);

            Assert.True(File.Exists(f.AuditDbPath)); // first edit lazily creates the trail
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            List<AuditRecord> records = audit.ReadAll().ToList();
            AuditRecord req = Assert.Single(records, r => r.Entry.Kind == "view-edit");
            AuditRecord start = Assert.Single(records, r => r.Entry.Kind == "tx-start");
            Assert.Single(records, r => r.Entry.Kind == "tx-commit");
            Assert.True(req.Seq < start.Seq); // request audit before tx
            Assert.Equal("view", req.Entry.Source);
            Assert.Equal("tester", req.Entry.Operator);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task UndoButton_RestoresDrawing_AuditsRollback()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClickCanvasAtWorld(window, 20, 20);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 80, 30));
            Assert.Equal(2, window.Canvas!.Items.Count);
            string txId = window.Edit!.RecentTxIds[0];

            await ActAndWaitForEditAsync(window, () =>
                window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Single(window.Canvas.Items); // restored render
            // T8 图层表并集：撤销后编辑用层 "0" 仍在面板（空层计数 0），WALL 计数恢复 1。
            Assert.Equal(new[] { "0", "WALL" }, window.Layers!.Layers.Select(l => l.Name).ToArray());
            Assert.Equal(0, window.Layers.Layers[0].EntityCount);
            Assert.Equal(1, window.Layers.Layers.Single(l => l.Name == "WALL").EntityCount);
            Assert.Empty(window.Edit.RecentTxIds);
            Assert.False(window.UndoButton!.IsEnabled);
            Assert.Contains("1 render items", window.Status!.Text);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "rollback");
            Assert.Equal(txId, rec.Entry.ParamsJson);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task DeleteTool_ClickSelects_SecondPressConfirms()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Delete, window.Edit!.ActiveTool);

            ClickCanvasAtWorld(window, 50, 0); // on the fixture line -> selects, no edit yet
            Assert.NotNull(window.Canvas!.SelectedHandle);
            Assert.Single(window.Canvas.Items);

            // Second press of the Delete toolbar button = confirmation.
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Empty(window.Canvas.Items);
            // T8 图层表并集：删空实体后图层仍显示为空层（计数 0），面板不再清空。
            Assert.Equal(new[] { "0", "WALL" }, window.Layers!.Layers.Select(l => l.Name).ToArray());
            Assert.All(window.Layers.Layers, l => Assert.Equal(0, l.EntityCount));
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord step = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "step");
            Assert.Contains("delete-entity", step.Entry.ParamsJson);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task TextTool_EmptyInputGuidance_ThenClickAddsText()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ClickCanvasAtWorld(window, 30, 10); // empty input -> guidance, no edit/audit
            Assert.True(window.EditError!.IsVisible);
            Assert.False(File.Exists(f.AuditDbPath));
            Assert.Single(window.Canvas!.Items);

            window.EditTextInput!.Text = "新注记";
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 30, 10));

            Assert.False(window.EditError!.IsVisible);
            Assert.Equal(2, window.Canvas.Items.Count);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord step = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "step");
            Assert.Contains("add-text", step.Entry.ParamsJson);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }
}
