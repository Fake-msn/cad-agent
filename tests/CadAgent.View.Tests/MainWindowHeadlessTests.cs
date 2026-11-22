using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// Plan 4 Task 4 headless coverage ([AvaloniaFact], real Skia frames):
/// MainWindow layer panel builds ordered entries (checkbox + swatch + count),
/// checkbox click toggles layer visibility out of CanvasViewModel.VisibleItems,
/// double-click isolates, Show all restores, and canvas left-click selection
/// updates the status bar with the real DXF handle / kind / layer.
/// </summary>
public class MainWindowHeadlessTests
{
    /// <summary>ALPHA: 2 red lines (y=0, y=2); BETA: 1 green line (y=20).</summary>
    private static string TwoLayerDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var alpha = new Layer("ALPHA") { Color = new ACadSharp.Color(1) };
        var beta = new Layer("BETA") { Color = new ACadSharp.Color(3) };
        doc.Layers.Add(alpha);
        doc.Layers.Add(beta);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0), Layer = alpha });
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 2, 0), EndPoint = new XYZ(10, 2, 0), Layer = alpha });
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(10, 20, 0), Layer = beta });
    });

    private static (MainWindow Window, MainViewModel Vm, string Path) ShowWindow()
    {
        string path = TwoLayerDxf();
        var vm = new MainViewModel(path, Path.GetTempPath());
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, path);
    }

    private static void Cleanup(MainWindow window, string path)
    {
        window.Close();
        File.Delete(path);
    }

    [AvaloniaFact]
    public void LayerPanel_BuildsOrderedEntries_WithCheckboxesAndCounts()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            Assert.NotNull(window.Layers);
            Assert.NotNull(window.LayerList);
            // T8 图层表并集：默认空层 "0" 也出现（计数 0，表色样白）。
            Assert.Equal(new[] { "0", "ALPHA", "BETA" }, window.Layers!.Layers.Select(l => l.Name).ToArray());
            Assert.Equal(new[] { 0, 2, 1 }, window.Layers.Layers.Select(l => l.EntityCount).ToArray());
            Assert.Equal("#FF0000", window.Layers.Layers[1].ColorBrush);
            Assert.Equal("#00FF00", window.Layers.Layers[2].ColorBrush);
            Assert.Equal(3, window.LayerList!.ItemCount);
            List<CheckBox> checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(3, checks.Count);
            Assert.All(checks, c => Assert.True(c.IsChecked));
            Assert.Contains("render items", window.Status!.Text);
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_CheckboxClick_TogglesLayerOutOfVisibleItems()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            CheckBox first = window.GetVisualDescendants().OfType<CheckBox>().ElementAt(1); // ALPHA（T8：索引 0 为空层 "0"）
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("ALPHA", window.Canvas!.HiddenLayers);
            Assert.False(window.Layers!.Layers.Single(l => l.Name == "ALPHA").IsVisible);
            Assert.False(first.IsChecked); // refreshed from entry state
            Assert.All(window.Canvas.VisibleItems, i => Assert.Equal("BETA", i.Layer));
            Assert.Single(window.Canvas.VisibleItems);

            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // toggle back
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Canvas.HiddenLayers);
            Assert.True(first.IsChecked);
            Assert.Equal(3, window.Canvas.VisibleItems.Count());
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_DoubleClick_IsolatesSelectedLayer()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            // Real double-click on the BETA row (right side, away from the checkbox):
            // two rapid click pairs at the same point -> MouseDevice click count 2
            // -> DoubleTapped bubbles from ListBoxItem to the ListBox.
            // T8：索引 0 为空层 "0"，BETA 行索引 2。
            var row = (Control)window.LayerList!.ContainerFromIndex(2)!;
            Point p = row.TranslatePoint(new Point(row.Bounds.Width - 20, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Layers!.Layers.Single(l => l.Name == "ALPHA").IsVisible); // ALPHA hidden
            Assert.True(window.Layers.Layers.Single(l => l.Name == "BETA").IsVisible);
            Assert.All(window.Canvas!.VisibleItems, i => Assert.Equal("BETA", i.Layer));
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_DoubleClickOnName_Isolates()
    {
        // T10 真机验证发现：双击图层名（旧结构在 CheckBox 内容区）被 Toggle 消费
        // （两次翻转抵消）且 DoubleTapped 不冒泡 → 隔离失效。名字区双击必须隔离。
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            var row = (Control)window.LayerList!.ContainerFromIndex(2)!; // T8：BETA 行索引 2（0 为空层 "0"）
            Point p = row.TranslatePoint(new Point(60, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Layers!.Layers.Single(l => l.Name == "ALPHA").IsVisible); // ALPHA hidden
            Assert.True(window.Layers.Layers.Single(l => l.Name == "BETA").IsVisible);
            Assert.All(window.Canvas!.VisibleItems, i => Assert.Equal("BETA", i.Layer));
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_ShowAllButton_RestoresAllLayers()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            window.Layers!.Isolate("BETA");
            Dispatcher.UIThread.RunJobs();
            // T6 起底部还有 Diff ToggleButton（也是 Button）——按内容精确定位 Show all。
            Button showAll = window.GetVisualDescendants().OfType<Button>()
                .First(b => b is not CheckBox && (b.Content as string) == "Show all");
            showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Canvas!.HiddenLayers);
            Assert.All(window.Layers.Layers, l => Assert.True(l.IsVisible));
            Assert.Equal(3, window.Canvas.VisibleItems.Count());
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void CanvasSelection_UpdatesStatusBar_WithHandleKindLayer()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            string expectedHandle = vm.Session.Doc.Entities.First().Handle.ToString();

            (double sx, double sy) = window.Canvas!.WorldToScreen(5, 0); // on first ALPHA line
            Point onLine = canvas.TranslatePoint(new Point(sx, sy), window)!.Value;
            window.MouseDown(onLine, MouseButton.Left);
            window.MouseUp(onLine, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(expectedHandle, window.Canvas.SelectedHandle);
            Assert.Contains(expectedHandle, window.Status!.Text);
            Assert.Contains("Line", window.Status.Text);
            Assert.Contains("ALPHA", window.Status.Text);

            Point empty = canvas.TranslatePoint(new Point(5, 5), window)!.Value; // background corner
            window.MouseDown(empty, MouseButton.Left);
            window.MouseUp(empty, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.Canvas.SelectedHandle);
            Assert.DoesNotContain("Selected", window.Status.Text);
        }
        finally { Cleanup(window, path); }
    }

    // ---- 视图闭环 T5：审计 live-rebuild + _diffBusy 硬闸 ----

    /// <summary>T5 夹具（镜像 AuditPanelHeadlessTests/IntegrationHeadlessTests）：
    /// 临时项目目录 + .cadagent/audit.db（真实 StepEngine 事务一组，含快照）。</summary>
    private sealed class AuditFixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public SqliteAuditStore Audit { get; }
        public SnapshotStore Snapshots { get; }
        public string TxId { get; }

        public AuditFixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-t5-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ProjectDir, ".cadagent"));
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(100, 50, 0) });
            });
            var session = DrawingSession.Load(DxfPath);
            Audit = new SqliteAuditStore(Path.Combine(ProjectDir, ".cadagent", "audit.db"));
            Snapshots = new SnapshotStore(Path.Combine(ProjectDir, ".cadagent", "snapshots"));
            var engine = new StepEngine(Audit, Snapshots,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), HandlerRegistry.All());
            TxId = engine.Run(Plan("p1",
                ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}""")), session, "s1").TxId;
        }

        public void Dispose()
        {
            Audit.Dispose();
            // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
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

    private static (MainWindow Window, AuditFixture F) ShowAuditWindow(SemaphoreSlim? gate = null)
    {
        var f = new AuditFixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)), gate);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, f);
    }

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

    /// <summary>真实指针点击 Diff 切换（镜像 AuditPanelHeadlessTests：RaiseEvent(ClickEvent)
    /// 只触发 Click 订阅，不走 Button.OnClick → ToggleButton.Toggle，勾选态须走指针管线）。</summary>
    private static void ClickDiff(MainWindow window)
    {
        ToggleButton toggle = window.DiffToggle!;
        Point p = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task DiffLoading_EditClicks_IgnoredByHardGuard()
    {
        // 注入会话闸并持闸 → Diff 加载确定性 in-flight（_diffBusy 置位），
        // 镜像 T10“注入会话闸持闸制造确定性 in-flight 编辑”测试的构造法。
        var gate = new SemaphoreSlim(1, 1);
        (MainWindow window, AuditFixture f) = ShowAuditWindow(gate);
        try
        {
            // 先经审计管线完成一次真实编辑（撤销栈非空，Undo 守卫可观察）。
            var editTcs = new TaskCompletionSource();
            void EditHandler() => editTcs.TrySetResult();
            window.Edit!.EditCompleted += EditHandler;
            try
            {
                ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                ClickCanvasAtWorld(window, 20, 20); // 锚点
                ClickCanvasAtWorld(window, 80, 30); // 提交 → 编辑完成
                await editTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { window.Edit.EditCompleted -= EditHandler; }
            Assert.Null(window.Edit.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            int viewEditsBefore = f.Audit.ReadAll().Count(r => r.Entry.Kind == "view-edit");
            Assert.Equal(1, viewEditsBefore);

            // 选中既有事务 → 持闸 → Diff 加载 in-flight。
            window.AuditList!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            var diffTcs = new TaskCompletionSource();
            void DiffHandler() => diffTcs.TrySetResult();
            window.Audit!.DiffChanged += DiffHandler;
            try
            {
                await gate.WaitAsync(); // 持闸：BuildDiff 停在会话闸上，diff 无法完成
                try
                {
                    ClickDiff(window);
                    Assert.False(window.DiffToggle!.IsEnabled); // diff 加载 UI 状态
                    Assert.False(window.EditToolbar!.IsEnabled);

                    // 画布工具点击一律忽略：不落预览锚点、不产生新 view-edit。
                    ClickCanvasAtWorld(window, 20, 20);
                    Assert.Null(window.Canvas!.PendingPoint);
                    ClickCanvasAtWorld(window, 80, 30);
                    Assert.Equal(viewEditsBefore, f.Audit.ReadAll().Count(r => r.Entry.Kind == "view-edit"));
                    // 工具切换/删除确认一律忽略（RaiseEvent 绕过 IsEnabled=false）。
                    ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
                    ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
                    // 撤销一律忽略（释闸后也不得发生 rollback）。
                    window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Single(window.Edit.RecentTxIds);

                    gate.Release();
                    await diffTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    // headless 无 SynchronizationContext：ApplyDiffAsync 的 await 续体
                    // 在线程池执行（与测试并发）——轮询直至 finally 复位 IsEnabled
                    // （成功路径此时叠加层已施加），再 RunJobs 刷新调度队列。
                    for (int i = 0; i < 100 && !window.DiffToggle!.IsEnabled; i++)
                        await Task.Delay(10);
                    Dispatcher.UIThread.RunJobs();
                }
                finally
                {
                    if (gate.CurrentCount == 0) gate.Release();
                }
            }
            finally { window.Audit.DiffChanged -= DiffHandler; }

            // Diff 完成：叠加层施加、禁用态复位；被忽略的编辑/撤销未留任何痕迹。
            Assert.NotNull(window.Canvas!.DiffOverlay);
            Assert.True(window.DiffToggle!.IsEnabled);
            Assert.True(window.EditToolbar!.IsEnabled);
            Assert.Single(window.Edit.RecentTxIds);
            Assert.Equal(viewEditsBefore, f.Audit.ReadAll().Count(r => r.Entry.Kind == "view-edit"));
            Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "rollback");
        }
        finally
        {
            window.Close();
            f.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task EditCompleted_AuditTimeline_LiveRebuilds()
    {
        (MainWindow window, AuditFixture f) = ShowAuditWindow();
        try
        {
            // 构造期快照：仅既有 tx。
            Assert.Equal(1, window.AuditList!.ItemCount);
            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            window.Edit!.EditCompleted += Handler;
            EditResult result;
            try
            {
                result = await window.Edit.DrawLineAsync(0, 80, 100, 80);
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { window.Edit.EditCompleted -= Handler; }
            Assert.True(result.Ok);
            Assert.Null(window.Edit.ErrorMessage);

            // RefreshAfterEdit → Audit.Rebuild → TimelineChanged → 列表 live 刷新（无需重开窗口）。
            Assert.Equal(2, window.AuditList.ItemCount);
            Assert.Same(window.Audit!.Transactions, window.AuditList.ItemsSource);
            Assert.Contains(window.Audit.Transactions, g => g.TxId == result.TxId);
        }
        finally
        {
            window.Close();
            f.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task NoAuditDb_FirstEdit_AuditTimeline_LiveRebuilds()
    {
        // Fix Round 1 钉住（controller 核实缺口）：无 audit.db 临时项目开窗 →
        // 读/写审计必须共享同一 LazyWriteAuditStore 实例——首 tx 写入后 Rebuild
        // 立即可见（live），无需重开窗口。夹具镜像 EditPanelHeadlessTests.Fixture
        // （.cadagent 目录存在但无 audit.db；开窗不建库约定不变）。
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-t5fix-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".cadagent"));
        string dxfPath = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        });
        string auditDbPath = Path.Combine(projectDir, ".cadagent", "audit.db");
        MainWindow? window = null;
        try
        {
            var vm = new MainViewModel(dxfPath, projectDir);
            Assert.Null(vm.ErrorMessage);
            window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(File.Exists(auditDbPath)); // 打开窗口不建库（Ensure 仅首 Append 触发）
            Assert.Equal(0, window.AuditList!.ItemCount); // 创建前空库语义（等价 EmptyAuditStore）

            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            window.Edit!.EditCompleted += Handler;
            EditResult result;
            try
            {
                result = await window.Edit.DrawLineAsync(0, 80, 100, 80);
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { window.Edit.EditCompleted -= Handler; }
            Assert.True(result.Ok);
            Assert.Null(window.Edit.ErrorMessage);
            Assert.True(File.Exists(auditDbPath)); // 首 Append 惰性建库

            // 首 tx live：Rebuild 重读同一实例 → 时间线含新 tx（无需重开窗口）。
            Assert.Equal(1, window.AuditList.ItemCount);
            Assert.Same(window.Audit!.Transactions, window.AuditList.ItemsSource);
            Assert.Contains(window.Audit.Transactions, g => g.TxId == result.TxId);
        }
        finally
        {
            window?.Close();
            // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dxfPath);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }
}
