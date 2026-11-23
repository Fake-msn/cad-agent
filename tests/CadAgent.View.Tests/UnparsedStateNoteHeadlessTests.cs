using ACadSharp.Entities;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 防假 Green 切片 T3：视图 census 警示。未解析天正对象不进入渲染模型（无句柄/几何），
/// 显示不完整——MainWindow StateNote 追加"含 N 个未解析天正对象，显示不完整（只读审查）"
/// （Unparsed.TotalCount&gt;0；census 经 MainViewModel sidecar 接线恢复）。
/// 窗口照常打开、图纸照常渲染（警示非崩溃）；画布仍占 LastChildFill（StateNote  dock Bottom）。
/// 无 census 健康路径由 CorruptStateHeadlessTests.HealthyState_NoErrorMessage_NoStateNote 钉住。
/// T5 顺手修①（R10 Minor#4）：补 .dwg working 降级链钉住——sidecar census 恢复警示
/// （WorkingDegrade_*）+ R9 加载失败优雅降级（WorkingOnlySourceMissing_*，不进入渲染/编辑）。
/// </summary>
public class UnparsedStateNoteHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-unparsed-note-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(100, 10, 0) });
            });
        }

        /// <summary>sidecar 注入 census（TCH_WALL×2，源哈希匹配）→ 有原生+未解析=Yellow 口径。</summary>
        public void InjectCensus() =>
            UnparsedCensusStore.Save(ProjectDir, DxfPath,
                new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));

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

    [AvaloniaFact]
    public void YellowUnparsed_WindowOpens_StateNoteReadOnlyWarning_DrawingRenders()
    {
        using var f = new Fixture();
        f.InjectCensus();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Session.Unparsed.TotalCount); // sidecar 接线恢复 census
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.NotNull(window.Canvas);
            Assert.Equal(2, window.Canvas!.Items.Count); // 原生实体照常渲染
            Assert.NotNull(window.ErrorMessage);
            Assert.Contains("含 2 个未解析天正对象，显示不完整（只读审查）", window.ErrorMessage);
            Assert.NotNull(window.StateNote);
            Assert.True(window.StateNote!.IsVisible);
            Assert.Contains("含 2 个未解析天正对象，显示不完整（只读审查）", window.StateNote.Text);
            // StateNote dock Bottom 不抢画布填充位（CorruptState 先例守卫）。
            Dispatcher.UIThread.RunJobs();
            DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            Assert.True(canvas.Bounds.Width > 0, $"DrawingCanvas collapsed: width={canvas.Bounds.Width}");
            Assert.True(canvas.Bounds.Height > 0, $"DrawingCanvas collapsed: height={canvas.Bounds.Height}");
            TextBlock note = window.StateNote!;
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(note));
            Assert.True(note.Bounds.Y >= canvas.Bounds.Bottom - 0.5,
                $"StateNote not below canvas: note.Y={note.Bounds.Y}, canvas.Bottom={canvas.Bounds.Bottom}");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// T5 顺手修①（R10 Minor#4 视图降级钉住）：.dwg working 降级路径——working DXF
    /// 丢失 TCH 对象后，MainViewModel 经 sidecar persisted census 恢复（Load working
    /// 分支），MainWindow ErrorMessage/StateNote 含未解析警示，原生实体照常渲染。
    /// 与既有 DXF 直载用例互补：本用例钉住 .dwg→working 二次加载降级链。
    /// </summary>
    [AvaloniaFact]
    public void WorkingDegrade_CensusRestoredFromSidecar_StateNoteWarning_Renders()
    {
        using var f = new DwgWorkingFixture();
        var first = new MainViewModel(f.DwgPath, f.ProjectDir); // 首载物化 working（合成源无 TCH → census Empty）
        Assert.Null(first.ErrorMessage);
        Assert.True(File.Exists(f.WorkingPath));
        Assert.Equal(0, first.Session.Unparsed.TotalCount);
        UnparsedCensusStore.Save(f.ProjectDir, f.DwgPath,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));

        var vm = new MainViewModel(f.DwgPath, f.ProjectDir); // working 分支 + persisted census 恢复

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Session.Unparsed.TotalCount); // sidecar 接线恢复 census（降级不丢警示依据）
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.NotNull(window.Canvas);
            Assert.Equal(2, window.Canvas!.Items.Count); // 原生实体照常渲染
            Assert.NotNull(window.ErrorMessage);
            Assert.Contains("含 2 个未解析天正对象，显示不完整（只读审查）", window.ErrorMessage);
            Assert.NotNull(window.StateNote);
            Assert.True(window.StateNote!.IsVisible);
            Assert.Contains("含 2 个未解析天正对象，显示不完整（只读审查）", window.StateNote.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// T5 顺手修①（R10 Minor#4 视图降级钉住）：R9 加载失败降级——working 副本无源图
    /// 且无 census → Load 抛 InvalidOperationException → MainViewModel.ErrorMessage
    /// 优雅降级；MainWindow 先行短路展示错误文本（窗口照常打开），不进入渲染/编辑
    /// （Canvas/StateNote null；加载失败不走 StateNote 通道，通道分离钉住）。
    /// </summary>
    [AvaloniaFact]
    public void WorkingOnlySourceMissing_LoadDegrades_ErrorShown_NoRenderNoEdit()
    {
        using var f = new DwgWorkingFixture();
        var first = new MainViewModel(f.DwgPath, f.ProjectDir); // 首载物化 working
        Assert.Null(first.ErrorMessage);
        Assert.True(File.Exists(f.WorkingPath));
        File.Delete(f.DwgPath); // 源缺失 + 无 sidecar → R9 第三分支（无法核验完整性）

        var vm = new MainViewModel(f.DwgPath, f.ProjectDir);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("无法核验完整性", vm.ErrorMessage);
        var window = new MainWindow(vm, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // 优雅降级：窗口照常打开、错误文本可见；不进入渲染/编辑。
            var errorText = Assert.IsType<TextBlock>(window.Content);
            Assert.Contains("无法核验完整性", errorText.Text);
            Assert.Null(window.Canvas); // 未构建渲染模型
            Assert.Null(window.StateNote); // 加载失败不走 StateNote 通道
            Assert.Null(window.ErrorMessage); // 通道分离：vm.ErrorMessage 先行短路
        }
        finally { window.Close(); }
    }

    /// <summary>.dwg working 降级夹具：源 .dwg 落项目目录内（working/sidecar 同目录，清理一并删除）。</summary>
    private sealed class DwgWorkingFixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DwgPath { get; }
        public string WorkingPath => DrawingSession.WorkingPathFor(DwgPath);

        public DwgWorkingFixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-unparsed-degrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ProjectDir);
            string tmp = DxfFixture.CreateTempDwg(doc =>
            {
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(100, 10, 0) });
            });
            DwgPath = Path.Combine(ProjectDir, "src.dwg");
            File.Move(tmp, DwgPath);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }
}
