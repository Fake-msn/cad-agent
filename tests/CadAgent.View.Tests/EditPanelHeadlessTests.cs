using System.Text.Json;
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
using CadAgent.View.Rendering;
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
/// 视图闭环 T9：字高/缩放/旋转输入框（非法输入 fallback 3.5/1/0，UI 度→handler
/// 弧度）+ Delete 模态确认——headless 经 ConfirmDeleteOverride seam 注入
/// （ShowDialog 无 owner 消息泵不测，真模态走 Task 11 真机协议）。
/// </summary>
public class EditPanelHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string AuditDbPath => Path.Combine(AgentDir, "audit.db");

        /// <summary>withLumBlock=true 追加块定义 LUM（插块测试用；块内实体不进模型空间，既有计数断言不变）。
        /// withLumInsert=true 追加 LUM 块 + 模型空间 Insert@(50,10)（Imp#3 子图元句柄测试）；
        /// withDimension=true 追加带匿名块的 DimensionLinear（块内线 y=20，避开夹具 WALL 线）。</summary>
        public Fixture(bool withLumBlock = false, bool withLumInsert = false, bool withDimension = false)
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-edit-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
                if (withLumBlock || withLumInsert) DxfFixture.AddLumBlock(doc);
                if (withLumInsert)
                {
                    var block = doc.BlockRecords.First(b => b.Name == "LUM");
                    doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(50, 10, 0) });
                }
                if (withDimension)
                {
                    // 探针核定（RenderModelTests 先例）：Dimension.Block 可设且 DXF 往返保留匿名块。
                    var blk = new BlockRecord("*D1");
                    blk.Entities.Add(new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(10, 20, 0) });
                    blk.Entities.Add(new TextEntity { InsertPoint = new XYZ(5, 22, 0), Value = "10", Height = 2.5 });
                    doc.BlockRecords.Add(blk);
                    doc.Entities.Add(new DimensionLinear { Block = blk, DefinitionPoint = new XYZ(0, 20, 0), InsertionPoint = new XYZ(5, 25, 0) });
                }
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

    private static (MainWindow Window, Fixture F) ShowWindow(bool withLumBlock = false,
        bool withLumInsert = false, bool withDimension = false)
    {
        var f = new Fixture(withLumBlock, withLumInsert, withDimension);
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
            // 视图闭环 T9：字高/缩放/旋转输入框装配 + 默认值。
            Assert.NotNull(window.TextHeightInput);
            Assert.Equal("3.5", window.TextHeightInput!.Text);
            Assert.NotNull(window.BlockScaleInput);
            Assert.Equal("1", window.BlockScaleInput!.Text);
            Assert.NotNull(window.BlockRotationInput);
            Assert.Equal("0", window.BlockRotationInput!.Text);
            Assert.Null(window.ConfirmDeleteOverride); // 默认走真模态（seam 未注入）
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
            // 视图闭环 T9（sanctioned）：模态引入后第二击不再无条件删除——
            // headless 经 seam 注入确认 true（等价模态 Close(true)）。
            window.ConfirmDeleteOverride = _ => Task.FromResult(true);
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

    // ---- 视图闭环 T9：字高/缩放/旋转输入 + Delete 模态确认（seam 注入） ----

    [AvaloniaFact]
    public async Task TextTool_CustomHeight_UsedInEdit()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditTextInput!.Text = "注记";
            window.TextHeightInput!.Text = "7";
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 30, 10));

            Assert.Null(window.Edit!.ErrorMessage);
            // 渲染图元字高 = 输入值（不再固定 3.5）。
            RenderItem text = Assert.Single(window.Canvas!.Items, i => i.Kind == RenderKind.Text);
            Assert.Equal(7, text.TextHeight, 3);
            // 审计 view-edit args.height 同值（VM→StepEngine 契约无旁路）。
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord req = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "view-edit");
            JsonElement args = JsonDocument.Parse(req.Entry.ParamsJson).RootElement.GetProperty("args");
            Assert.Equal(7, args.GetProperty("height").GetDouble(), 3);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task TextTool_InvalidHeight_FallsBackDefault()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditTextInput!.Text = "注记";
            window.TextHeightInput!.Text = "abc"; // 非法输入 → fallback 3.5，不崩
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 30, 10));

            Assert.Null(window.Edit!.ErrorMessage);
            RenderItem text = Assert.Single(window.Canvas!.Items, i => i.Kind == RenderKind.Text);
            Assert.Equal(3.5, text.TextHeight, 3);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord req = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "view-edit");
            JsonElement args = JsonDocument.Parse(req.Entry.ParamsJson).RootElement.GetProperty("args");
            Assert.Equal(3.5, args.GetProperty("height").GetDouble(), 3);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task InsertBlockTool_ScaleRotation_UsedInEdit()
    {
        (MainWindow window, Fixture f) = ShowWindow(withLumBlock: true);
        try
        {
            ToolButton(window, "InsertBlock").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditBlockInput!.Text = "LUM";
            window.BlockScaleInput!.Text = "2";
            window.BlockRotationInput!.Text = "90"; // UI 度 → handler 弧度契约
            // 点击须落在画布视口内（夹具为水平线，y 过大映射到画布外；镜像 Text 测试 y=10）。
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 50, 10));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Equal(2, window.Canvas!.Items.Count); // WALL 线 + LUM 块内对角线展开
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord req = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "view-edit");
            JsonElement args = JsonDocument.Parse(req.Entry.ParamsJson).RootElement.GetProperty("args");
            Assert.Equal(2, args.GetProperty("scale").GetDouble(), 3);
            Assert.Equal(Math.PI / 2, args.GetProperty("rotation").GetDouble(), 6); // 90° → π/2
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task Delete_ConfirmOverrideFalse_NoTx()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            // seam 注入取消：模态逻辑等价 Close(false)（headless 无 owner 消息泵）。
            string? message = null;
            var asked = new TaskCompletionSource();
            window.ConfirmDeleteOverride = m =>
            {
                message = m;
                asked.TrySetResult();
                return Task.FromResult(false);
            };
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 50, 0); // 选中夹具线
            Assert.NotNull(window.Canvas!.SelectedHandle);

            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();

            // 取消：保持选中不清状态，实体仍在，无 delete-entity tx（连审计库都不创建）。
            Assert.Contains("确认删除", message);
            Assert.NotNull(window.Canvas.SelectedHandle);
            Assert.Single(window.Canvas.Items);
            Assert.Empty(window.Edit!.RecentTxIds);
            Assert.False(File.Exists(f.AuditDbPath));
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task Delete_ConfirmOverrideTrue_TxCommitted()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            window.ConfirmDeleteOverride = _ => Task.FromResult(true); // 等价 Close(true)
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ClickCanvasAtWorld(window, 50, 0);
            Assert.NotNull(window.Canvas!.SelectedHandle);

            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit!.ErrorMessage);
            Assert.Empty(window.Canvas.Items); // 实体消失
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            List<AuditRecord> records = audit.ReadAll().ToList();
            Assert.Single(records, r => r.Entry.Kind == "tx-start");
            Assert.Single(records, r => r.Entry.Kind == "tx-commit");
            AuditRecord step = Assert.Single(records, r => r.Entry.Kind == "step");
            Assert.Contains("delete-entity", step.Entry.ParamsJson);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    // ---- 最终审查 Imp#3：子图元后缀句柄（"{parent}:{i}"）编辑入口剥后缀 ----
    // 语义 = 编辑父实体/块整体（独立子图元编辑 deferred，spec §12 注记）。

    [AvaloniaFact]
    public async Task Move_InsertSubEntitySelected_MovesParentInsert()
    {
        (MainWindow window, Fixture f) = ShowWindow(withLumInsert: true);
        try
        {
            // 夹具：WALL 线 + LUM Insert@(50,10)（块内对角线 (49,9)-(51,11) 展开为子图元）。
            Assert.Equal(2, window.Canvas!.Items.Count);
            ToolButton(window, "Move").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ClickCanvasAtWorld(window, 50, 10); // 命中子图元 → 选中 "{insert}:0" + 记基准 (49,9)
            Assert.NotNull(window.Canvas.SelectedHandle);
            Assert.Contains(":", window.Canvas.SelectedHandle); // 子图元后缀口径（RenderModel）
            ulong parentHandle = ulong.Parse(window.Canvas.SelectedHandle!.Split(':')[0]);

            // 第二击空白处 (70,10) → dx=21, dy=1 → 父 Insert (50,10)→(71,11)。
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 70, 10));

            Assert.Null(window.Edit!.ErrorMessage); // 旧缺陷：ulong.TryParse("{h}:0") 失败 → 非法句柄
            Assert.Single(window.Edit.RecentTxIds);
            RenderItem child = Assert.Single(window.Canvas.Items, i => i.Handle.Contains(':'));
            Assert.Equal(70, child.Points[0].X, 3); // 子图元随父 Insert 平移（块整体语义）
            Assert.Equal(10, child.Points[0].Y, 3);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord step = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "step");
            Assert.Contains("move-entity", step.Entry.ParamsJson);
            // WithHandle 剥后缀后以 ulong 入处理器 → 审计 Params.handle 为数字（顶层句柄，无后缀）。
            JsonElement stepParams = JsonDocument.Parse(step.Entry.ParamsJson).RootElement.GetProperty("Params");
            Assert.Equal(parentHandle, stepParams.GetProperty("handle").GetUInt64());
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task Delete_DimensionSubEntitySelected_DeletesParentDimension()
    {
        (MainWindow window, Fixture f) = ShowWindow(withDimension: true);
        try
        {
            // 夹具：WALL 线 + Dimension 匿名块展开（线 y=20 + 文本 (5,22)）→ 3 render items。
            Assert.Equal(3, window.Canvas!.Items.Count);
            window.ConfirmDeleteOverride = _ => Task.FromResult(true);
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ClickCanvasAtWorld(window, 5, 20); // 命中标注子图元线 → 选中 "{dim}:0"
            Assert.NotNull(window.Canvas.SelectedHandle);
            Assert.Contains(":", window.Canvas.SelectedHandle);
            ulong parentHandle = ulong.Parse(window.Canvas.SelectedHandle!.Split(':')[0]);

            await ActAndWaitForEditAsync(window, () =>
                ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            Assert.Null(window.Edit!.ErrorMessage); // 旧缺陷：后缀句柄 → 非法句柄，标注不可删
            Assert.Single(window.Canvas.Items); // 父标注整体删除（两子图元一并消失），WALL 线保留
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord step = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "step");
            Assert.Contains("delete-entity", step.Entry.ParamsJson);
            // WithHandle 剥后缀后以 ulong 入处理器 → 审计 Params.handle 为数字（父标注顶层句柄）。
            JsonElement stepParams = JsonDocument.Parse(step.Entry.ParamsJson).RootElement.GetProperty("Params");
            Assert.Equal(parentHandle, stepParams.GetProperty("handle").GetUInt64());
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    // ---- 最终审查顺手修（T9 deferred）：字高/缩放/旋转 double.IsFinite 守卫 ----

    [AvaloniaFact]
    public async Task InsertBlockTool_NaNRotation_FallsBackZero()
    {
        (MainWindow window, Fixture f) = ShowWindow(withLumBlock: true);
        try
        {
            ToolButton(window, "InsertBlock").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditBlockInput!.Text = "LUM";
            window.BlockRotationInput!.Text = "NaN"; // NaN → fallback 0（旧缺陷：NaN 入 JSON 序列化抛异常）
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 50, 10));

            Assert.Null(window.Edit!.ErrorMessage);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord req = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "view-edit");
            JsonElement args = JsonDocument.Parse(req.Entry.ParamsJson).RootElement.GetProperty("args");
            Assert.Equal(0, args.GetProperty("rotation").GetDouble(), 6);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task InsertBlockTool_InfinityScale_FallsBackOne()
    {
        (MainWindow window, Fixture f) = ShowWindow(withLumBlock: true);
        try
        {
            ToolButton(window, "InsertBlock").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditBlockInput!.Text = "LUM";
            window.BlockScaleInput!.Text = "Infinity"; // +∞ 通过 >0 检查 → 须 IsFinite 拦截 → 1
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 50, 10));

            Assert.Null(window.Edit!.ErrorMessage);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord req = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "view-edit");
            JsonElement args = JsonDocument.Parse(req.Entry.ParamsJson).RootElement.GetProperty("args");
            Assert.Equal(1, args.GetProperty("scale").GetDouble(), 6);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public async Task TextTool_InfinityHeight_FallsBackDefault()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            ToolButton(window, "Text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.EditTextInput!.Text = "注记";
            window.TextHeightInput!.Text = "Infinity"; // +∞ 通过 >0 检查 → 须 IsFinite 拦截 → 3.5
            await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 30, 10));

            Assert.Null(window.Edit!.ErrorMessage);
            RenderItem text = Assert.Single(window.Canvas!.Items, i => i.Kind == RenderKind.Text);
            Assert.Equal(3.5, text.TextHeight, 3);
            using var audit = new SqliteAuditStore(f.AuditDbPath);
            AuditRecord req = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "view-edit");
            JsonElement args = JsonDocument.Parse(req.Entry.ParamsJson).RootElement.GetProperty("args");
            Assert.Equal(3.5, args.GetProperty("height").GetDouble(), 3);
            Assert.True(audit.VerifyChain(out string error), error);
        }
        finally { Cleanup(window, f); }
    }
}
