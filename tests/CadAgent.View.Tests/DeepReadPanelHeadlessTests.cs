using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 天读证据面板 headless coverage ([AvaloniaFact])：左栏第 5 tab "天读证据"；
/// 初始加载 overlay 五区行 + 计数头部；勾选"显示中心线" → Canvas.ShowCenterlines
/// + 中心线叠加（无中心线时禁用）；点击原生参数行 → 画布按句柄选中；
/// 语义扫描完成（SemanticChanged）→ 面板重读 overlay（损坏→修复后错误消失）。
/// </summary>
public class DeepReadPanelHeadlessTests
{
    private const string ValidCenterline =
        "{\"kind\":\"line\",\"p1\":[0.0,0.0,0.0],\"p2\":[100.0,0.0,0.0],\"width\":200.0}";

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");
        public string OverlayPath => Path.Combine(AgentDir, "semantic.json");
        public string DxfPath { get; }
        public MainViewModel Vm { get; }
        public string Handle { get; }

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-deepread-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-WALL");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
            });
            Vm = new MainViewModel(DxfPath, ProjectDir);
            Assert.Null(Vm.ErrorMessage);
            Handle = Vm.Session.Doc.Entities.First().Handle.ToString();
        }

        /// <summary>写一份含原生参数/炸开证据（有效中心线）/块定义的抽样 overlay。</summary>
        public void WriteOverlay()
        {
            new SemanticOverlay
            {
                NativeEntities = new List<NativeEntityParam>
                {
                    new(Handle, "LINE", SemanticClass.Wall,
                        new Dictionary<string, string> { ["ObjectName"] = "LINE" },
                        new double[] { 0, 0, 0, 100, 0, 0 }, DateTimeOffset.UnixEpoch,
                        ReadOk: true, Layer: "W-WALL"),
                },
                ExplodeEvidence = new List<ExplodeEvidence>
                {
                    new(Handle, "TDbWall", "TDbWall", "W-WALL", new double[] { 0, 0, 0, 100, 0, 0 },
                        true, 1, 0,
                        new List<ExplodePrimitive> { new("line", new double[] { 0, 0, 0, 100, 0, 0 }, null, null) },
                        new Dictionary<string, string>(), ValidCenterline, DateTimeOffset.UnixEpoch),
                },
                BlockDefTdb = new List<BlockDefTdbEntry> { new("BLK-A", "TDbWall", 1, "HASH-A") },
            }.Save(OverlayPath);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }

    private static MainWindow Show(Fixture f)
    {
        var window = new MainWindow(f.Vm, new FixedIdentity(new OperatorIdentity("tester", Role.Review)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void DeepReadTab_LoadsSections_TogglesCenterlineAndSelectsRow()
    {
        using var f = new Fixture();
        f.WriteOverlay();
        MainWindow window = Show(f);
        try
        {
            Assert.Equal(5, window.LeftTabs!.ItemCount);
            Assert.Equal("天读证据", ((TabItem)window.LeftTabs.Items[4]!).Header);
            window.LeftTabs.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(window.DeepRead);
            Assert.Null(window.DeepRead!.ErrorMessage);
            Assert.Equal(1, window.DeepReadNativeList!.ItemCount);
            Assert.Equal(1, window.DeepReadExplodeList!.ItemCount);
            Assert.Equal(0, window.DeepReadSymbolList!.ItemCount); // 无符号库块前缀
            Assert.Equal(1, window.DeepReadBlockDefList!.ItemCount);
            Assert.Equal(1, window.DeepReadEvidenceList!.ItemCount);
            Assert.Contains("原生参数 1", window.DeepReadHeader!.Text);
            Assert.Contains("非放行依据", window.DeepReadHeader.Text);
            Assert.False(window.DeepReadError!.IsVisible);

            CheckBox show = window.DeepReadShowCenterlines!;
            Assert.True(show.IsEnabled); // 有中心线才可勾选
            Assert.False(window.Canvas!.ShowCenterlines); // 默认关闭
            show.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Canvas.ShowCenterlines);
            Assert.Single(window.Canvas.Centerlines);

            window.DeepReadNativeList.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Handle, window.Canvas.SelectedHandle);
            Assert.Contains(f.Handle, window.Canvas.HighlightedHandles);

            show.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Canvas.ShowCenterlines);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SemanticScanCompletion_ReloadsDeepReadPanel()
    {
        using var f = new Fixture();
        File.WriteAllText(f.OverlayPath, "{ not-json");
        MainWindow window = Show(f);
        try
        {
            window.LeftTabs!.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.False(string.IsNullOrEmpty(window.DeepRead!.ErrorMessage));
            Assert.True(window.DeepReadError!.IsVisible);
            Assert.Equal(0, window.DeepReadNativeList!.ItemCount);

            f.WriteOverlay(); // 修复损坏 overlay（模拟重新落盘）
            var tcs = new TaskCompletionSource();
            void Handler() => tcs.TrySetResult();
            window.Semantic!.SemanticChanged += Handler;
            try
            {
                window.SemanticScanButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Dispatcher.UIThread.RunJobs();
            }
            finally { window.Semantic!.SemanticChanged -= Handler; }

            Assert.Null(window.DeepRead.ErrorMessage);
            Assert.False(window.DeepReadError.IsVisible);
            Assert.Equal(1, window.DeepReadNativeList.ItemCount);
            Assert.Equal(1, window.DeepReadExplodeList!.ItemCount);
        }
        finally { window.Close(); }
    }
}
