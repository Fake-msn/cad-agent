using System.Diagnostics;
using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// Fix round 3：本方进程**信息性**模态框自动关窗（fake 缝，NO 真窗口/NO 真进程启动）。
/// 真机实证：/p 每次启动弹"不支持硬件加速"WPF 提示框——未关时 COM Gate1 间歇失败、
/// Dispose 的 Quit 留下本方 acad.exe 孤儿；测试无人手点，只能按标题关窗。
/// 钉住三条边界：① 只对标题命中的窗口投递 WM_CLOSE；② 不命中/无 needle/序列不一致 → 0（不动窗口）；
/// ③ EnsureAppTArch 在 Gate1 期间确实跑关窗节拍（缝可观测）。
/// </summary>
public class AcadDialogDismissTests : IDisposable
{
    private readonly Func<int, AcadWindows.TopLevelWindow[]> _origWindows = AcadWindows.WindowsOfProcess;
    private readonly Func<int, string[]> _origTitles = AcadWindows.WindowTitlesOfProcess;
    private readonly Action<IntPtr> _origClose = AcadWindows.CloseWindow;
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string, int, object?> _origRot = AcadAttach.RotAttachByPid;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly int _origAttempts = AcadRuntime.StartupRetryAttempts;
    private readonly TimeSpan _origDelay = AcadRuntime.StartupRetryDelay;
    private readonly TimeSpan _origDismissInterval = AcadRuntime.DialogDismissInterval;

    public AcadDialogDismissTests()
    {
        // 守卫缝：本机 acad 进程存在与否不影响单测。
        VendorGuard.ProcessNames = () => Array.Empty<string>();
        // 默认关窗缝全部换成记录器：单测绝不投递真实 WM_CLOSE、绝不枚举真实窗口。
        AcadWindows.WindowsOfProcess = _ => Array.Empty<AcadWindows.TopLevelWindow>();
        AcadWindows.CloseWindow = _ => { };
    }

    public void Dispose()
    {
        AcadWindows.WindowsOfProcess = _origWindows;
        AcadWindows.WindowTitlesOfProcess = _origTitles;
        AcadWindows.CloseWindow = _origClose;
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadAttach.ActiveObjectResolver = _origActive;
        AcadAttach.RotAttachByPid = _origRot;
        VendorGuard.ProcessNames = _origNames;
        AcadRuntime.StartupRetryAttempts = _origAttempts;
        AcadRuntime.StartupRetryDelay = _origDelay;
        AcadRuntime.DialogDismissInterval = _origDismissInterval;
    }

    // 注入窗口快照：标题与句柄同源同序（默认标题缝由 WindowsOfProcess 派生）。
    private static void FakeWindows(params (int Hwnd, string Title)[] windows)
    {
        var list = windows
            .Select(w => new AcadWindows.TopLevelWindow(new IntPtr(w.Hwnd), w.Title))
            .ToArray();
        AcadWindows.WindowsOfProcess = _ => list;
    }

    /// <summary>假 app：Gate1 就绪（Version 非空 + 1 个文档）与退出 Quit。</summary>
    public sealed class ReadyGateApp
    {
        public string Version => "19.1s (LMS Tech)";
        public ReadyGateDocuments Documents => new();
        public bool Visible { get; set; }
        public void Quit() { }
    }

    public sealed class ReadyGateDocuments
    {
        public int Count => 1;
    }

    [Fact]
    public void DismissDialogs_TitleMatch_ClosesOnlyMatchedWindow()
    {
        FakeWindows((101, "AutoCAD 2014 - 不支持硬件加速"), (202, "AutoCAD 2014 - Drawing1.dwg"));
        var closed = new List<IntPtr>();
        AcadWindows.CloseWindow = h => closed.Add(h);

        int n = AcadWindows.DismissDialogs(4242, new[] { "不支持硬件加速" });

        Assert.Equal(1, n);
        Assert.Equal(new[] { new IntPtr(101) }, closed);   // 只关命中标题的那个窗口
    }

    [Fact]
    public void DismissDialogs_NoMatch_ReturnsZeroAndClosesNothing()
    {
        FakeWindows((101, "AutoCAD 2014 - Drawing1.dwg"), (202, "安全 - 可执行文件"));
        var closed = new List<IntPtr>();
        AcadWindows.CloseWindow = h => closed.Add(h);

        Assert.Equal(0, AcadWindows.DismissDialogs(4242, new[] { "不支持硬件加速" }));
        Assert.Empty(closed);   // 安全确认框等其他对话框一律不碰
    }

    [Fact]
    public void DismissDialogs_NeedleAbsentOrPidInvalid_ReturnsZero()
    {
        FakeWindows((101, "AutoCAD 2014 - 不支持硬件加速"));
        var closed = new List<IntPtr>();
        AcadWindows.CloseWindow = h => closed.Add(h);

        Assert.Equal(0, AcadWindows.DismissDialogs(4242, Array.Empty<string>()));   // 无 needle
        Assert.Equal(0, AcadWindows.DismissDialogs(4242, null!));                   // needle 为 null
        Assert.Equal(0, AcadWindows.DismissDialogs(0, new[] { "不支持硬件加速" })); // 无 PID（全桌面兜底禁用）
        Assert.Empty(closed);
    }

    [Fact]
    public void DismissDialogs_TitlesAndHandlesDisagree_DoesNotGuess()
    {
        FakeWindows((101, "窗口一"));
        // 标题序列与句柄序列长度不符（两次枚举不一致）：关闭不可撤销，宁可少关也不误关。
        AcadWindows.WindowTitlesOfProcess = _ => new[] { "不支持硬件加速", "另一个窗口" };
        var closed = new List<IntPtr>();
        AcadWindows.CloseWindow = h => closed.Add(h);

        Assert.Equal(0, AcadWindows.DismissDialogs(4242, new[] { "不支持硬件加速" }));
        Assert.Empty(closed);
    }

    [Fact]
    public void TitleMatchesAny_SubstringCaseInsensitive()
    {
        Assert.True(AcadWindows.TitleMatchesAny("AutoCAD - 不支持硬件加速提示", new[] { "不支持硬件加速" }));
        Assert.True(AcadWindows.TitleMatchesAny("AUTOCAD hardware notice", new[] { "hardware" }));
        Assert.False(AcadWindows.TitleMatchesAny("AutoCAD - Drawing1.dwg", new[] { "不支持硬件加速" }));
        Assert.False(AcadWindows.TitleMatchesAny(null, new[] { "不支持硬件加速" }));
        Assert.False(AcadWindows.TitleMatchesAny("任意标题", Array.Empty<string>()));
    }

    [Fact]
    public void EnsureAppTArch_Gate1_RunsDismissalLoopAgainstOwnProcessOnly()
    {
        // 真进程句柄（本测试宿主）以取得有效 PID；不上抛 acad.exe、不触真实窗口（缝全段注入）。
        // fix/acad-rot-pid-filter：宿主 PID 可判定 → EnsureAppTArch 走"按 PID 过滤"附着通道，
        // 故此处注入 ROT 过滤缝（真 ROT 里没有本宿主 PID 的 AutoCAD 实例，不得触真 COM/真轮询）。
        using var host = Process.GetCurrentProcess();
        AcadRuntime.ProcessLauncher = _ => host;
        var attachedPids = new List<int>();
        AcadAttach.RotAttachByPid = (_, pid) =>
        {
            attachedPids.Add(pid);
            return pid == host.Id ? new ReadyGateApp() : null;
        };
        AcadAttach.ActiveObjectResolver = _ => throw new InvalidOperationException("PID 可判定时不得走 GetActiveObject");
        var observedPids = new List<int>();
        AcadWindows.WindowTitlesOfProcess = pid => { observedPids.Add(pid); return Array.Empty<string>(); };
        AcadWindows.WindowsOfProcess = _ => Array.Empty<AcadWindows.TopLevelWindow>();

        using var rt = new AcadRuntime();
        var app = (object)rt.EnsureAppTArch("TElec20V5");

        Assert.IsType<ReadyGateApp>(app);
        Assert.Equal(new[] { host.Id }, attachedPids);        // 附着按本方 PID 过滤
        Assert.NotEmpty(observedPids);                       // 关窗节拍确实跑过（首发同步调用）
        Assert.All(observedPids, pid => Assert.Equal(host.Id, pid));   // 只针对本方启动的 PID

        // Dispose 的 Quit 之前补关一次（真机实证：模态框未关时 Quit 留下本方孤儿进程）。
        int beforeQuit = observedPids.Count;
        rt.Dispose();
        Assert.True(observedPids.Count > beforeQuit);
        Assert.All(observedPids, pid => Assert.Equal(host.Id, pid));
    }
}
