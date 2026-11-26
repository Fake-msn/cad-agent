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

    private static (MainWindow Window, MainViewModel Vm, string Path, string ProjectDir) ShowWindow()
    {
        string path = TwoLayerDxf();
        // 合并前审查：项目目录按测试隔离（sidecar 落 <project>/.cadagent，不共享 %TEMP%）。
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-mw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        var vm = new MainViewModel(path, projectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, path, projectDir);
    }

    private static void Cleanup(MainWindow window, string path, string projectDir)
    {
        window.Close();
        File.Delete(path);
        if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
    }

    [AvaloniaFact]
    public void MainWindow_ConsumesPrebuiltItemsAndSheets_NoUiThreadBuild()
    {
        // 终审 Important 2：MainWindow 直接消费 MainViewModel 预构建的 Items/Sheets
        // （同一实例引用）——证明加载期 Build 未在 UI 线程重复执行。
        string path = TwoLayerDxf();
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-mw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        var vm = new MainViewModel(path, projectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        try
        {
            Assert.Same(vm.Items, window.FullModelItemsForTests);
            Assert.Same(vm.Sheets, window.SheetsForTests);
            Assert.NotEmpty(window.FullModelItemsForTests);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void LayerPanel_BuildsOrderedEntries_WithCheckboxesAndCounts()
    {        (MainWindow window, MainViewModel vm, string path, string projectDir) = ShowWindow();
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
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void LayerPanel_CheckboxClick_TogglesLayerOutOfVisibleItems()
    {
        (MainWindow window, MainViewModel vm, string path, string projectDir) = ShowWindow();
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
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void LayerPanel_DoubleClick_IsolatesSelectedLayer()
    {
        (MainWindow window, MainViewModel vm, string path, string projectDir) = ShowWindow();
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
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void LayerPanel_DoubleClickOnName_Isolates()
    {
        // T10 真机验证发现：双击图层名（旧结构在 CheckBox 内容区）被 Toggle 消费
        // （两次翻转抵消）且 DoubleTapped 不冒泡 → 隔离失效。名字区双击必须隔离。
        (MainWindow window, MainViewModel vm, string path, string projectDir) = ShowWindow();
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
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void LayerPanel_ShowAllButton_RestoresAllLayers()
    {
        (MainWindow window, MainViewModel vm, string path, string projectDir) = ShowWindow();
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
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void CanvasSelection_UpdatesStatusBar_WithHandleKindLayer()
    {
        (MainWindow window, MainViewModel vm, string path, string projectDir) = ShowWindow();
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
        finally { Cleanup(window, path, projectDir); }
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

    // ---- 视图闭环 T10：④纠偏计划预览面板（fake client 注入，不触网） ----

    [AvaloniaFact]
    public async Task PlanPanel_FakeClient_PreviewThenConfirm_ExecutesAndRefreshes()
    {
        // 夹具：临时项目 + 预建 audit.db（窗口走 SqliteAuditStore 分支）+ 单线图纸。
        // 注入 PlanPreviewViewModel（fake client 返回 add-line 计划 + 本地端点 profile）；
        // 注入 VM 与窗口各持同一 audit.db 的 SqliteAuditStore 实例——Append 在
        // Serializable 事务内读链头（跨实例安全），Rebuild 经窗口实例读已提交数据。
        string projectDir = Path.Combine(Path.GetTempPath(), "cadagent-t10-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".cadagent"));
        string dxfPath = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        });
        string auditDbPath = Path.Combine(projectDir, ".cadagent", "audit.db");
        using (var seed = new SqliteAuditStore(auditDbPath)) { } // 预建库文件
        MainWindow? window = null;
        SqliteAuditStore? planAudit = null;
        try
        {
            var vm = new MainViewModel(dxfPath, projectDir);
            Assert.Null(vm.ErrorMessage);
            planAudit = new SqliteAuditStore(auditDbPath);
            var gate = new SemaphoreSlim(1, 1);
            var identity = new FixedIdentity(new OperatorIdentity("tester", Role.Draw));
            var planVm = new PlanPreviewViewModel(vm.Session, projectDir, planAudit, identity,
                new RoleChecker(), gate,
                new FakeLlmClient("""{"steps":[{"kind":"add-line","params":{"x1":0,"y1":5,"x2":10,"y2":5}}]}"""),
                new ProviderProfile("local", "http://localhost:11434/v1", "fake-model",
                    VisionCapable: false, DataPolicy.LocalOnly, new ProfileLimits()));
            window = new MainWindow(vm, identity, gate, planVm);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(planVm, window.PlanPreview);
            int before = window.Canvas!.Items.Count;

            // 切到"计划"tab（第四 tab）——面板进入视觉树。
            window.LeftTabs!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            window.PlanIntentInput!.Text = "在 y=5 加一条线";

            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var issuesTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnReady() => readyTcs.TrySetResult();
            void OnDone() => doneTcs.TrySetResult();
            void OnIssues() => issuesTcs.TrySetResult();
            planVm.PreviewReady += OnReady;
            planVm.RunCompleted += OnDone;
            window.Issues!.IssuesChanged += OnIssues;
            try
            {
                window.GeneratePlanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();

                // 预览面板可见步骤文本（kind + params JSON），尚未执行。
                Assert.True(planVm.IsPreviewVisible);
                Assert.True(window.PlanPreviewSection!.IsVisible);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text is not null && t.Text.Contains("add-line"));
                Assert.Equal(before, window.Canvas.Items.Count); // 预览不改图

                window.ConfirmPlanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                // 最终审查顺手修（T10 deferred）：确认后立即禁用确认/拒绝按钮，
                // 至下次 PreviewReady 才重新启用（防纠正循环二次 confirmPlan 时确认未见计划）。
                Assert.False(window.ConfirmPlanButton!.IsEnabled);
                Assert.False(window.RejectPlanButton!.IsEnabled);
                await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
                // PlanExecuted 联动的问题清单重评完成（check-run 审计落盘后再清理夹具）。
                await issuesTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                planVm.PreviewReady -= OnReady;
                planVm.RunCompleted -= OnDone;
                window.Issues.IssuesChanged -= OnIssues;
            }

            // 执行成功：状态 Executed + 画布渲染图元 +1 + 审计时间线含新 tx（live-rebuild）。
            Assert.Equal("Executed", planVm.Status);
            Assert.False(string.IsNullOrEmpty(planVm.LastTxId));
            Assert.Equal(before + 1, window.Canvas.Items.Count);
            Assert.Contains(window.Audit!.Transactions, g => g.TxId == planVm.LastTxId);
            Assert.False(window.PlanPreviewSection!.IsVisible); // 编排结束预览收起
            Assert.True(planAudit.VerifyChain(out string error), error);
        }
        finally
        {
            window?.Close();
            planAudit?.Dispose();
            // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dxfPath);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
        }
    }

    // ---- §4.1④ 勾选→意图（checked 优先 + force 标签 + 零勾选兜底 + rule-adopted 采纳审计） ----

    /// <summary>勾选→意图夹具（镜像 PlanPanel 测试装配）：2-issue 图纸（字高 4.2 +
    /// W-墙体 混排层名）+ 裁定升级 R-50001-5.0.2 → Mandatory（mandatory+recommended
    /// 混合）+ 预建 audit.db + fake client PlanPreviewViewModel（不触网）。
    /// elevationCollision=true（I1 碰撞夹具）：字高违规文本换成标高 "+3.50"（Height=3
    /// 合规避免字高 issue）+ mode 省略 elevation skill 规则 + overlay Annotation →
    /// 同 RuleId 同 Targets 异 Message 两条 issue（小数位 vs 符号）。</summary>
    private sealed class IssueIntentFixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public SqliteAuditStore Audit { get; }
        public MainViewModel Vm { get; }
        public MainWindow Window { get; }
        public PlanPreviewViewModel PlanVm { get; }

        /// <summary>注入窗口的共享会话闸（I2 busy 测试持闸制造确定性在途编辑）。</summary>
        public SemaphoreSlim Gate { get; }

        /// <summary>elevationCollision 模式的临时 skill 目录（Dispose 清理）。</summary>
        public string? SkillDir { get; }

        public IssueIntentFixture(bool elevationCollision = false)
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-issue-intent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ProjectDir, ".cadagent"));
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                doc.Entities.Add(elevationCollision
                    ? new TextEntity { InsertPoint = new XYZ(20, 20, 0), Value = "+3.50", Height = 3 }
                    : new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            });
            var adj = new AdjudicationSet();
            adj.Upsert(new Adjudication("R-50001-5.0.2", "tester", DateTimeOffset.UtcNow,
                "测试升级", ForceOverride: RuleForce.Mandatory));
            adj.Save(Path.Combine(ProjectDir, ".cadagent", "adjudications.json"));
            string auditDbPath = Path.Combine(ProjectDir, ".cadagent", "audit.db");
            using (var seed = new SqliteAuditStore(auditDbPath)) { } // 预建库文件（窗口走 SqliteAuditStore 分支）
            Audit = new SqliteAuditStore(auditDbPath);
            if (elevationCollision) SkillDir = MakeElevationSkillDir();
            Vm = new MainViewModel(DxfPath, ProjectDir, SkillDir is null ? null : new[] { SkillDir });
            Assert.Null(Vm.ErrorMessage);
            if (elevationCollision)
            {
                // overlay Annotation 标注标高文本（ElevationChecker IOverlayDependent 前提）。
                string handle = Vm.Session.Doc.Entities.OfType<TextEntity>()
                    .Single(t => t.Value == "+3.50").Handle.ToString();
                var overlay = new SemanticOverlay();
                overlay.UpsertAnnotation(new SemanticAnnotation(handle, SemanticClass.Annotation,
                    Provenance.HumanConfirmed, 0.9, "测试标注"));
                overlay.Save(Path.Combine(ProjectDir, ".cadagent", "semantic.json"));
            }
            Gate = new SemaphoreSlim(1, 1);
            var identity = new FixedIdentity(new OperatorIdentity("tester", Role.Draw));
            PlanVm = new PlanPreviewViewModel(Vm.Session, ProjectDir, Audit, identity,
                new RoleChecker(), Gate,
                new FakeLlmClient("""{"steps":[{"kind":"add-line","params":{"x1":0,"y1":5,"x2":10,"y2":5}}]}"""),
                new ProviderProfile("local", "http://localhost:11434/v1", "fake-model",
                    VisionCapable: false, DataPolicy.LocalOnly, new ProfileLimits()));
            Window = new MainWindow(Vm, identity, Gate, PlanVm);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>点击 Refresh 并等待 IssuesChanged（async Task.Run 路径，镜像 IssuesPanelHeadlessTests）。</summary>
        public async Task RefreshIssuesAsync()
        {
            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            Window.Issues!.IssuesChanged += Handler;
            try
            {
                Window.RefreshIssuesButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { Window.Issues!.IssuesChanged -= Handler; }
        }

        public void Dispose()
        {
            Window.Close();
            Audit.Dispose();
            // Microsoft.Data.Sqlite 连接池持有文件句柄，Windows 下删临时目录前须清池。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
            if (SkillDir is not null && Directory.Exists(SkillDir)) Directory.Delete(SkillDir, true);
        }
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

    private static void ClickFillIntent(MainWindow window)
    {
        window.FillIntentButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task FillIntent_CheckedMixed_ForceTagFormat_AndCheckboxBinding()
    {
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        IssueEntry mandatory = window.Issues!.Issues.Single(i => i.RuleId == "R-50001-5.0.2");
        IssueEntry recommended = window.Issues!.Issues.Single(i => i.RuleId == "R-50001-13.0.1");
        Assert.Equal(RuleForce.Mandatory, mandatory.Force);
        Assert.Equal(RuleForce.Recommended, recommended.Force);
        mandatory.IsChecked = true;
        recommended.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        // TwoWay 绑定 source→target：问题行 CheckBox 同步勾选（图层面板复选框不在 IssueList 内）。
        List<CheckBox> issueChecks = window.IssueList!.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(2, issueChecks.Count);
        Assert.All(issueChecks, c => Assert.True(c.IsChecked));

        ClickFillIntent(window);
        // 行格式逐字：{RuleId} [{Force}]: {Suggestion}（Force 枚举名原文），顺序=清单顺序。
        string expected = string.Join("\n", window.Issues.Issues
            .Where(i => i.IsChecked)
            .Select(i => $"{i.RuleId} [{i.Force}]: {i.Suggestion}"));
        Assert.Equal(expected, window.PlanIntentInput!.Text);
        Assert.Contains($"R-50001-5.0.2 [Mandatory]: {mandatory.Suggestion}", window.PlanIntentInput.Text);
        Assert.Contains($"R-50001-13.0.1 [Recommended]: {recommended.Suggestion}", window.PlanIntentInput.Text);
        Assert.Equal(2, window.PlanIntentInput!.Text!.Split('\n').Length);
    }

    [AvaloniaFact]
    public async Task IssueRow_CheckboxPointerClick_TogglesEntryIsChecked()
    {
        // TwoWay 绑定 target→source：真实指针点击行复选框 → IssueEntry.IsChecked 翻转。
        // M1（fixwave2）：点第二行复选框 → 断言对应第二条目翻转（闭合"绑定正确条目"透镜，
        // 首行点击无法区分"全部绑到第一条目"的错误实现）。
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        List<CheckBox> checks = window.IssueList!.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(2, checks.Count);
        CheckBox check = checks[1]; // 第二行
        // sln 并行负载下第二行容器 arrange 可能晚于首次 RunJobs（Bounds 空/与首行重叠 →
        // 点击点失真误点首行）：泵消息直至两行容器均有非零尺寸且位置分离（有界防挂死）。
        bool laidOut = false;
        for (int i = 0; i < 200 && !laidOut; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (checks[0].Bounds.Width > 0 && check.Bounds.Width > 0)
            {
                Point a = checks[0].TranslatePoint(new Point(0, 0), window)!.Value;
                Point b = check.TranslatePoint(new Point(0, 0), window)!.Value;
                laidOut = a != b;
            }
            if (!laidOut) await Task.Delay(5);
        }
        Assert.True(laidOut); // 布局确定性前提（失败=环境异常而非绑定缺陷）
        Point p = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(check.IsChecked);
        Assert.True(window.Issues!.Issues[1].IsChecked); // 对应第二条目翻转
        Assert.False(window.Issues.Issues[0].IsChecked); // 第一条目不受牵连
        Assert.Single(window.Issues.Issues.Where(i => i.IsChecked));
    }

    [AvaloniaFact]
    public async Task FillIntent_NoChecks_MandatoryFallback_OldFormatAndStatusNote()
    {
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        IssueEntry mandatory = window.Issues!.Issues.Single(i => i.Force == RuleForce.Mandatory);
        ClickFillIntent(window);
        // 旧格式逐字保持（既有行为）：仅 mandatory、无 force 标签。
        Assert.Equal($"{mandatory.RuleId}: {mandatory.Suggestion}", window.PlanIntentInput!.Text);
        Assert.Contains("未勾选问题，已按 mandatory 自动填充", window.Status!.Text);
        // 零勾选兜底不留采纳审计（义务非采纳）。
        Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adopted");
    }

    [AvaloniaFact]
    public async Task FillIntent_CheckedRecommended_WritesRuleAdoptedAudit()
    {
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        IssueEntry recommended = window.Issues!.Issues.Single(i => i.RuleId == "R-50001-13.0.1");
        recommended.IsChecked = true;
        ClickFillIntent(window);

        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adopted");
        AuditEntry e = rec.Entry;
        Assert.Equal("rule", e.Source); // 镜像 rule-adjudicated 先例（Source=rule）
        Assert.Equal("view", e.Session); // 视图会话常量口径（镜像 IssuesVM check-run）
        Assert.Equal("tester", e.Operator);
        Assert.Equal("Draw", e.Role);
        Assert.Equal(f.Vm.Session.SourcePath, e.Target);
        // 填充不改图纸：前后哈希一致且等于当前图纸哈希。
        Assert.Equal(e.BeforeHash, e.AfterHash);
        Assert.Equal(f.Vm.Session.ComputeHash(), e.AfterHash);
        Assert.Equal("", e.SnapshotRef);
        Assert.Equal("", e.PlanRef);
        using var doc = JsonDocument.Parse(e.ParamsJson);
        JsonElement p = doc.RootElement;
        Assert.Equal(1, p.GetProperty("count").GetInt32());
        JsonElement adopted = p.GetProperty("adopted");
        Assert.Equal(1, adopted.GetArrayLength());
        Assert.Equal("R-50001-13.0.1", adopted[0].GetProperty("ruleId").GetString());
        Assert.Equal(recommended.Clause, adopted[0].GetProperty("clause").GetString());
        Assert.Equal(recommended.Standard, adopted[0].GetProperty("standard").GetString());
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [AvaloniaFact]
    public async Task FillIntent_MandatoryOnlyChecked_NoRuleAdopted()
    {
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        IssueEntry mandatory = window.Issues!.Issues.Single(i => i.Force == RuleForce.Mandatory);
        mandatory.IsChecked = true;
        ClickFillIntent(window);
        // Mandatory 勾选走 force 标签格式，但义务非采纳——无 rule-adopted。
        Assert.Equal($"{mandatory.RuleId} [Mandatory]: {mandatory.Suggestion}", window.PlanIntentInput!.Text);
        Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adopted");
    }

    [AvaloniaFact]
    public async Task FillIntent_Twice_AppendsTwoRuleAdopted()
    {
        // append-only 决策日志：每次填充动作各留一条，不去重。
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        f.Window.Issues!.Issues.Single(i => i.RuleId == "R-50001-13.0.1").IsChecked = true;
        ClickFillIntent(f.Window);
        ClickFillIntent(f.Window);
        Assert.Equal(2, f.Audit.ReadAll().Count(r => r.Entry.Kind == "rule-adopted"));
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [AvaloniaFact]
    public async Task FillIntent_AuditWriteFails_AbortsFillWithInlineError()
    {
        // 审计写失败路径（严格中止口径）：刷新后清连接池并把 audit.db 换成同名目录
        // → Append 开库失败抛 SqliteException → 行内错误 + 意图框不变 + 不崩 UI。
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        window.PlanIntentInput!.Text = "既有意图哨兵"; // 钉住"不填充"：失败后哨兵须逐字保留
        string auditDbPath = Path.Combine(f.ProjectDir, ".cadagent", "audit.db");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // 连接池持文件句柄，Windows 下删库前须清池
        File.Delete(auditDbPath);
        Directory.CreateDirectory(auditDbPath); // 目录占位 → SQLite CANTOPEN（非 BUSY/LOCKED 重试路径）
        window.Issues!.Issues.Single(i => i.RuleId == "R-50001-13.0.1").IsChecked = true;
        ClickFillIntent(window); // 异常吞入行内通道，不崩 UI
        Assert.True(window.PlanError!.IsVisible);
        Assert.Contains("采纳审计写入失败", window.PlanError.Text);
        Assert.Equal("既有意图哨兵", window.PlanIntentInput.Text); // 严格中止：审计先行无旁路
    }

    [AvaloniaFact]
    public async Task FillIntent_Checked_GeneratePlan_PreviewReadyWithCheckedIntent()
    {
        // 勾选→填充→生成预览全链（FakeLlmClient 注入，不触网）：Intent 含勾选项 force 标签。
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        foreach (IssueEntry i in window.Issues!.Issues) i.IsChecked = true;
        ClickFillIntent(window);
        window.LeftTabs!.SelectedIndex = 3; // 切到"计划"tab，面板进入视觉树
        Dispatcher.UIThread.RunJobs();

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady() => readyTcs.TrySetResult();
        void OnDone() => doneTcs.TrySetResult();
        f.PlanVm.PreviewReady += OnReady;
        f.PlanVm.RunCompleted += OnDone;
        try
        {
            window.GeneratePlanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
            Assert.True(f.PlanVm.IsPreviewVisible);
            Assert.True(window.PlanPreviewSection!.IsVisible);
            Assert.Equal(window.PlanIntentInput!.Text, f.PlanVm.Intent);
            Assert.Contains("R-50001-5.0.2 [Mandatory]", f.PlanVm.Intent);
            Assert.Contains("R-50001-13.0.1 [Recommended]", f.PlanVm.Intent);
            // 拒绝收尾（不执行）：编排正常结束，无 tx 落图。
            window.RejectPlanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("RejectedByHuman", f.PlanVm.Status);
        }
        finally
        {
            f.PlanVm.PreviewReady -= OnReady;
            f.PlanVm.RunCompleted -= OnDone;
        }
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [AvaloniaFact]
    public async Task FillIntent_SameRuleTargetsCollision_CheckedOne_AdoptedCountOne_CheckPreserved()
    {
        // I1（合并前 ISTQB fixwave2）：mode 省略标高规则 → 同 RuleId 同 Targets 异 Message
        // 两条 issue。旧 key 碰撞 → 勾其一刷新后两条全勾 → adopted 重复/count 失真。
        // 修复后：仅勾选项入意图与采纳审计（count=1 无重复），刷新后仅该条保持勾选。
        using var f = new IssueIntentFixture(elevationCollision: true);
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        List<IssueEntry> elev = window.Issues!.Issues.Where(i => i.RuleId == "R-TEST-ELEV").ToList();
        Assert.Equal(2, elev.Count);
        Assert.Equal(elev[0].Targets, elev[1].Targets); // 同目标句柄
        Assert.NotEqual(elev[0].Message, elev[1].Message); // 异消息（小数位 vs 符号）
        IssueEntry sign = elev.Single(i => i.Message.Contains("正数"));
        sign.IsChecked = true;
        // 刷新 → 仅该条保持勾选（key 含 Message 区分因子）；旧 key 碰撞此处两条全勾。
        await f.RefreshIssuesAsync();
        Assert.True(window.Issues.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("正数")).IsChecked);
        Assert.False(window.Issues.Issues.Single(i => i.RuleId == "R-TEST-ELEV" && i.Message.Contains("小数位")).IsChecked);
        ClickFillIntent(window);

        // 采纳审计：仅勾选的一条（count=1，adopted 数组无重复）——旧 key 碰撞时
        // 刷新后两条全勾 → count=2 失真，此处钉住修复后口径。
        AuditRecord rec = Assert.Single(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adopted");
        using (JsonDocument doc = JsonDocument.Parse(rec.Entry.ParamsJson))
        {
            JsonElement p = doc.RootElement;
            Assert.Equal(1, p.GetProperty("count").GetInt32());
            Assert.Equal(1, p.GetProperty("adopted").GetArrayLength());
            Assert.Equal("R-TEST-ELEV", p.GetProperty("adopted")[0].GetProperty("ruleId").GetString());
        }
        // 意图仅勾选项一行（未勾选的小数位条不混入）。
        Assert.Equal($"R-TEST-ELEV [Recommended]: {sign.Suggestion}", window.PlanIntentInput!.Text);
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [AvaloniaFact]
    public async Task FillIntent_DuringInFlightEdit_BusyGate_NoFillNoAudit()
    {
        // I2（合并前 ISTQB fixwave2）：注入会话闸持闸 → 编辑确定性在途（_editBusy 置位，
        // 镜像 IntegrationHeadlessTests 持闸构造法）。RaiseEvent 绕过 IsEnabled →
        // 结构闸必须拦截：意图框哨兵不变 + 无 rule-adopted（TryAppendRuleAdoptedAudit
        // 的 ComputeHash 无闸读点不得在途触达）。
        using var f = new IssueIntentFixture();
        await f.RefreshIssuesAsync();
        MainWindow window = f.Window;
        window.Issues!.Issues.Single(i => i.RuleId == "R-50001-13.0.1").IsChecked = true;
        window.PlanIntentInput!.Text = "既有意图哨兵"; // 钉住"不填充"
        ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        ClickCanvasAtWorld(window, 20, 20); // 锚点
        await f.Gate.WaitAsync(); // 持闸：提交后的编辑确定性停在会话闸上
        var tcs = new TaskCompletionSource();
        // EditCompleted 先于 RunEditAsync finally 清 busy——Background 优先级续体确保清 busy 先跑。
        void Handler() => Dispatcher.UIThread.Post(() => tcs.TrySetResult(), DispatcherPriority.Background);
        window.Edit!.EditCompleted += Handler;
        try
        {
            ClickCanvasAtWorld(window, 80, 30); // 提交 → busy，阻塞在会话闸
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.EditToolbar!.IsEnabled); // busy 已确立
            Assert.False(window.FillIntentButton!.IsEnabled); // ② busy 时按钮同步禁用
            ClickFillIntent(window); // RaiseEvent 绕过禁用态 → 结构闸拦截
            Assert.Equal("既有意图哨兵", window.PlanIntentInput.Text); // 不填充
            Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adopted"); // 不审计
            f.Gate.Release();
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Edit.EditCompleted -= Handler;
            if (f.Gate.CurrentCount == 0) f.Gate.Release();
        }
        Assert.True(window.FillIntentButton!.IsEnabled); // busy 清除后恢复
    }

    [AvaloniaFact]
    public async Task FillIntent_InformationalOnlyChecked_ForceTag_NoRuleAdopted()
    {
        // M2（fixwave2）：第三分支钉住——Informational-only 勾选入意图带 [Informational]
        // 标签，但不入采纳审计（仅 Recommended 采纳；Mandatory 义务已由他测钉住）。
        using var f = new IssueIntentFixture();
        // 裁定降级 R-50001-13.0.1 → Informational（Refresh 每次重读裁定文件）。
        string adjPath = Path.Combine(f.ProjectDir, ".cadagent", "adjudications.json");
        var set = AdjudicationSet.Load(adjPath);
        set.Upsert(new Adjudication("R-50001-13.0.1", "tester", DateTimeOffset.UtcNow,
            "测试降级", ForceOverride: RuleForce.Informational));
        set.Save(adjPath);
        await f.RefreshIssuesAsync();
        IssueEntry info = f.Window.Issues!.Issues.Single(i => i.RuleId == "R-50001-13.0.1");
        Assert.Equal(RuleForce.Informational, info.Force);
        info.IsChecked = true;
        ClickFillIntent(f.Window);
        Assert.Equal($"{info.RuleId} [Informational]: {info.Suggestion}", f.Window.PlanIntentInput!.Text);
        Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "rule-adopted");
    }
}
