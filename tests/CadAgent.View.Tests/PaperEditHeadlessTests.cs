using System.Text.Json;
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
/// 项2 Plan-2 Task 5 headless：布局幅面纸面空间编辑（移动/删除/改层/纸面新建线·字/改文字）、
/// 块子图元 Plan-3 拒绝、视口框只读回归、跨空间两击拒绝、纸面 Undo 还原。
/// 夹具：模型近线（视口内）+ 远线；布局1 视口（Center(500,350) 600×400 / viewHeight 200
/// → s=2，PaperRect (200,150)-(800,550)）+ 纸面线 (100,100)-(200,100) + 纸面标题块
/// Insert（属性「图名」=旧图名 @ (300,620)，字高 10）。纸面编辑参数断言含 space/layout。
/// 全部点击点落在内容 bounds 内（Fit 保证可见）且按空间区分：纸面点 x&lt;200 或 y&gt;550。
/// </summary>
public class PaperEditHeadlessTests
{
    private const string LayoutName = "布局1";

    private static string UniqueProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-paper-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string PaperDxf(bool blockChild = false, string? extraLayer = null)
        => DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(50, 0, 0) });
            doc.Entities.Add(new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(6000, 5000, 0) });
            if (extraLayer is not null) doc.Layers.Add(new Layer(extraLayer));
            if (blockChild)
            {
                var child = new BlockRecord("LUM_CHILD");
                child.Entities.Add(new Line { StartPoint = new XYZ(19, 19, 0), EndPoint = new XYZ(21, 21, 0) });
                doc.BlockRecords.Add(child);
                doc.Entities.Add(new Insert(child) { InsertPoint = new XYZ(20, 20, 0) });
            }
            var titleBlock = new BlockRecord("TB_TITLE");
            doc.BlockRecords.Add(titleBlock);
            var layout = new Layout(LayoutName);
            doc.Layouts.Add(layout);
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0),
                Width = 600,
                Height = 400,
                ViewTarget = new XYZ(0, 0, 0),
                ViewHeight = 200,
            });
            layout.AssociatedBlock!.Entities.Add(
                new Line { StartPoint = new XYZ(100, 100, 0), EndPoint = new XYZ(200, 100, 0) });
            var title = new Insert(titleBlock) { InsertPoint = new XYZ(300, 620, 0) };
            var attr = new AttributeEntity { Tag = "图名", Value = "旧图名", Height = 10 };
            attr.InsertPoint = new XYZ(300, 620, 0);
            attr.AlignmentPoint = new XYZ(300, 620, 0);
            title.Attributes.Add(attr);
            layout.AssociatedBlock.Entities.Add(title);
        });

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private static FixedIdentity DrawIdentity() => new(new OperatorIdentity("tester", Role.Draw));

    private static (MainWindow Window, string Path, string ProjectDir) Show(string dxfPath)
    {
        string projectDir = UniqueProjectDir();
        var vm = new MainViewModel(dxfPath, projectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, DrawIdentity());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, dxfPath, projectDir);
    }

    private static void Cleanup(MainWindow window, string path, string projectDir)
    {
        window.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // 编辑测试惰性建库，释放文件句柄
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

    /// <summary>触发编辑的 UI 动作并等待 EditCompleted（同 SheetSelector 夹具：Background 优先级确保清 busy 先跑）。</summary>
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

    private static void SelectLayoutSheet(MainWindow window)
    {
        window.SheetSelector!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
    }

    private static int PaperLineIndex(MainWindow window) =>
        window.Canvas!.Items.ToList().FindIndex(i =>
            i.Space == RenderSpace.PaperSpace && !i.Locked && Math.Abs(i.Points[0].X - 100) < 1e-6);

    private static Line SavedPaperLine(CadAgent.Data.DrawingSession saved) =>
        saved.Doc.Layouts.First(l => l.Name == LayoutName)
            .AssociatedBlock!.Entities.OfType<Line>().Single();

    private static JsonElement Params(AuditRecord record) =>
        JsonDocument.Parse(record.Entry.ParamsJson).RootElement;

    /// <summary>Step 审计 params 含 space="paper"+layout（纸面写参数无旁路证据）。</summary>
    private static void AssertPaperScopeAudited(string projectDir, string kind)
    {
        using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
        AuditRecord step = Assert.Single(audit.ReadAll(), r =>
            r.Entry.Kind == "step" && Params(r).GetProperty("Kind").GetString() == kind);
        JsonElement p = Params(step).GetProperty("Params");
        Assert.Equal("paper", p.GetProperty("space").GetString());
        Assert.Equal(LayoutName, p.GetProperty("layout").GetString());
    }

    // ---- 1. 纸面线 Move：纸面增量原值（不 ÷s），模型不变，恰一 tx ----

    [AvaloniaFact]
    public async Task PaperLine_Move_WritesPaperDelta_ModelUntouched_OneTx()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 150, 100); // 命中纸面线 → 选中 + 基准 (100,100)
            Assert.NotNull(window.Canvas!.SelectedHandle);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 250, 250)); // 空点 → (+150,+150)

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);

            var saved = CadAgent.Data.DrawingSession.Load(path);
            Line moved = SavedPaperLine(saved);
            Assert.Equal(250, moved.StartPoint.X, 6);
            Assert.Equal(250, moved.StartPoint.Y, 6);
            Assert.Equal(350, moved.EndPoint.X, 6);
            // 模型近线未动（纸面增量不得经 ÷s 落到模型）
            Assert.Contains(saved.Doc.Entities.OfType<Line>(),
                l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.EndPoint.X - 50) < 1e-6);
            AssertPaperScopeAudited(projectDir, "move-entity");
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 2. 纸面线 Delete：确认后落盘消失，恰一 tx ----

    [AvaloniaFact]
    public async Task PaperLine_Delete_Confirmed_RemovesPaperOnly_OneTx()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            int index = PaperLineIndex(window);
            Assert.True(index >= 0);

            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.Canvas!.Select(index);
            window.ConfirmDeleteOverride = _ => Task.FromResult(true);
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            Assert.Empty(saved.Doc.Layouts.First(l => l.Name == LayoutName)
                .AssociatedBlock!.Entities.OfType<Line>()); // 纸面线消失
            Assert.Equal(2, saved.Doc.Entities.OfType<Line>().Count()); // 模型两线保留
            AssertPaperScopeAudited(projectDir, "delete-entity");
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 3. 纸面线 ChangeLayer：落盘层名变，模型不变 ----

    [AvaloniaFact]
    public async Task PaperLine_ChangeLayer_WritesPaperLayer_Tx()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf(extraLayer: "W-墙体"));
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "ChangeLayer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            int index = PaperLineIndex(window);
            Assert.True(index >= 0);
            window.Canvas!.Select(index);
            Assert.Contains("W-墙体", window.EditLayerSelector!.ItemsSource!.Cast<string>());

            await ActAndWaitForEditAsync(window, () =>
                window.EditLayerSelector.SelectedItem = "W-墙体");

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            Assert.Equal("W-墙体", SavedPaperLine(saved).Layer.Name);
            Assert.All(saved.Doc.Entities.OfType<Line>(), l => Assert.Equal("0", l.Layer.Name));
            AssertPaperScopeAudited(projectDir, "change-layer");
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 4. 视口外 Line 两击 → 纸面新线（space/layout 审计，模型实体数不变）----

    [AvaloniaFact]
    public async Task LineToolOutsideViewport_WritesPaperLine_ModelCountUnchanged()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 150, 300); // 视口外纸面（x<200）→ 预览锚点
            Assert.NotNull(window.Canvas!.PendingPoint);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 700, 600));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var added = Assert.Single(saved.Doc.Layouts.First(l => l.Name == LayoutName)
                .AssociatedBlock!.Entities.OfType<Line>(), l => Math.Abs(l.StartPoint.X - 150) < 1e-6);
            Assert.Equal(300, added.StartPoint.Y, 6);
            Assert.Equal(700, added.EndPoint.X, 6);
            Assert.Equal(600, added.EndPoint.Y, 6);
            Assert.Equal(2, saved.Doc.Entities.OfType<Line>().Count()); // 模型实体数不变
            // 纸面坐标原值渲染（不套逆映射）
            Assert.Contains(window.Canvas.Items, i => i.Kind == RenderKind.Line
                && Math.Abs(i.Points[0].X - 150) < 1e-6 && Math.Abs(i.Points[1].X - 700) < 1e-6);
            AssertPaperScopeAudited(projectDir, "add-line");
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 5. 视口外 Text → 纸面文字（纸面单位字高）----

    [AvaloniaFact]
    public async Task TextToolOutsideViewport_WritesPaperText()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditTextInput!.Text = "纸面字";
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 700, 600));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var text = Assert.Single(saved.Doc.Layouts.First(l => l.Name == LayoutName)
                .AssociatedBlock!.Entities.OfType<TextEntity>());
            Assert.Equal("纸面字", text.Value);
            Assert.Equal(700, text.InsertPoint.X, 6);
            Assert.Equal(600, text.InsertPoint.Y, 6);
            Assert.Empty(saved.Doc.Entities.OfType<TextEntity>()); // 模型空间未写
            var item = Assert.Single(window.Canvas!.Items, i => i.Kind == RenderKind.Text && i.Text == "纸面字");
            Assert.Equal(700, item.Points[0].X, 6);
            AssertPaperScopeAudited(projectDir, "add-text");
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 6. 改文字：纸面标题属性 → ATTRIB.Value 变 + 渲染文本更新 ----

    [AvaloniaFact]
    public async Task SetText_PaperAttribute_UpdatesValueAndRender()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "改文字").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.SetText, window.Edit!.ActiveTool);
            window.EditTextInput!.Text = "新图名";
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 305, 618)); // 属性文字盒内

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            var saved = CadAgent.Data.DrawingSession.Load(path);
            var attr = Assert.Single(saved.Doc.Layouts.First(l => l.Name == LayoutName)
                .AssociatedBlock!.Entities.OfType<Insert>().Single().Attributes);
            Assert.Equal("新图名", attr.Value);
            // RefreshAfterEdit 重建纸面：画布属性文本随之更新
            var item = Assert.Single(window.Canvas!.Items, i => i.AttributeTag == "图名");
            Assert.Equal("新图名", item.Text);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 7. 块内子图元：Move/Delete 拒绝（Plan-3），无 tx ----

    [AvaloniaFact]
    public void BlockChild_MoveAndDelete_RefusedPlan3_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf(blockChild: true));
        try
        {
            SelectLayoutSheet(window);
            int index = window.Canvas!.Items.ToList().FindIndex(i =>
                i.BlockName is not null && i.Space == RenderSpace.ModelSpace && !i.Locked);
            Assert.True(index >= 0, "夹具块子图元未入视口");

            // Move：首击命中块子图元 → 守卫拒绝（不选中、不落基准）
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 578, 428); // 子线纸面位置 = s·(insert(20,20)+子(19,19)) + center = (578,428)
            Assert.Null(window.Canvas.SelectedHandle);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("Plan-3", window.EditError.Text);
            Assert.Empty(window.Edit!.RecentTxIds);

            // Delete：选中块子图元 → 守卫先于确认拒绝
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.Canvas.Select(index);
            bool confirmInvoked = false;
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(confirmInvoked);
            Assert.Contains("Plan-3", window.EditError!.Text);
            Assert.Empty(window.Edit.RecentTxIds);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 8. 视口框 Delete：既有「视口边框只读」（回归）----

    [AvaloniaFact]
    public void ViewportFrame_Delete_StillRefused()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            int frameIndex = window.Canvas!.Items.ToList().FindIndex(i => i.Locked);
            Assert.True(frameIndex >= 0);
            window.Canvas.Select(frameIndex);
            bool confirmInvoked = false;
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(confirmInvoked);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("视口边框只读", window.EditError.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 9. Undo 一次纸面编辑 → 纸面还原 ----

    [AvaloniaFact]
    public async Task UndoAfterPaperMove_RestoresPaper()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 150, 100);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 250, 250));
            Assert.Single(window.Edit!.RecentTxIds);
            Assert.Equal(250, SavedPaperLine(CadAgent.Data.DrawingSession.Load(path)).StartPoint.X, 6);

            await ActAndWaitForEditAsync(window, () =>
                window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Empty(window.Edit.RecentTxIds);
            Assert.Equal(100, SavedPaperLine(CadAgent.Data.DrawingSession.Load(path)).StartPoint.X, 6);
            Assert.Equal(100, Assert.Single(window.Canvas!.Items,
                i => i.Space == RenderSpace.PaperSpace && !i.Locked && i.Kind == RenderKind.Line).Points[0].X, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 10. 跨空间两击（模型→纸面）：拒绝且无 tx（回归）----

    [AvaloniaFact]
    public void CrossSpaceTwoClicks_ModelThenPaper_Refused_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(PaperDxf());
        try
        {
            SelectLayoutSheet(window);
            int before = window.Canvas!.Items.Count;
            ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 500, 350); // 视口内 → 模型锚点
            Assert.NotNull(window.Canvas.PendingPoint);
            ClickCanvasAtWorld(window, 150, 300); // 视口外纸面 → 跨空间
            Assert.Null(window.Canvas.PendingPoint);
            Assert.True(window.EditError!.IsVisible);
            Assert.Contains("跨空间", window.EditError.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.Equal(before, window.Canvas.Items.Count);
        }
        finally { Cleanup(window, path, projectDir); }
    }
}
