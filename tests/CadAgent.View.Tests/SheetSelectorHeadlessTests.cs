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
/// Canvas.Items 刷新为该幅面图元（模型幅面=框内锚点裁剪；布局幅面=ComposeLayout
/// 纸面→视口内模型→视口框），状态栏显示幅面名。单幅面夹具无感（既有 headless 回归闸口）；
/// 多幅面夹具断言按幅面。项2 A（Task 7）：布局幅面视口内可编辑模型（纸→模型逆映射）；
/// 项2 B（Plan-2 T5）：纸面编辑启用——纸面允许路径/纸面新建/改文字覆盖移至
/// PaperEditHeadlessTests，本文件保留模型幅面语义、视口框只读与跨空间拒绝回归。
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

    /// <summary>
    /// 模型两线（近/远）+ 纸空间布局视口（viewTarget=(0,0) viewHeight=200 纸面 1000×700）。
    /// </summary>
    /// <param name="nearLineEndX">近线模型终点 X（默认 1000 → 纸面映射到 (4000,350)）。Move 用例取小值，
    /// 使纸面目标点为空点（全长映射线会在目标点二次命中 → 只重置基准不移动）。</param>
    /// <param name="twist">视口 TwistAngle（弧度）：非 0 时布局仍登记（尺寸合法）但纸→模型不可映射。</param>
    /// <param name="blockChild">模型空间追加块 LUM_CHILD（一条对角线子图元）+ Insert@(20,20)，
    /// 锚点落在视口模型区域内——块子图元 Plan-3 拒绝用例夹具。</param>
    /// <param name="extraLayer">追加空图层（布局幅面 ChangeLayer 允许路径目标）。</param>
    private static string LayoutDxf(double nearLineEndX = 1000, double twist = 0,
        bool blockChild = false, string? extraLayer = null)
        => DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(nearLineEndX, 0, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(6000, 5000, 0) });
        if (extraLayer is not null) doc.Layers.Add(new Layer(extraLayer));
        if (blockChild)
        {
            var block = new BlockRecord("LUM_CHILD");
            block.Entities.Add(new Line { StartPoint = new XYZ(19, 19, 0), EndPoint = new XYZ(21, 21, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(20, 20, 0) });
        }
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200,
            TwistAngle = twist,
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
            // ComposeLayout：纸面 0 + 视口内模型 1（远线锚点在视口外）+ 视口框 4 = 5 项
            Assert.Equal(5, window.Canvas.Items.Count);
            Assert.Equal(4, window.Canvas.Items.Count(i => i.Locked));
            // 近线入视口区域，映射到纸面 (500,350)→(4000,350)
            var item = Assert.Single(window.Canvas.Items, i => !i.Locked);
            Assert.Equal(RenderKind.Line, item.Kind);
            Assert.Equal(500, item.Points[0].X, 6);
            Assert.Equal(350, item.Points[0].Y, 6);
            Assert.Contains("布局1", window.Status!.Text);
            Assert.Contains("纸面/模型双空间", window.Status.Text);
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

    // ===== Task 7（项2 A）：布局幅面 = 纸面视图内可编辑模型 =====

    /// <summary>
    /// 纸→模型逆映射（s=700/200=3.5）：纸面 (500,350)/(850,350) 两击 → 模型 (0,0)/(100,0)，
    /// 落盘回验 + 纸面显示位置回验。夹具另有一条纸面映射为 (500,350)-(4000,350) 的近线，
    /// 故按端点过滤新线（doc.Entities 末位追加）。
    /// </summary>
    [AvaloniaFact]
    public async Task LayoutSheet_LineToolInsideViewport_WritesModelLine_AtInverseCoords()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // 纸面层（无纸面实体）+ 模型 1 线 + 视口框 4 线
            Assert.Equal(5, window.Canvas!.Items.Count);
            Assert.Equal(4, window.Canvas.Items.Count(i => i.Locked));

            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 纸面中心 → 模型 (0,0)
            Assert.NotNull(window.Canvas.PendingPoint);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 850, 350)); // → 模型 (100,0)
            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);

            // 模型实体落在逆映射坐标（落盘回验）与纸面显示位置回验
            var saved = CadAgent.Data.DrawingSession.Load(path);
            Assert.Contains(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.EndPoint.X - 100) < 1e-6);
            var line = Assert.Single(window.Canvas.Items, i => i.Kind == RenderKind.Line && !i.Locked
                && Math.Abs(i.Points[0].X - 500) < 1e-6 && Math.Abs(i.Points[1].X - 850) < 1e-6);
            Assert.Equal(500, line.Points[0].X, 6);
            Assert.Equal(850, line.Points[1].X, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// Important I2：跨空间两击——首击视口内（=模型锚点，pending 已设）、次击视口外纸面：
    /// 不得混空间成线；pending 清除 + 行内跨空间拒绝 + 零 tx + 图元数不变。
    /// （项2 Plan-2 T5：纸面新建已启用，拒绝原因从「纸面未启用」改为「跨空间」。）
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_LineToolCrossSpaceTwoClicks_ClearsPending_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, window.Canvas!.Items.Count);
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 视口内 → 预览锚点
            Assert.NotNull(window.Canvas.PendingPoint);
            ClickCanvasAtWorld(window, 1200, 350); // 视口外纸面 → 次击跨空间拒绝
            Assert.Null(window.Canvas.PendingPoint);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("跨空间", window.EditError.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.Equal(5, window.Canvas.Items.Count); // 无新实体
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// Important 1：Move 增量 ÷ 视口比例（s=700/200=3.5）。纸面 (500,350) 命中视口内模型线
    /// （选中、基准=映射线首点），纸面空点 (850,350) → dx=350/3.5=+100 模型单位（模型线短，
    /// 目标点不在全长映射线上）；落盘回验 + 纸面显示位置 (850,350)-(1025,350) 回验。
    /// </summary>
    [AvaloniaFact]
    public async Task LayoutSheet_MoveInsideViewport_DividesPaperDeltaByScale()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(nearLineEndX: 50),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Move, window.Edit!.ActiveTool);

            ClickCanvasAtWorld(window, 500, 350); // 命中映射线 → 选中 + 基准 (500,350)
            Assert.NotNull(window.Canvas!.SelectedHandle);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 850, 350)); // 空点 → 纸面增量 350

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);

            // 落盘：模型起点 X 恰好 +100（去掉 ÷s 会得到 +350）
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var moved = Assert.Single(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.Y) < 1e-6 && Math.Abs(l.StartPoint.X - 100) < 1e-6);
            Assert.Equal(100, moved.StartPoint.X, 6);
            Assert.Equal(150, moved.EndPoint.X, 6);

            // 纸面显示随模型 +100×3.5 → (850,350)-(1025,350)
            var line = Assert.Single(window.Canvas.Items,
                i => i.Kind == RenderKind.Line && !i.Locked && Math.Abs(i.Points[0].X - 850) < 1e-6);
            Assert.Equal(850, line.Points[0].X, 6);
            Assert.Equal(1025, line.Points[1].X, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// Important 1：twist 夹具（TwistAngle=0.3，尺寸合法 → 布局仍登记）——视口内点工具拒绝，
    /// 文案为旋转原因（不得落入纸面新建）；无预览锚点、零 tx、无新实体。
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_TwistedViewport_LineClickRefused_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(twist: 0.3),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            Assert.Equal(2, window.SheetSelector!.ItemCount); // 旋转不影响幅面登记
            window.SheetSelector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 视口内 → 纸→模型映射不可用（旋转）

            Assert.Null(window.Canvas!.PendingPoint);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("旋转", window.EditError.Text);
            Assert.DoesNotContain("纸面编辑", window.EditError.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.Equal(5, window.Canvas.Items.Count); // 纸面 0 + 模型 1 + 框 4，无新实体
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// 布局幅面守卫（项2 B 口径）：工具激活不允许被整体拒绝；视口框只读（拒绝先于确认），
    /// 切回模型幅面后编辑恢复可用。
    /// 拒绝路径无 EditCompleted，故 Delete 按压不等待编辑事件（守卫先于确认对话框）。
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_LockedFrameAndPaperGuards()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // 工具激活不再被整体拒绝
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Line, window.Edit!.ActiveTool);
            // 视口框不可删：选中框线 → Delete（确认 seam 注入 true）→ 守卫先于确认拒绝
            window.Edit.ActiveTool = EditTool.Delete;
            window.Canvas!.Select(window.Canvas.Items.ToList().FindIndex(i => i.Locked));
            bool confirmInvoked = false;
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            window.Edit.ActiveTool = EditTool.Delete;
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(confirmInvoked); // 守卫先于确认（不对只读框弹确认）
            Assert.Empty(window.Edit.RecentTxIds);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("视口边框只读", window.EditError.Text);

            // Minor 5：切回模型幅面 → 编辑恢复可用（工具激活 + 错误行隐藏）
            window.SheetSelector.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Line, window.Edit.ActiveTool);
            Assert.False(window.EditError.IsVisible);
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
            // ComposeLayout：纸面 0 + 模型 1 + 视口框 4；陈旧 _doc 会多渲染已撤销线（模型 2 → 6 项，RED）
            Assert.Equal(5, window.Canvas.Items.Count);
            var item = Assert.Single(window.Canvas.Items, i => !i.Locked);
            Assert.Equal(RenderKind.Line, item.Kind);
            Assert.Equal(500, item.Points[0].X, 6);
            Assert.Equal(350, item.Points[0].Y, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ===== 合并前 ISTQB 加固 B：Y 轴逆映射 / 允许路径 / 幅面切换 =====

    /// <summary>
    /// Y 轴逆映射（旧夹具两击同 y=350，Y 通道未被覆盖）：纸面 (500,350)→(850,420)
    /// 两击 → 模型 (0,0)-(100,20)；落盘 + 纸面终点 y=420 回验（去掉 y 逆映射会得 (100,0)/(850,350)）。
    /// </summary>
    [AvaloniaFact]
    public async Task LayoutSheet_LineToolInverseMapping_PreservesYAxis()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 纸面中心 → 模型 (0,0)
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 850, 420)); // → 模型 (100,20)

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var added = Assert.Single(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.StartPoint.Y) < 1e-6
                     && Math.Abs(l.EndPoint.X - 100) < 1e-6);
            Assert.Equal(20, added.EndPoint.Y, 6);

            var line = Assert.Single(window.Canvas.Items, i => i.Kind == RenderKind.Line && !i.Locked
                && Math.Abs(i.Points[1].X - 850) < 1e-6);
            Assert.Equal(420, line.Points[1].Y, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// Y 轴逆映射（Move）：纸面 (500,350) 命中映射线、目标 (850,420) 空点 →
    /// 增量 (350,70)/3.5 = (+100,+20) 模型单位；落盘 + 纸面显示回验（旧用例 dy=0 未覆盖）。
    /// </summary>
    [AvaloniaFact]
    public async Task LayoutSheet_MoveInverseMapping_PreservesYAxis()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(nearLineEndX: 50),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 命中映射线 (500,350)-(675,350)
            Assert.NotNull(window.Canvas!.SelectedHandle);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 850, 420));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var moved = Assert.Single(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X - 100) < 1e-6 && Math.Abs(l.StartPoint.Y - 20) < 1e-6);
            Assert.Equal(100, moved.StartPoint.X, 6);
            Assert.Equal(20, moved.StartPoint.Y, 6);
            Assert.Equal(20, moved.EndPoint.Y, 6);

            var line = Assert.Single(window.Canvas.Items,
                i => i.Kind == RenderKind.Line && !i.Locked && Math.Abs(i.Points[0].X - 850) < 1e-6);
            Assert.Equal(420, line.Points[0].Y, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// 项2 B（Plan-2 T5）取代 Plan-1 的「块子图元按父实体整体」允许路径：布局幅面下块子图元
    /// （模型空间 Insert 子线，句柄 "{insert}:0"）Delete → Plan-3 拒绝文案、零 tx、确认不触发、
    /// audit.db 不落盘（父 Insert 与两条线原样保留）。模型幅面的整父语义不变（见 MainWindow 早期口径）。
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_BlockChildDelete_RefusedPlan3_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(blockChild: true),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // 纸面 0 + 视口内模型（近线 + 块子图元）2 + 视口框 4
            Assert.Equal(6, window.Canvas!.Items.Count);
            int index = window.Canvas.Items.ToList().FindIndex(i =>
                i.Handle.Contains(':') && i.Space == RenderSpace.ModelSpace && !i.Locked);
            Assert.True(index >= 0, "夹具块子图元未入视口");

            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            bool confirmInvoked = false;
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            window.Canvas.Select(index);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(confirmInvoked); // 守卫先于确认（Plan-3 目标不弹确认）
            Assert.True(window.EditError?.IsVisible ?? false);
            Assert.Contains("Plan-3", window.EditError!.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            Assert.Contains(saved.Doc.Entities, e => e is Insert); // 父 Insert 原样保留
            Assert.Equal(2, saved.Doc.Entities.OfType<Line>().Count());
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>布局幅面允许路径（a）：Delete 视口内模型线 → 落盘消失 + 恰好一 tx + step 审计。</summary>
    [AvaloniaFact]
    public async Task LayoutSheet_ModelLineDelete_Allowed_WritesDiskAndTx()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(nearLineEndX: 50),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            int index = window.Canvas!.Items.ToList().FindIndex(i => !i.Locked);
            Assert.True(index >= 0);

            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.ConfirmDeleteOverride = _ => Task.FromResult(true);
            window.Canvas.Select(index);
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            Assert.Single(saved.Doc.Entities); // 仅剩远线（视口内近线已删）
            Assert.DoesNotContain(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.EndPoint.X - 50) < 1e-6);
            using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "step"
                && r.Entry.ParamsJson.Contains("delete-entity"));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>布局幅面允许路径（b）：ChangeLayer 视口内模型线 → 落盘层名改变 + tx + step 审计。</summary>
    [AvaloniaFact]
    public async Task LayoutSheet_ModelLineChangeLayer_Allowed_WritesDiskAndTx()
    {
        (MainWindow window, string path, string projectDir) = Show(
            LayoutDxf(nearLineEndX: 50, extraLayer: "W-墙体"),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "ChangeLayer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.ChangeLayer, window.Edit!.ActiveTool);
            int index = window.Canvas!.Items.ToList().FindIndex(i => !i.Locked);
            Assert.True(index >= 0);
            window.Canvas.Select(index);
            Assert.Contains("W-墙体", window.EditLayerSelector!.ItemsSource!.Cast<string>());

            await ActAndWaitForEditAsync(window, () =>
                window.EditLayerSelector!.SelectedItem = "W-墙体");

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var changed = Assert.Single(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.EndPoint.X - 50) < 1e-6);
            Assert.Equal("W-墙体", changed.Layer.Name);
            using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "step"
                && r.Entry.ParamsJson.Contains("change-layer"));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>布局幅面允许路径（c）：布局内编辑后 Undo → 实体恢复、RecentTxIds 清空、视图回原。</summary>
    [AvaloniaFact]
    public async Task LayoutSheet_UndoAfterLayoutEdit_RestoresEntity()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(nearLineEndX: 50),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            int index = window.Canvas!.Items.ToList().FindIndex(i => !i.Locked);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.ConfirmDeleteOverride = _ => Task.FromResult(true);
            window.Canvas.Select(index);
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Single(window.Edit!.RecentTxIds);

            await ActAndWaitForEditAsync(window, () =>
                window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Empty(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            Assert.Contains(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.EndPoint.X - 50) < 1e-6);
            // 布局视图回原：模型 1 + 视口框 4（纸面 0）
            Assert.Equal(5, window.Canvas.Items.Count);
            Assert.Single(window.Canvas.Items, i => !i.Locked);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// 幅面切换清预览锚点：模型幅面首击落锚点（模型坐标 1:1）→ 切布局幅面 →
    /// PendingPoint==null；再单击只重新布锚点（零 tx）——跨幅面坐标不得续用。
    /// </summary>
    [AvaloniaFact]
    public void SheetSwitch_ClearsPendingAnchor_NextClickOnlyRearms()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            Assert.Equal(0, window.SheetSelector!.SelectedIndex);
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 模型幅面空白点 → 锚点
            Assert.NotNull(window.Canvas!.PendingPoint);

            window.SheetSelector.SelectedIndex = 1; // 切布局幅面
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.Canvas.PendingPoint);

            ClickCanvasAtWorld(window, 500, 350); // 布局视口内单击 → 只布新锚点
            Assert.NotNull(window.Canvas.PendingPoint);
            Assert.Empty(window.Edit!.RecentTxIds);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// Text 工具逆映射：布局幅面纸面 (850,420) 单击 → 模型 (100,20) 落盘 TextEntity；
    /// 纸面显示位置回验（Text 与 Line/Move 共用 TryPointTarget 通道）。
    /// </summary>
    [AvaloniaFact]
    public async Task LayoutSheet_TextTool_WritesModelTextAtInversePoint()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.Text, window.Edit!.ActiveTool);
            window.EditTextInput!.Text = "轴号";

            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 850, 420));

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var text = Assert.Single(saved.Doc.Entities.OfType<TextEntity>(), e => e.Value == "轴号");
            Assert.Equal(100, text.InsertPoint.X, 6);
            Assert.Equal(20, text.InsertPoint.Y, 6);
            var item = Assert.Single(window.Canvas.Items,
                i => i.Kind == RenderKind.Text && i.Text == "轴号");
            Assert.Equal(850, item.Points[0].X, 6);
            Assert.Equal(420, item.Points[0].Y, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }
}
