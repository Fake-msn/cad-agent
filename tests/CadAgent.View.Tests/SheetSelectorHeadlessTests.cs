using ACadSharp.Entities;
using ACadSharp.Objects;
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
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// 幅面感知 T5 headless（[AvaloniaFact] 真实 Skia 帧）：MainWindow 幅面 ComboBox
/// （internal SheetSelector）——默认首幅面裁剪（R4 sanctioned 行为变更），切换幅面 →
/// Canvas.Items 刷新为该幅面图元（模型幅面=框内锚点裁剪；布局幅面=BuildViewport 纸面映射），
/// 状态栏显示幅面名。单幅面夹具无感（既有 headless 回归闸口）；多幅面夹具断言按幅面。
/// </summary>
public class SheetSelectorHeadlessTests
{
    private static string UniqueProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-sheet-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>双图框幅面：框 0 (0,0)-(1000,700)、框 1 (2000,0)-(3000,700)，各含幅面名文字 + E-WIRE 线。</summary>
    private static string TwoSheetDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        for (int i = 0; i < 2; i++)
        {
            var ins = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(i * 2000 + 500, 650, 0), Value = i == 0 ? "一层电力平面图" : "二层电力平面图", Height = 30, Layer = new Layer("图框") });
            doc.Entities.Add(new Line { StartPoint = new XYZ(i * 2000 + 100, 100, 0), EndPoint = new XYZ(i * 2000 + 200, 100, 0), Layer = new Layer("E-WIRE") });
        }
    });

    /// <summary>模型两线（近/远）+ 纸空间布局视口（viewTarget=(0,0) viewHeight=200 纸面 1000×700）。</summary>
    private static string LayoutDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(6000, 5000, 0) });
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200,
        });
    });

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private static (MainWindow Window, string Path, string ProjectDir) Show(
        string dxfPath, IIdentityProvider? identity = null, SemaphoreSlim? sessionGate = null)
    {
        string projectDir = UniqueProjectDir();
        var vm = new MainViewModel(dxfPath, projectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, identity, sessionGate);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, dxfPath, projectDir);
    }

    private static void Cleanup(MainWindow window, string path, string projectDir)
    {
        window.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // 编辑测试可能惰性建库，释放文件句柄
        File.Delete(path);
        if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
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

    [AvaloniaFact]
    public void SheetSelector_SwitchesCanvasItems()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoSheetDxf());
        try
        {
            Assert.NotNull(window.SheetSelector);
            Assert.Equal(2, window.SheetSelector!.ItemCount);
            Assert.Equal(0, window.SheetSelector.SelectedIndex); // 默认首幅面
            // 首幅面：仅框 0 内实体（含一层名文字，不含二层）
            Assert.Contains(window.Canvas!.Items, i => i.Text == "一层电力平面图");
            Assert.DoesNotContain(window.Canvas.Items, i => i.Text == "二层电力平面图");
            Assert.Contains("一层电力平面图", window.Status!.Text);

            // 切换第二幅面 → 仅框 1 内实体
            window.SheetSelector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.Canvas.Items, i => i.Text == "二层电力平面图");
            Assert.DoesNotContain(window.Canvas.Items, i => i.Text == "一层电力平面图");
            Assert.Contains("二层电力平面图", window.Status.Text);
            // 框 1 内实体锚点 X 均 ≥2000（按幅面断言，R4 加强）
            Assert.All(window.Canvas.Items, i => Assert.True(i.Points[0].X >= 2000));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void SheetSelector_LayoutSheet_BuildsViewportItems()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf());
        try
        {
            // sheets = S0（模型）+ 布局1（布局视口）
            Assert.Equal(2, window.SheetSelector!.ItemCount);
            // 默认模型幅面 S0：两线锚点均在 ModelBounds 内 → 全保留
            Assert.Equal(2, window.Canvas!.Items.Count);

            window.SheetSelector.SelectedIndex = 1; // 布局幅面
            Dispatcher.UIThread.RunJobs();
            // BuildViewport：仅近线入视口区域，映射到纸面 (500,350)
            var item = Assert.Single(window.Canvas.Items);
            Assert.Equal(RenderKind.Line, item.Kind);
            Assert.Equal(500, item.Points[0].X, 6);
            Assert.Equal(350, item.Points[0].Y, 6);
            Assert.Contains("布局1", window.Status!.Text);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void SheetSelector_SingleSheet_LegacyUnaffected()
    {
        // 单幅面（无图框）夹具：默认 S0 覆盖全部实体，legacy 行为无感（R4 单幅面无感）
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(10, 20, 0) });
        });
        (MainWindow window, _, string projectDir) = Show(path);
        try
        {
            Assert.Equal(1, window.SheetSelector!.ItemCount);
            Assert.Equal(2, window.Canvas!.Items.Count); // 全保留
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ===== Fix Round 1（审查修复①）：布局幅面只读硬闸 =====

    /// <summary>
    /// 布局幅面激活时编辑工具硬禁（结构性标志，非仅 IsEnabled）：工具栏按压被拒
    /// （RaiseEvent 可绕过禁用态）+ EditError 提示；直接置 ActiveTool（模拟跨幅面
    /// 残留工具态）后画布点击也不产 view-edit 审计/tx——纸面坐标绝不提交进模型空间。
    /// 切回模型幅面后编辑恢复。不做纸→模型逆映射（纸面编辑非本期语义，deferred）。
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_EditToolsHardDisabled_NoCrossSpaceWrite()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1; // 布局幅面
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.Canvas!.Items); // 视口纸面单线

            // 工具栏按压（RaiseEvent 绕过 IsEnabled）：Line/Delete/Undo 一律拒绝 + 提示。
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Select, window.Edit!.ActiveTool);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("布局幅面为只读视图", window.EditError.Text);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(EditTool.Select, window.Edit.ActiveTool);
            window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(window.Edit.RecentTxIds);

            // 结构性钉住：直接置 ActiveTool（模拟模型幅面激活 Line 后切布局的残留态），
            // 画布两击纸面坐标不得提交 Line（OnCanvasToolClick 布局守卫）。
            window.Edit.ActiveTool = EditTool.Line;
            ClickCanvasAtWorld(window, 500, 350);
            ClickCanvasAtWorld(window, 600, 400);
            Assert.Null(window.Canvas.PendingPoint); // 不落预览锚点
            Assert.Empty(window.Edit.RecentTxIds);   // 无 tx
            Assert.Single(window.Canvas.Items);      // 无新实体

            // ChangeLayer 残留态 + 层下拉选择：同样拒绝（OnEditLayerSelected 守卫）。
            window.Edit.ActiveTool = EditTool.ChangeLayer;
            window.Canvas.Select(0);
            window.EditLayerSelector!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Edit.RecentTxIds);

            // 全程无 view-edit 审计（审计库惰性创建——根本不应存在）。
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));

            // 切回模型幅面 → 编辑恢复（只读闸随幅面解除）。
            window.SheetSelector.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
            Assert.False(window.EditError!.IsVisible);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ===== Fix Round 1（审查修复②）：OnSheetSelected _editBusy 硬闸 =====

    /// <summary>
    /// 在途编辑期间程序性 SelectionChanged → 幅面不切换（索引恢复到当前幅面）、
    /// 不枚举 Doc（无 torn read——ApplySheet/BuildViewport 无会话闸，T6/T10 同款
    /// 持闸钉住）；编辑完成后幅面切换恢复正常。
    /// </summary>
    [AvaloniaFact]
    public async Task SheetSelection_DuringInFlightEdit_Ignored_IndexRestored()
    {
        var gate = new SemaphoreSlim(1, 1);
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)), gate);
        try
        {
            Assert.Equal(0, window.SheetSelector!.SelectedIndex); // 默认模型幅面 S0
            Assert.Equal(2, window.Canvas!.Items.Count);
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 20, 20); // 预览锚点
            Assert.NotNull(window.Canvas.PendingPoint);

            await gate.WaitAsync(); // 持闸：编辑确定性停在闸上（在途）
            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            window.Edit!.EditCompleted += Handler;
            try
            {
                ClickCanvasAtWorld(window, 80, 30); // 提交 → busy，阻塞在会话闸
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.EditToolbar!.IsEnabled);

                // 在途编辑期间程序性 SelectionChanged：必须忽略 + 索引恢复。
                window.SheetSelector.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, window.SheetSelector.SelectedIndex); // 索引恢复到当前幅面
                Assert.Equal(2, window.Canvas.Items.Count);          // 未切换（无 BuildViewport torn read）
                Assert.DoesNotContain("布局1", window.Status!.Text);

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
            Assert.Single(window.Edit.RecentTxIds);      // 恰好一次编辑
            Assert.Equal(0, window.SheetSelector.SelectedIndex); // 编辑重建后仍在模型幅面
            Assert.Equal(3, window.Canvas.Items.Count);  // 新线入 S0（无框幅面全保留）

            // 编辑完成后幅面切换恢复正常。
            window.SheetSelector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("布局1", window.Status.Text);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ===== Final fix（R11 Imp#3）：undo/rollback 后 _doc 同步 =====

    /// <summary>触发编辑的 UI 动作并等待 EditCompleted（T8 模式）。
    /// EditCompleted 在 RunEditAsync 的 finally 清 _editBusy 之前触发——tcs 经 Background
    /// 优先级 dispatcher job 完成，确保清 busy（Normal 优先级续体）先跑，后续幅面切换不被 busy 闸误拒。</summary>
    private static async Task ActAndWaitForEditAsync(MainWindow window, Action action)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => Dispatcher.UIThread.Post(() => tcs.TrySetResult(), DispatcherPriority.Background);
        window.Edit!.EditCompleted += Handler;
        try
        {
            action();
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Edit.EditCompleted -= Handler; }
    }

    /// <summary>
    /// R11：编辑（加线）→ undo（StepEngine.Rollback→RestoreState 整体替换 Doc）→ 切布局幅面
    /// → BuildViewport 必须用恢复后的 Doc（仅原始近线入视口），而非 undo 前 detached doc
    /// （含已撤销新线 → 2 项）。RefreshAfterEdit 顶部同步 _doc 钉住。
    /// </summary>
    [AvaloniaFact]
    public async Task UndoThenLayoutSheet_RendersRestoredState_NotStaleDoc()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            Assert.Equal(0, window.SheetSelector!.SelectedIndex); // 默认模型幅面 S0
            Assert.Equal(2, window.Canvas!.Items.Count);

            // 编辑：加一条落在视口模型区域内的线（锚点 (20,20) ∈ viewTarget±区域）
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 20, 20); // 预览锚点
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 80, 30));
            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Equal(3, window.Canvas.Items.Count);
            Assert.Single(window.Edit.RecentTxIds);

            // undo → RestoreState 替换 Doc（模型幅面恢复 2 线）
            await ActAndWaitForEditAsync(window, () =>
                window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Null(window.Edit.ErrorMessage);
            Assert.Empty(window.Edit.RecentTxIds);
            Assert.Equal(2, window.Canvas.Items.Count);
            Assert.True(window.EditToolbar!.IsEnabled); // busy 已清（幅面切换不被 busy 闸误拒）

            // 切布局幅面 → 渲染须反映恢复态：仅原始近线入视口（新线已撤销）
            window.SheetSelector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("布局1", window.Status!.Text);
            var item = Assert.Single(window.Canvas.Items); // 陈旧 _doc 会多渲染已撤销线 → 2 项（RED）
            Assert.Equal(RenderKind.Line, item.Kind);
            Assert.Equal(500, item.Points[0].X, 6);
            Assert.Equal(350, item.Points[0].Y, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }
}
