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
/// + 中心线叠加（无中心线时禁用；重读后数据消失 → 自动取消勾选并禁用）；
/// 点击 a/b/e 区行 → 画布按句柄定位（缺失句柄 → 清选中 + 状态栏提示）；
/// 编辑完成（RefreshAfterEdit）→ 面板重读 overlay（行数/块定义来源哈希联动）；
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

        /// <summary>写一份含原生参数/炸开证据/块定义的抽样 overlay。
        /// centerline=null → 无中心线行（开关应禁用）；extraNativeRows → 追加
        /// 不在渲染图元中的句柄行（EE01/EE02，定位失败路径）；blockDefHash →
        /// 块定义来源哈希（与当前图纸哈希比对）。</summary>
        public void WriteOverlay(string? centerline = ValidCenterline, int extraNativeRows = 0,
            string? blockDefHash = "HASH-A")
        {
            var natives = new List<NativeEntityParam>
            {
                new(Handle, "LINE", SemanticClass.Wall,
                    new Dictionary<string, string> { ["ObjectName"] = "LINE" },
                    new double[] { 0, 0, 0, 100, 0, 0 }, DateTimeOffset.UnixEpoch,
                    ReadOk: true, Layer: "W-WALL"),
            };
            for (int i = 0; i < extraNativeRows; i++)
                natives.Add(new NativeEntityParam($"EE0{i + 1}", "TDbWall", SemanticClass.Unknown,
                    new Dictionary<string, string>(), null, DateTimeOffset.UnixEpoch, ReadOk: false));
            new SemanticOverlay
            {
                NativeEntities = natives,
                ExplodeEvidence = new List<ExplodeEvidence>
                {
                    new(Handle, "TDbWall", "TDbWall", "W-WALL", new double[] { 0, 0, 0, 100, 0, 0 },
                        true, 1, 0,
                        new List<ExplodePrimitive> { new("line", new double[] { 0, 0, 0, 100, 0, 0 }, null, null) },
                        new Dictionary<string, string>(), centerline, DateTimeOffset.UnixEpoch),
                },
                BlockDefTdb = new List<BlockDefTdbEntry> { new("BLK-A", "TDbWall", 1, blockDefHash) },
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

    /// <summary>触发语义扫描并等待 SemanticChanged（ReloadDeepRead 衔接点）。</summary>
    private static async Task TriggerSemanticScanAsync(MainWindow window)
    {
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
    }

    /// <summary>点击一次真实编辑（Text），等待 EditCompleted（RefreshAfterEdit 衔接点）。</summary>
    private static async Task AddTextViaEditAsync(MainWindow window)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        window.Edit!.EditCompleted += Handler;
        try
        {
            _ = window.Edit.AddTextAsync(50, 5, "T", 3.5);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Edit!.EditCompleted -= Handler; }
    }

    private static int RowIndex(ListBox list, string handle)
    {
        for (int i = 0; i < list.ItemCount; i++)
            if (list.Items[i] is NativeParamRow r && string.Equals(r.Handle, handle, StringComparison.Ordinal))
                return i;
        return -1;
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
            await TriggerSemanticScanAsync(window);

            Assert.Null(window.DeepRead.ErrorMessage);
            Assert.False(window.DeepReadError.IsVisible);
            Assert.Equal(1, window.DeepReadNativeList.ItemCount);
            Assert.Equal(1, window.DeepReadExplodeList!.ItemCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CenterlineToggle_DisabledWhenNoCenterlines()
    {
        using var f = new Fixture();
        f.WriteOverlay(centerline: null);
        MainWindow window = Show(f);
        try
        {
            window.LeftTabs!.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.DeepRead!.HasCenterlines);
            CheckBox show = window.DeepReadShowCenterlines!;
            Assert.False(show.IsEnabled); // 无中心线不可开
            Assert.NotEqual(true, show.IsChecked);
            Assert.Empty(window.Canvas!.Centerlines);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CenterlineToggle_AutoUncheckedWhenDataDisappearsAfterReload()
    {
        using var f = new Fixture();
        f.WriteOverlay();
        MainWindow window = Show(f);
        try
        {
            window.LeftTabs!.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            CheckBox show = window.DeepReadShowCenterlines!;
            Assert.True(show.IsEnabled);
            show.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Canvas!.ShowCenterlines);

            f.WriteOverlay(centerline: null); // 重读后中心线数据消失
            await TriggerSemanticScanAsync(window);

            Assert.False(window.DeepRead!.HasCenterlines);
            Assert.False(show.IsEnabled);
            Assert.NotEqual(true, show.IsChecked); // 自动取消勾选
            Assert.False(window.Canvas.ShowCenterlines);
            Assert.Empty(window.Canvas.Centerlines);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ExplodeAndEvidenceRowClick_LocateByHandle()
    {
        using var f = new Fixture();
        f.WriteOverlay();
        MainWindow window = Show(f);
        try
        {
            window.LeftTabs!.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, window.DeepReadExplodeList!.ItemCount);
            window.DeepReadExplodeList.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Handle, window.Canvas!.SelectedHandle); // b 区行点击 → 定位
            Assert.Contains(f.Handle, window.Canvas.HighlightedHandles);

            window.Canvas.Select(null);
            Assert.Equal(1, window.DeepReadEvidenceList!.ItemCount);
            window.DeepReadEvidenceList.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Handle, window.Canvas.SelectedHandle); // e 区行点击 → 定位
            Assert.Contains(f.Handle, window.Canvas.HighlightedHandles);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RowClick_MissingHandle_ClearsSelectionAndShowsStatusNote()
    {
        using var f = new Fixture();
        f.WriteOverlay(extraNativeRows: 1); // EE01 仅存在于深读证据（不在渲染图元）
        MainWindow window = Show(f);
        try
        {
            window.LeftTabs!.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();

            int missing = RowIndex(window.DeepReadNativeList!, "EE01");
            Assert.True(missing >= 0);
            window.DeepReadNativeList!.SelectedIndex = missing;
            Dispatcher.UIThread.RunJobs();

            Assert.Null(window.Canvas!.SelectedIndex); // 清画布选中
            Assert.Empty(window.Canvas.HighlightedHandles); // 清定位高亮
            Assert.Contains(MainWindow.LocateMissNote, window.Status!.Text); // 状态栏提示

            int located = RowIndex(window.DeepReadNativeList, f.Handle);
            window.DeepReadNativeList.SelectedIndex = located;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Handle, window.Canvas.SelectedHandle);
            Assert.DoesNotContain(MainWindow.LocateMissNote, window.Status.Text); // 下次成功定位清除
        }
        finally { window.Close(); }
    }

    [Fact]
    public void BlockDefRow_Format_MismatchWordingAndNullDash()
    {
        string mismatch = MainWindow.FormatBlockDefRow(
            new BlockDefRow("BLK-A", "TDbWall", 2, "HASH-A", IsCurrentDrawing: false));
        Assert.Contains("来源哈希不一致（图纸可能已编辑或来自他图）", mismatch);
        Assert.DoesNotContain("异图", mismatch);
        Assert.DoesNotContain("非本图", mismatch);

        string unknown = MainWindow.FormatBlockDefRow(
            new BlockDefRow("BLK-B", "TDbDoor", 1, null, IsCurrentDrawing: null));
        Assert.Contains("—", unknown); // 不可比不猜
    }

    [AvaloniaFact]
    public async Task RefreshAfterEdit_ReloadsDeepReadPanel()
    {
        using var f = new Fixture();
        string currentHash = f.Vm.Session.ComputeHash();
        f.WriteOverlay(blockDefHash: currentHash); // 初始行哈希与当前图纸一致
        MainWindow window = Show(f);
        try
        {
            window.LeftTabs!.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, window.DeepReadNativeList!.ItemCount);
            BlockDefRow before = Assert.Single(window.DeepRead!.BlockDefRows);
            Assert.True(before.IsCurrentDrawing);

            // 编辑前后 overlay 内容变化（模拟他处重扫落盘）：RefreshAfterEdit 须重读。
            f.WriteOverlay(blockDefHash: currentHash, extraNativeRows: 1);
            await AddTextViaEditAsync(window);

            Assert.Equal(2, window.DeepReadNativeList.ItemCount); // 行变化可见
            BlockDefRow after = Assert.Single(window.DeepRead.BlockDefRows);
            Assert.False(after.IsCurrentDrawing); // 编辑改图 → 来源哈希不一致
            Assert.Contains("来源哈希不一致（图纸可能已编辑或来自他图）",
                MainWindow.FormatBlockDefRow(after));
        }
        finally { window.Close(); }
    }
}
