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
/// 多幅面夹具断言按幅面。项2 A（Task 7）：布局幅面视口内可编辑模型（纸→模型逆映射），
/// 纸面编辑未启用（拒绝路径钉住）。
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
    /// <param name="paperLine">纸面项夹具（Important 2）：布局关联块内的纸面线 (100,100)-(200,100)。</param>
    /// <param name="twist">视口 TwistAngle（弧度）：非 0 时布局仍登记（尺寸合法）但纸→模型不可映射。</param>
    private static string LayoutDxf(double nearLineEndX = 1000, bool paperLine = false, double twist = 0)
        => DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(nearLineEndX, 0, 0) });
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
            TwistAngle = twist,
        });
        if (paperLine)
            layout.AssociatedBlock!.Entities.Add(
                new Line { StartPoint = new XYZ(100, 100, 0), EndPoint = new XYZ(200, 100, 0) });
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
            Assert.Contains("视口内可编辑模型", window.Status.Text);
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

    /// <summary>Plan-1 纸面编辑未启用：视口外纸面点击拒绝（无预览锚点、无 tx、行内提示）。</summary>
    [AvaloniaFact]
    public void LayoutSheet_PaperClick_Refused_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 1200, 350); // 视口外纸面
            Assert.Null(window.Canvas.PendingPoint);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("纸面编辑", window.EditError.Text);
            Assert.Empty(window.Edit.RecentTxIds);
            // 拒绝不触审计管线（不惰性建库）
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
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
    /// 文案不得声称纸面编辑可用（R14，Plan-1 纸面未启用）；无预览锚点、零 tx、无新实体。
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
    /// Important 2：纸面项（Space==PaperSpace）Delete——守卫先于确认（confirm seam 不触发）、
    /// 行内「纸面编辑」拒绝、零 tx、audit.db 不落盘。
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_PaperItemDelete_RefusedBeforeConfirm_NoAudit()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(paperLine: true),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(6, window.Canvas!.Items.Count); // 纸面 1 + 模型 1 + 框 4
            int paperIndex = window.Canvas.Items.ToList()
                .FindIndex(i => i.Space == RenderSpace.PaperSpace && !i.Locked);
            Assert.True(paperIndex >= 0);

            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.Canvas.Select(paperIndex);
            bool confirmInvoked = false;
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // 二次按压=确认
            Dispatcher.UIThread.RunJobs();

            Assert.False(confirmInvoked); // 守卫先于确认（不对纸面目标弹确认）
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("纸面编辑", window.EditError.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>Important 2：纸面项 ChangeLayer——同样守卫拒绝（零 tx、audit.db 不落盘）。</summary>
    [AvaloniaFact]
    public void LayoutSheet_PaperItemChangeLayer_Refused_NoAudit()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf(paperLine: true),
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            int paperIndex = window.Canvas!.Items.ToList()
                .FindIndex(i => i.Space == RenderSpace.PaperSpace && !i.Locked);
            Assert.True(paperIndex >= 0);

            ToolButton(window, "ChangeLayer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.ChangeLayer, window.Edit!.ActiveTool);
            window.Canvas.Select(paperIndex);
            window.EditLayerSelector!.SelectedItem = window.EditLayerSelector.ItemsSource!.Cast<string>().First();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("纸面编辑", window.EditError.Text);
            Assert.Empty(window.Edit.RecentTxIds);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    /// <summary>
    /// 旧「布局整体只读」语义作废（Task 7 取代）：视口内可编辑，视口框只读。
    /// 拒绝路径无 EditCompleted，故 Delete 按压不等待编辑事件（守卫先于确认对话框）。
    /// </summary>
    [AvaloniaFact]
    public void LayoutSheet_ViewportEditsAllowed_FrameAndPaperProtected()
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
}
