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
}
