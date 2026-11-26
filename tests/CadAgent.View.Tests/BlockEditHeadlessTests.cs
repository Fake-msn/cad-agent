using System.Reflection;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Objects.Evaluations;
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
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// Plan-3 Task 6：View 子图元模式 + 块定义编辑（确认/计数/路由）。
/// 夹具走真实 DXF 写/读往返（DxfFixture 口径）；块局部增量期望值由正向
/// <see cref="Affine.InsertAffine"/> 推导（旋转+缩放用例不手算）。
/// Toggle 关：模型幅面父整体语义不变（既有 EditPanel 用例保持）、布局幅面新提示拒绝；
/// Toggle 开：块定义编辑（全实例生效）+ 模态确认 + 实例计数；匿名/动态/XRef 拒绝；
/// 属性项（AttributeTag）仍实例级。
/// </summary>
public class BlockEditHeadlessTests
{
    private const string TitleBlockName = "TITLE_BLOCK";
    private const string BlockTextValue = "旧标题";

    // ---- 夹具 ----

    /// <summary>块定义 TITLE_BLOCK（线 (0,0)-(10,0) + 文字 (0,5)）+ 两实例（共享定义）。</summary>
    private static void AddTitleBlock(CadDocument doc)
    {
        doc.Layers.Add(new Layer("W-墙体"));
        var block = new BlockRecord(TitleBlockName);
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        block.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 5, 0), Value = BlockTextValue, Height = 2 });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0) });
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(100, 0, 0) });
        // 无框 S0 的 ModelBounds 只取顶层实体锚点（Insert=插入点），块内容不参与——
        // 顶层包络线保证 CropToFrame 不裁掉块子图元（文字 y=5 等）。
        doc.Entities.Add(new Line { StartPoint = new XYZ(-20, -20, 0), EndPoint = new XYZ(120, -20, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(-20, 20, 0), EndPoint = new XYZ(120, 20, 0) });
    }

    private static string TwoInsertDxf() => DxfFixture.CreateTempDxf(AddTitleBlock);

    /// <summary>两实例 + 布局视口（paper 1000×700，viewHeight 200 → s=3.5，viewTarget=(50,0)）。</summary>
    private static string TwoInsertLayoutDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        AddTitleBlock(doc);
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(50, 0, 0),
            ViewHeight = 200,
        });
    });

    /// <summary>两实例 + 布局视口 + 纸面线（状态栏目标空间夹具）。</summary>
    private static string TwoInsertLayoutWithPaperDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        AddTitleBlock(doc);
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(50, 0, 0),
            ViewHeight = 200,
        });
        layout.AssociatedBlock!.Entities.Add(
            new Line { StartPoint = new XYZ(100, 100, 0), EndPoint = new XYZ(200, 100, 0) });
    });

    /// <summary>旋转 90° + 缩放 (2,3) 的单个实例（局部增量精确性夹具）。</summary>
    private static string RotatedScaledDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("ROT_BLOCK");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        block.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 5, 0), Value = BlockTextValue, Height = 2 });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(12, -7, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 3, ZScale = 1
        });
        // 包络线（同两实例夹具）：块内容不参与 ModelBounds，防 CropToFrame 裁掉子图元。
        doc.Entities.Add(new Line { StartPoint = new XYZ(-20, -15, 0), EndPoint = new XYZ(40, -15, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(-20, 15, 0), EndPoint = new XYZ(40, 15, 0) });
    });

    /// <summary>Dimension 匿名块（*D）：子图元进入渲染但块不可编辑。</summary>
    private static string DimensionDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var blk = new BlockRecord("*D1");
        // 真实 DXF 的标注块由生成器置 Anonymous 标志；写入器不自动补，夹具显式置位。
        blk.Flags |= BlockTypeFlags.Anonymous;
        blk.Entities.Add(new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(10, 20, 0) });
        blk.Entities.Add(new TextEntity { InsertPoint = new XYZ(5, 22, 0), Value = "10", Height = 2.5 });
        doc.BlockRecords.Add(blk);
        doc.Entities.Add(new DimensionLinear
        {
            Block = blk, DefinitionPoint = new XYZ(0, 20, 0), InsertionPoint = new XYZ(5, 25, 0)
        });
    });

    /// <summary>Insert + ATTRIB（属性项实例语义夹具；块内无实体）。</summary>
    private static string AttributeDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("TB_TITLE");
        doc.BlockRecords.Add(block);
        // 顶层包络线：幅面 ModelBounds 取顶层锚点，须覆盖属性锚点 (60,4) 否则被裁掉。
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        var insert = new Insert(block) { InsertPoint = new XYZ(50, 10, 0) };
        var attr = new AttributeEntity { Tag = "图名", Value = "旧图名", Height = 5 };
        attr.InsertPoint = new XYZ(60, 4, 0);
        attr.AlignmentPoint = new XYZ(60, 4, 0);
        insert.Attributes.Add(attr);
        doc.Entities.Add(insert);
    });

    /// <summary>动态块标志：ACadSharp 3.8.35 <see cref="BlockRecord.IsDynamic"/> 只读，反射注入 XDictionary。</summary>
    private static void ForceDynamic(BlockRecord block)
    {
        var xd = new CadDictionary();
        xd.Add("ACAD_ENHANCEDBLOCK", new EvaluationGraph());
        typeof(CadObject).GetField("_xdictionary", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(block, xd);
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private static string UniqueProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-block-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>mutate 在 MainViewModel 加载后、窗口构造前执行（动态/XRef 标志须作用于已加载文档）。</summary>
    private static (MainWindow Window, string Path, string ProjectDir) Show(
        string dxfPath, Action<CadDocument>? mutate = null)
    {
        string projectDir = UniqueProjectDir();
        var vm = new MainViewModel(dxfPath, projectDir);
        Assert.Null(vm.ErrorMessage);
        mutate?.Invoke(vm.Session.Doc);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
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

    private static void EnableSubEntityMode(MainWindow window)
    {
        Assert.NotNull(window.SubEntityToggle);
        window.SubEntityToggle!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>块子图元图元索引（模型空间、非只读）；kind 限定可选。</summary>
    private static int BlockChildIndex(MainWindow window, RenderKind? kind = null) =>
        window.Canvas!.Items.ToList().FindIndex(i => i.BlockName is not null && !i.Locked
            && i.Space == RenderSpace.ModelSpace && (kind is null || i.Kind == kind));

    private static Line SavedBlockLine(DrawingSession saved, string blockName) =>
        saved.Doc.BlockRecords.First(b => b.Name == blockName).Entities.OfType<Line>().Single();

    // ---- 1. Toggle 关：布局幅面块子图元 → 新提示拒绝、无 tx ----

    [AvaloniaFact]
    public void ToggleOff_LayoutBlockChild_NewMessage_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertLayoutDxf());
        try
        {
            window.SheetSelector!.SelectedIndex = 1; // 布局幅面
            Dispatcher.UIThread.RunJobs();
            int index = BlockChildIndex(window, RenderKind.Line);
            Assert.True(index >= 0, "夹具块子图元未入视口");

            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            bool confirmInvoked = false;
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            window.Canvas!.Select(index);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.SubEntityToggle!.IsChecked);
            Assert.False(confirmInvoked); // 守卫先于确认
            Assert.True(window.EditError?.IsVisible ?? false);
            Assert.Contains("块子图元需开启「子图元」模式（全实例生效）", window.EditError!.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
            var saved = DrawingSession.Load(path);
            Assert.Equal(2, saved.Doc.Entities.OfType<Insert>().Count()); // 父 Insert 原样保留
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 2. Toggle 开：块内线 Move → 两实例同步位移 + 定义坐标 + 审计含 block ----

    [AvaloniaFact]
    public async Task ToggleOn_MoveBlockLine_ShiftsBothInstances_AndDefinition()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf());
        try
        {
            EnableSubEntityMode(window);
            Assert.Contains("子图元模式：块内编辑=全实例生效", window.Status!.Text); // 状态栏提示
            int index = BlockChildIndex(window, RenderKind.Line);
            Assert.True(index >= 0);

            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 5, 0); // 命中实例 1 的子线 (0,0)-(10,0)
            Assert.NotNull(window.Canvas!.SelectedHandle);

            string? confirmMessage = null;
            window.ConfirmEditOverride = msg => { confirmMessage = msg; return Task.FromResult(true); };
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 20, 0)); // 世界增量 (20,0)

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(window.Edit.RecentTxIds);
            Assert.NotNull(confirmMessage);
            Assert.Contains($"将修改块定义 {TitleBlockName}", confirmMessage);
            Assert.Contains("约 2 个实例", confirmMessage);
            var saved = DrawingSession.Load(path);
            Line line = SavedBlockLine(saved, TitleBlockName);
            Assert.Equal(20, line.StartPoint.X, 6); // 局部增量=世界增量（identity insert）
            Assert.Equal(0, line.StartPoint.Y, 6);
            Assert.Equal(30, line.EndPoint.X, 6);
            // 两个实例的渲染几何同步位移
            List<RenderItem> lines = window.Canvas.Items
                .Where(i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Line).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Single(lines, i => Math.Abs(i.Points[0].X - 20) < 1e-6);
            Assert.Single(lines, i => Math.Abs(i.Points[0].X - 120) < 1e-6);
            using var audit = new SqliteAuditStore(Path.Combine(projectDir, ".cadagent", "audit.db"));
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "step"
                && r.Entry.ParamsJson.Contains("move-entity")
                && r.Entry.ParamsJson.Contains(TitleBlockName));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 3. 旋转 90° + 缩放 (2,3)：局部增量精确（经正向 InsertAffine 推导） ----

    [AvaloniaFact]
    public async Task ToggleOn_MoveRotatedScaledChild_LocalDeltaExact()
    {
        (MainWindow window, string path, string projectDir) = Show(RotatedScaledDxf());
        try
        {
            EnableSubEntityMode(window);
            int index = BlockChildIndex(window, RenderKind.Line);
            Assert.True(index >= 0);
            Insert insert = DrawingSession.Load(path).Doc.Entities.OfType<Insert>().Single();
            Affine t = Affine.InsertAffine(insert);
            (double rx, double ry) = t.Apply(0, 0); // 渲染起点（_moveBase）
            (double ex, double ey) = t.Apply(10, 0); // 渲染终点

            // 期望局部增量 (1, 0.5) → 世界增量经正向变换推导（不手算旋转/缩放矩阵）
            const double ldx = 1.0, ldy = 0.5;
            double wdx = t.A * ldx + t.B * ldy;
            double wdy = t.C * ldx + t.D * ldy;

            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, (rx + ex) / 2, (ry + ey) / 2);
            Assert.NotNull(window.Canvas!.SelectedHandle);

            window.ConfirmEditOverride = _ => Task.FromResult(true);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, rx + wdx, ry + wdy));

            Assert.Null(window.Edit!.ErrorMessage);
            var saved = DrawingSession.Load(path);
            Line line = SavedBlockLine(saved, "ROT_BLOCK");
            Assert.Equal(ldx, line.StartPoint.X, 9);
            Assert.Equal(ldy, line.StartPoint.Y, 9);
            Assert.Equal(10 + ldx, line.EndPoint.X, 9);
            Assert.Equal(ldy, line.EndPoint.Y, 9);
            // 渲染回验：块内线渲染起点 = 原起点 + 正向映射的世界增量
            RenderItem after = window.Canvas.Items.First(i => i.BlockName == "ROT_BLOCK"
                && i.Kind == RenderKind.Line && !i.Locked);
            Assert.Equal(rx + wdx, after.Points[0].X, 6);
            Assert.Equal(ry + wdy, after.Points[0].Y, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 4. 布局幅面模型子图元：纸面增量 ÷ 视口比例 → 局部增量 ----

    [AvaloniaFact]
    public async Task ToggleOn_LayoutMove_ScalesPaperDeltaToBlockLocal()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertLayoutDxf());
        try
        {
            window.SheetSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            EnableSubEntityMode(window);
            List<RenderItem> lines = window.Canvas!.Items
                .Where(i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Line && !i.Locked)
                .OrderBy(i => i.Points[0].X).ToList();
            Assert.Equal(2, lines.Count);
            RenderItem first = lines[0]; // 实例 1：纸面 (325,350)-(360,350)
            (double px, double py) = first.Points[0];
            Assert.Equal(325, px, 6);
            Assert.Equal(350, py, 6);

            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, (first.Points[0].X + first.Points[1].X) / 2, py);
            Assert.NotNull(window.Canvas!.SelectedHandle);

            window.ConfirmEditOverride = _ => Task.FromResult(true);
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, px + 15, py + 40));

            Assert.Null(window.Edit!.ErrorMessage);
            var saved = DrawingSession.Load(path);
            Line line = SavedBlockLine(saved, TitleBlockName);
            Assert.Equal(15.0 / 3.5, line.StartPoint.X, 9); // 纸面 Δ ÷ s(700/200)
            Assert.Equal(40.0 / 3.5, line.StartPoint.Y, 9);
            List<RenderItem> after = window.Canvas.Items
                .Where(i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Line && !i.Locked)
                .OrderBy(i => i.Points[0].X).ToList();
            Assert.Equal(px + 15, after[0].Points[0].X, 6); // 全实例纸面位移 = 纸面增量
            Assert.Equal(py + 40, after[0].Points[0].Y, 6);
            Assert.Equal(px + 350 + 15, after[1].Points[0].X, 6);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 5. Delete 块内线 → 两实例均缺失 ----

    [AvaloniaFact]
    public async Task ToggleOn_DeleteBlockLine_RemovesFromBothInstances()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf());
        try
        {
            EnableSubEntityMode(window);
            int index = BlockChildIndex(window, RenderKind.Line);
            Assert.True(index >= 0);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var messages = new List<string>();
            window.ConfirmEditOverride = msg => { messages.Add(msg); return Task.FromResult(true); };
            window.Canvas!.Select(index);
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(messages);
            Assert.Contains("将修改块定义", messages[0]); // 块定义确认（非实例删除确认）
            var saved = DrawingSession.Load(path);
            Assert.Empty(saved.Doc.BlockRecords.First(b => b.Name == TitleBlockName).Entities.OfType<Line>());
            Assert.Equal(2, saved.Doc.Entities.OfType<Insert>().Count());
            Assert.DoesNotContain(window.Canvas.Items,
                i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Line);
            Assert.Equal(2, window.Canvas.Items.Count(i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Text));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 6. ChangeLayer 块内文字 → 两实例同步改层 ----

    [AvaloniaFact]
    public async Task ToggleOn_ChangeLayerBlockText_UpdatesDefinition()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf());
        try
        {
            EnableSubEntityMode(window);
            int index = BlockChildIndex(window, RenderKind.Text);
            Assert.True(index >= 0);
            ToolButton(window, "ChangeLayer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.ChangeLayer, window.Edit!.ActiveTool);
            window.Canvas!.Select(index);
            window.ConfirmEditOverride = _ => Task.FromResult(true);

            await ActAndWaitForEditAsync(window, () =>
                window.EditLayerSelector!.SelectedItem = "W-墙体");

            Assert.Null(window.Edit.ErrorMessage);
            var saved = DrawingSession.Load(path);
            TextEntity text = saved.Doc.BlockRecords.First(b => b.Name == TitleBlockName)
                .Entities.OfType<TextEntity>().Single();
            Assert.Equal("W-墙体", text.Layer.Name);
            List<RenderItem> texts = window.Canvas!.Items
                .Where(i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Text).ToList();
            Assert.Equal(2, texts.Count);
            Assert.All(texts, t => Assert.Equal("W-墙体", t.Layer));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 7. SetText 块内文字 → 两实例同步改值 ----

    [AvaloniaFact]
    public async Task ToggleOn_SetTextBlockText_UpdatesDefinition()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf());
        try
        {
            EnableSubEntityMode(window);
            ToolButton(window, "改文字").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditTool.SetText, window.Edit!.ActiveTool);
            window.EditTextInput!.Text = "新标题";
            window.ConfirmEditOverride = _ => Task.FromResult(true);

            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 0, 5));

            Assert.Null(window.Edit.ErrorMessage);
            var saved = DrawingSession.Load(path);
            TextEntity text = saved.Doc.BlockRecords.First(b => b.Name == TitleBlockName)
                .Entities.OfType<TextEntity>().Single();
            Assert.Equal("新标题", text.Value);
            List<RenderItem> texts = window.Canvas!.Items
                .Where(i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Text).ToList();
            Assert.Equal(2, texts.Count);
            Assert.All(texts, t => Assert.Equal("新标题", t.Text));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 8. 取消确认 → 无 tx、盘上不变 ----

    [AvaloniaFact]
    public void ToggleOn_ConfirmFalse_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf());
        try
        {
            EnableSubEntityMode(window);
            bool confirmInvoked = false;
            window.ConfirmEditOverride = _ => { confirmInvoked = true; return Task.FromResult(false); };
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 5, 0);
            ClickCanvasAtWorld(window, 25, 0);
            Dispatcher.UIThread.RunJobs();

            Assert.True(confirmInvoked);
            Assert.Empty(window.Edit!.RecentTxIds);
            var saved = DrawingSession.Load(path);
            Line line = SavedBlockLine(saved, TitleBlockName);
            Assert.Equal(0, line.StartPoint.X, 9);
            Assert.Equal(10, line.EndPoint.X, 9);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 9. 匿名块（Dimension 子图元）拒绝 ----

    [AvaloniaFact]
    public void ToggleOn_DimensionChild_RefusedProtected_NoTx()
    {
        (MainWindow window, string path, string projectDir) = Show(DimensionDxf());
        try
        {
            EnableSubEntityMode(window);
            int index = window.Canvas!.Items.ToList().FindIndex(i => i.BlockName is not null);
            Assert.True(index >= 0, "Dimension 匿名块子图元未渲染");
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            bool confirmInvoked = false;
            window.ConfirmEditOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            window.ConfirmDeleteOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            window.Canvas.Select(index);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(confirmInvoked);
            Assert.True(window.EditError?.IsVisible ?? false);
            Assert.Contains("块不可编辑", window.EditError!.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 10. Dynamic / XRef 块拒绝 ----

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToggleOn_DynamicOrXRefBlock_RefusedProtected_NoTx(bool dynamic)
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf(), doc =>
        {
            BlockRecord block = doc.BlockRecords.First(b => b.Name == TitleBlockName);
            if (dynamic) ForceDynamic(block);
            else block.Flags |= BlockTypeFlags.XRef;
        });
        try
        {
            EnableSubEntityMode(window);
            int index = BlockChildIndex(window, RenderKind.Line);
            Assert.True(index >= 0);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            bool confirmInvoked = false;
            window.ConfirmEditOverride = _ => { confirmInvoked = true; return Task.FromResult(true); };
            window.Canvas!.Select(index);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(confirmInvoked);
            Assert.True(window.EditError?.IsVisible ?? false);
            Assert.Contains("块不可编辑", window.EditError!.Text);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.False(File.Exists(Path.Combine(projectDir, ".cadagent", "audit.db")));
            var saved = DrawingSession.Load(path);
            Assert.Single(saved.Doc.BlockRecords.First(b => b.Name == TitleBlockName)
                .Entities.OfType<Line>());
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 11. 属性项在 Toggle 开时仍实例级（不误入块编辑） ----

    [AvaloniaFact]
    public async Task ToggleOn_AttributeItem_StillInstanceLevel()
    {
        (MainWindow window, string path, string projectDir) = Show(AttributeDxf());
        try
        {
            EnableSubEntityMode(window);
            int index = window.Canvas!.Items.ToList().FindIndex(i => i.AttributeTag is not null);
            Assert.True(index >= 0, "属性图元未渲染");
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var messages = new List<string>();
            window.ConfirmDeleteOverride = msg => { messages.Add(msg); return Task.FromResult(true); };
            window.Canvas.Select(index);
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Single(messages);
            Assert.DoesNotContain("将修改块定义", messages[0]); // 实例级确认，不进块定义分支
            Assert.Contains("确认删除选中实体", messages[0]);
            var saved = DrawingSession.Load(path);
            Insert insert = Assert.Single(saved.Doc.Entities.OfType<Insert>()); // 父 Insert 保留
            Assert.Empty(insert.Attributes); // 仅删除实例属性
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 12. Undo 还原块定义（两实例恢复） ----

    [AvaloniaFact]
    public async Task ToggleOn_UndoRestoresBlockDefinition_ForBothInstances()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertDxf());
        try
        {
            EnableSubEntityMode(window);
            int index = BlockChildIndex(window, RenderKind.Line);
            Assert.True(index >= 0);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.ConfirmEditOverride = _ => Task.FromResult(true);
            window.Canvas!.Select(index);
            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Single(window.Edit!.RecentTxIds);
            Assert.DoesNotContain(window.Canvas.Items,
                i => i.BlockName == TitleBlockName && i.Kind == RenderKind.Line);

            await ActAndWaitForEditAsync(window, () =>
                window.UndoButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit.ErrorMessage);
            Assert.Empty(window.Edit.RecentTxIds);
            var saved = DrawingSession.Load(path);
            Line line = SavedBlockLine(saved, TitleBlockName);
            Assert.Equal(0, line.StartPoint.X, 9);
            Assert.Equal(10, line.EndPoint.X, 9);
            Assert.Equal(2, window.Canvas.Items.Count(i =>
                i.BlockName == TitleBlockName && i.Kind == RenderKind.Line)); // 两实例恢复
        }
        finally { Cleanup(window, path, projectDir); }
    }

    // ---- 13. 状态栏目标空间：块定义 / 纸面 / 模型 ----

    [AvaloniaFact]
    public void ToggleOn_StatusBar_ShowsTargetSpace()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoInsertLayoutWithPaperDxf());
        try
        {
            window.SheetSelector!.SelectedIndex = 1; // 布局幅面
            Dispatcher.UIThread.RunJobs();
            EnableSubEntityMode(window);

            int blockIndex = BlockChildIndex(window, RenderKind.Line);
            Assert.True(blockIndex >= 0, "夹具块子图元未入视口");
            window.Canvas!.Select(blockIndex);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains($"目标 块定义 {TitleBlockName}", window.Status!.Text);

            int paperIndex = window.Canvas.Items.ToList().FindIndex(i =>
                i.Space == RenderSpace.PaperSpace && !i.Locked);
            Assert.True(paperIndex >= 0, "夹具纸面线未渲染");
            window.Canvas.Select(paperIndex);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("目标 纸面", window.Status.Text);
            Assert.DoesNotContain("目标 块定义", window.Status.Text);

            int modelIndex = window.Canvas.Items.ToList().FindIndex(i =>
                i.Space == RenderSpace.ModelSpace && i.BlockName is null && !i.Locked);
            Assert.True(modelIndex >= 0, "夹具模型图元未入视口");
            window.Canvas.Select(modelIndex);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("目标 模型", window.Status.Text);
        }
        finally { Cleanup(window, path, projectDir); }
    }
}
