using System.Diagnostics;
using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// fix/acad-rot-pid-filter：ROT 附着按**本方进程 PID** 过滤。
/// 根因（真机实证 3/4 全量失败）：类间交接时，前一真机类刚 Quit 的 AutoCAD 实例注册可能仍在
/// ROT 里——<c>GetActiveObject</c> 取"当前活动对象"会拿到该已退出/他方实例 → 归属校验伪失败
/// "ROT 附着实例非本方启动（归属校验失败）"。改为枚举 ROT、按"实例窗口所属 PID == 本方 PID
/// 且进程存活"过滤后附着。本文件只用缝（无 COM、无真机、不拉起 acad.exe）。
/// 失败用例 maxAttempts:2 → 2s 内必然终局（生产轮询常量 1000ms，确定性）。
/// </summary>
public class AcadAttachRotTests : IDisposable
{
    private readonly Func<string, int, object?> _origRot = AcadAttach.RotAttachByPid;
    private readonly Func<object, int> _origHwnd = AcadAttach.HwndReader;
    private readonly Func<int, int> _origWindowPid = AcadAttach.WindowProcessId;
    private readonly Func<int, bool> _origAlive = AcadAttach.PidAlive;

    public void Dispose()
    {
        AcadAttach.RotAttachByPid = _origRot;
        AcadAttach.HwndReader = _origHwnd;
        AcadAttach.WindowProcessId = _origWindowPid;
        AcadAttach.PidAlive = _origAlive;
    }

    private const int OurPid = 4242;
    private const int ForeignPid = 1111;

    // 主场景：ROT 里同时有他方 PID 的实例与本方实例——按本方 PID 请求只认本方实例
    // （他方实例即"交接残留/用户自开实例"的那一类，绝不能被附着）。
    [Fact]
    public void AttachByPid_ForeignInstanceAlsoRunning_PicksOwnInstanceOnly()
    {
        var oursApp = new object();
        var foreignApp = new object();
        var askedPids = new List<int>();
        AcadAttach.RotAttachByPid = (progId, pid) =>
        {
            askedPids.Add(pid);
            // 同一 ProgID 下他方实例也注册着：按 PID 请求时只能拿到属于该 PID 的那个。
            return pid == OurPid ? oursApp : foreignApp;
        };

        var app = AcadAttach.AttachByPid("AutoCAD.Application", OurPid, maxAttempts: 1);

        Assert.Same(oursApp, app);
        Assert.Equal(new[] { OurPid }, askedPids);
    }

    // 只有他方/已退出实例注册（过滤器恒无本方候选）→ 有界轮询耗尽→响亮拒绝（CLI 据此降级）。
    [Fact]
    public void AttachByPid_OnlyForeignRegistration_ThrowsAfterExhaustion()
    {
        int calls = 0;
        AcadAttach.RotAttachByPid = (_, _) =>
        {
            calls++;
            return null;   // 过滤后无本方候选（他方/已退出实例不算）
        };

        var ex = Assert.Throws<CadRuntimeException>(
            () => AcadAttach.AttachByPid("AutoCAD.Application", OurPid, maxAttempts: 2));

        Assert.Contains("未见本方 AutoCAD 实例注册", ex.Message);
        Assert.Contains(OurPid.ToString(), ex.Message);
        Assert.Equal(2, calls);
    }

    // 绑定异常被跳过（ROT 枚举内部吞掉、该轮返回 null）→ 下一轮命中本方实例：
    // 证明"单次绑定失败"不会终止整个附着轮询。
    [Fact]
    public void AttachByPid_BindFailureThenLaterMatch_PicksLaterCandidate()
    {
        var oursApp = new object();
        int calls = 0;
        AcadAttach.RotAttachByPid = (_, pid) =>
        {
            calls++;
            if (calls == 1) return null;            // 该轮 BindToObject 全部失败（模型：被跳过）
            return pid == OurPid ? oursApp : null;
        };

        var app = AcadAttach.AttachByPid("AutoCAD.Application", OurPid, maxAttempts: 3);

        Assert.Same(oursApp, app);
        Assert.Equal(2, calls);
    }

    // I2（评审 follow-up 1）：本方进程已退出 → 立即中止，不空转满轮询上界；消息精确钉住。
    // abortIf 首轮即真 ⇒ 一次 ROT 枚举都不该做（连 1s 节拍都不该等），故 maxAttempts 取大值。
    [Fact]
    public void AttachByPid_AbortIfTrue_ThrowsExitedMessageOnFirstPoll()
    {
        int rotCalls = 0;
        AcadAttach.RotAttachByPid = (_, _) =>
        {
            rotCalls++;
            return null;
        };
        var sw = Stopwatch.StartNew();

        var ex = Assert.Throws<CadRuntimeException>(() => AcadAttach.AttachByPid(
            "AutoCAD.Application", OurPid, maxAttempts: 1000, abortIf: () => true));

        sw.Stop();
        Assert.Equal("本方 AutoCAD 进程已退出（附着中止）", ex.Message);
        Assert.Equal(0, rotCalls);                             // 首轮即中止：未做无意义的 ROT 枚举
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));     // 不等 1s 节拍（更不空转 1000s 上界）
    }

    // 进程已死 → 候选被排除（ROT 残留的退出实例注册不得被认成本方实例）。
    [Fact]
    public void OwnedByPid_DeadProcess_Excluded()
    {
        AcadAttach.HwndReader = _ => 7777;
        AcadAttach.WindowProcessId = _ => OurPid;
        AcadAttach.PidAlive = _ => false;

        Assert.False(AcadAttach.OwnedByPid(new object(), OurPid));

        AcadAttach.PidAlive = _ => true;
        Assert.True(AcadAttach.OwnedByPid(new object(), OurPid));   // 对照：存活即认
    }

    // 不可判定 / 他方窗口 → 一律排除（错认会把 Quit 打到他方实例上）。
    [Fact]
    public void OwnedByPid_ForeignWindowOrUnreadableHwnd_Excluded()
    {
        AcadAttach.PidAlive = _ => true;

        AcadAttach.HwndReader = _ => 7777;
        AcadAttach.WindowProcessId = _ => ForeignPid;
        Assert.False(AcadAttach.OwnedByPid(new object(), OurPid));   // 窗口属他方 PID

        AcadAttach.WindowProcessId = _ => OurPid;
        AcadAttach.HwndReader = _ => throw new InvalidOperationException("HWND 不可读");
        Assert.False(AcadAttach.OwnedByPid(new object(), OurPid));   // 读不出窗口

        AcadAttach.HwndReader = _ => 0;
        Assert.False(AcadAttach.OwnedByPid(new object(), OurPid));   // 无窗口句柄

        AcadAttach.HwndReader = _ => 7777;
        Assert.False(AcadAttach.OwnedByPid(null, OurPid));           // 无候选
        Assert.False(AcadAttach.OwnedByPid(new object(), 0));        // 无目标 PID
    }

    // 默认存活判定缝（无 COM、无 acad.exe）：非法 PID=false，宿主自身 PID=true。
    [Fact]
    public void DefaultPidAlive_InvalidAndLivePids()
    {
        Assert.False(AcadAttach.PidAlive(0));
        Assert.False(AcadAttach.PidAlive(-1));
        using var self = Process.GetCurrentProcess();
        Assert.True(AcadAttach.PidAlive(self.Id));
    }

    // 缝注入后 Dispose 必须还原默认实现（跨测试类不串味）。
    [Fact]
    public void Seams_RestoredToDefaults_AfterDispose()
    {
        AcadAttach.HwndReader = _ => 1;
        AcadAttach.PidAlive = _ => false;
        AcadAttach.WindowProcessId = _ => 1;
        AcadAttach.RotAttachByPid = (_, _) => null;

        Dispose();

        Assert.Same(_origHwnd, AcadAttach.HwndReader);
        Assert.Same(_origAlive, AcadAttach.PidAlive);
        Assert.Same(_origWindowPid, AcadAttach.WindowProcessId);
        Assert.Same(_origRot, AcadAttach.RotAttachByPid);
    }
}

/// <summary>
/// EnsureAppTArch 接线（fix/acad-rot-pid-filter）：本方 PID 可判定 → 走 <see cref="AcadAttach.AttachByPid"/>
/// 过滤路径；PID 不可判定（fake/未启动进程）→ 回退既有 <see cref="AcadAttach.AttachOnly"/> 路径
/// （旧测试缝口径不变）。归属校验 + 有界重试保留为纵深防御。全部缝注入，无 COM、无真机。
/// </summary>
public class EnsureAppTArchPidFilterTests : IDisposable
{
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<Process, int> _origProcessId = AcadRuntime.ProcessIdReader;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string, int, object?> _origRot = AcadAttach.RotAttachByPid;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly Func<int, AcadWindows.TopLevelWindow[]> _origWindows = AcadWindows.WindowsOfProcess;
    private readonly Action<IntPtr> _origClose = AcadWindows.CloseWindow;
    private readonly Func<dynamic, Process, bool> _origOwnership = AcadRuntime.OwnershipMatches;
    private readonly Func<Process, bool> _origCleanup = AcadRuntime.TarchProcessCleanup;
    private readonly Func<Process, bool> _origProcExited = AcadRuntime.ProcExitedReader;
    private readonly int _origPidAttachAttempts = AcadRuntime.PidAttachAttempts;
    private readonly int _origPidFallbackAttempts = AcadRuntime.PidFallbackAttempts;
    private readonly int _origAttempts = AcadRuntime.StartupRetryAttempts;
    private readonly TimeSpan _origDelay = AcadRuntime.StartupRetryDelay;
    private readonly TimeSpan? _origOwnershipWindow = AcadRuntime.OwnershipRetryWindowOverride;

    public EnsureAppTArchPidFilterTests()
    {
        // 守卫缝：本机 acad 进程存在与否不影响单测；关窗缝全 fake（绝不枚举/投递真实窗口）。
        VendorGuard.ProcessNames = () => Array.Empty<string>();
        AcadWindows.WindowsOfProcess = _ => Array.Empty<AcadWindows.TopLevelWindow>();
        AcadWindows.CloseWindow = _ => { };
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
    }

    public void Dispose()
    {
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadRuntime.ProcessIdReader = _origProcessId;
        AcadAttach.ActiveObjectResolver = _origActive;
        AcadAttach.RotAttachByPid = _origRot;
        VendorGuard.ProcessNames = _origNames;
        AcadWindows.WindowsOfProcess = _origWindows;
        AcadWindows.CloseWindow = _origClose;
        AcadRuntime.OwnershipMatches = _origOwnership;
        AcadRuntime.TarchProcessCleanup = _origCleanup;
        AcadRuntime.ProcExitedReader = _origProcExited;
        AcadRuntime.PidAttachAttempts = _origPidAttachAttempts;
        AcadRuntime.PidFallbackAttempts = _origPidFallbackAttempts;
        AcadRuntime.StartupRetryAttempts = _origAttempts;
        AcadRuntime.StartupRetryDelay = _origDelay;
        AcadRuntime.OwnershipRetryWindowOverride = _origOwnershipWindow;
    }

    private const int LaunchedPid = 4242;

    // 接线主场景：ProcessLauncher 给的是 fake Process（真实 PID 不可得）→ ProcessIdReader 缝
    // 注入 4242 → EnsureAppTArch 必须以 4242 走 AttachByPid，且绝不再触 GetActiveObject。
    [Fact]
    public void EnsureAppTArch_LaunchedPidKnown_AttachesByPid()
    {
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadRuntime.ProcessIdReader = _ => LaunchedPid;
        var fake = new PidFilterGateApp();
        var askedPids = new List<int>();
        AcadAttach.ActiveObjectResolver = _ => throw new InvalidOperationException("PID 路径不得触 GetActiveObject");
        AcadAttach.RotAttachByPid = (progId, pid) =>
        {
            Assert.Equal("AutoCAD.Application", progId);   // 与 VendorProfiles.Acad 同源
            askedPids.Add(pid);
            return pid == LaunchedPid ? fake : null;
        };
        using var rt = new AcadRuntime();

        var app = (object)rt.EnsureAppTArch("TElec20V5");

        Assert.Same(fake, app);
        Assert.Equal(new[] { LaunchedPid }, askedPids);   // 附着按本方 PID 过滤
    }

    // 接线对照：PID 不可判定（0）→ 回退既有 AttachOnly 路径（真实 PID 拿不到的测试/未启动进程）。
    [Fact]
    public void EnsureAppTArch_LaunchedPidUnknown_FallsBackToActiveObjectPath()
    {
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadRuntime.ProcessIdReader = _ => 0;
        bool rotCalled = false;
        AcadAttach.RotAttachByPid = (_, _) => { rotCalled = true; return null; };
        var fake = new PidFilterGateApp();
        AcadAttach.ActiveObjectResolver = _ => fake;
        using var rt = new AcadRuntime();

        var app = (object)rt.EnsureAppTArch("TElec20V5");

        Assert.Same(fake, app);
        Assert.False(rotCalled);   // 零 PID 不走 ROT 过滤
    }

    // 纵深防御：PID 过滤后归属校验仍保留——不符→有界重试→窗口耗尽仍不符→原响亮拒绝，
    // 且绝不 Quit 附着实例、只清理本方启动进程。
    [Fact]
    public void EnsureAppTArch_PidPathOwnershipMismatch_StillRejectsAndCleansOwnProcess()
    {
        AcadRuntime.OwnershipRetryWindowOverride = TimeSpan.FromMilliseconds(120);
        Process? launched = null;
        AcadRuntime.ProcessLauncher = _ => launched = new Process();
        AcadRuntime.ProcessIdReader = _ => LaunchedPid;
        Process? cleaned = null;
        AcadRuntime.TarchProcessCleanup = p => { cleaned = p; return true; };
        var fake = new PidFilterGateApp();
        AcadAttach.RotAttachByPid = (_, _) => fake;
        AcadRuntime.OwnershipMatches = (_, __) => false;   // 注入：归属恒不符
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });

        Assert.Contains("归属校验失败", ex.Message);
        Assert.Equal(0, fake.QuitCalls);      // 绝不 Quit 附着实例
        Assert.Same(launched, cleaned);       // 只清理本方启动进程
    }

    // I3（评审 follow-up 1）：ROT 过滤**耗尽**（非进程退出）→ 单次回退既有 GetActiveObject 通道，
    // 回退命中后照常过归属校验并继续（构建可继续，不因一次过滤耗尽整体失败）。
    [Fact]
    public void EnsureAppTArch_RotFilterExhausted_FallsBackToActiveObjectOnce()
    {
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadRuntime.ProcessIdReader = _ => LaunchedPid;
        AcadRuntime.PidAttachAttempts = 1;      // 一轮即耗尽（单测不真轮询 180s）
        AcadRuntime.PidFallbackAttempts = 1;
        int rotCalls = 0;
        AcadAttach.RotAttachByPid = (_, _) => { rotCalls++; return null; };   // 过滤恒无本方候选
        var fake = new PidFilterGateApp();
        int activeCalls = 0;
        AcadAttach.ActiveObjectResolver = _ => { activeCalls++; return fake; };
        using var rt = new AcadRuntime();

        var app = (object)rt.EnsureAppTArch("TElec20V5");

        Assert.Same(fake, app);       // 回退命中→过归属校验→返回（构建继续）
        Assert.Equal(1, rotCalls);    // ROT 过滤只耗尽一次
        Assert.Equal(1, activeCalls); // 回退只做一次
    }

    // I3：回退也失败 → 原错误链路上抛（保留"未见本方 AutoCAD 实例注册"根因文案，不吞诊断）。
    [Fact]
    public void EnsureAppTArch_RotFilterExhaustedAndFallbackEmpty_Throws()
    {
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadRuntime.ProcessIdReader = _ => LaunchedPid;
        AcadRuntime.PidAttachAttempts = 1;
        AcadRuntime.PidFallbackAttempts = 1;
        AcadAttach.RotAttachByPid = (_, _) => null;
        AcadAttach.ActiveObjectResolver = _ => null;
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });

        Assert.Contains("未见本方 AutoCAD 实例注册", ex.Message);   // 原根因
        Assert.Contains("ROT 附着超时", ex.Message);               // 回退失败一并披露
    }

    // 评审 follow-up 2（守卫）：回退**全局至多一次**——归属不符会再进一轮附着，若闸门每轮重置，
    // 回退就会在嵌套重试里反复追加（反复持有他方实例）。场景：首轮过滤"命中"（对象属他方，
    // AttachByPid 如实返回）→ 归属不符 → 重试轮过滤耗尽 → 回退一次（仍是他方实例）→ 归属不符 →
    // 窗口耗尽 → 响亮拒绝。钉住：回退计数恒 1、轮次有界、绝不 Quit 他方实例。
    [Fact]
    public void EnsureAppTArch_OwnershipMismatchAcrossRetry_FallbackAtMostOnce()
    {
        AcadRuntime.OwnershipRetryWindowOverride = TimeSpan.FromMilliseconds(120);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadRuntime.ProcessIdReader = _ => LaunchedPid;
        AcadRuntime.PidAttachAttempts = 1;      // 每轮只枚举一次（单测不真轮询 180s）
        AcadRuntime.PidFallbackAttempts = 1;
        var foreign = new PidFilterGateApp();
        int rotCalls = 0;
        // 首轮"命中"他方实例（AttachByPid 因此如实返回对象，不耗尽、不回退）；其后过滤恒无候选。
        AcadAttach.RotAttachByPid = (_, _) => { rotCalls++; return rotCalls == 1 ? foreign : null; };
        int activeCalls = 0;
        AcadAttach.ActiveObjectResolver = _ => { activeCalls++; return foreign; };
        AcadRuntime.OwnershipMatches = (_, __) => false;   // 注入：归属恒不符（附到的是他方实例）
        AcadRuntime.TarchProcessCleanup = _ => true;
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });

        Assert.Contains("归属校验失败", ex.Message);   // 归属不符响亮拒绝（不静默放行他方实例）
        Assert.Equal(1, activeCalls);                  // 回退全程只做一次（嵌套重试不重复回退）
        Assert.Equal(2, rotCalls);                     // 有限轮次：首轮 + 一轮重试即终局（不追加循环）
        Assert.Equal(0, foreign.QuitCalls);            // 绝不 Quit 他方实例
    }

    // I2：本方进程已退出（fake Process 的 HasExited 经缝注入）→ 附着首轮即中止，
    // 不空转轮询、不回退 GetActiveObject，失败清理仍只处置本方启动进程。
    [Fact]
    public void EnsureAppTArch_LaunchedProcessExited_AbortsAttachOnFirstPoll()
    {
        Process? launched = null;
        AcadRuntime.ProcessLauncher = _ => launched = new Process();
        AcadRuntime.ProcessIdReader = _ => LaunchedPid;
        AcadRuntime.ProcExitedReader = _ => true;   // 真实 Process 上 HasExited 无法置位 → 注入
        int rotCalls = 0;
        AcadAttach.RotAttachByPid = (_, _) => { rotCalls++; return null; };
        AcadAttach.ActiveObjectResolver = _ =>
            throw new InvalidOperationException("本方进程已退出：不得回退 GetActiveObject");
        Process? cleaned = null;
        AcadRuntime.TarchProcessCleanup = p => { cleaned = p; return true; };
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });

        Assert.Contains("已退出", ex.Message);
        Assert.Equal(0, rotCalls);            // 首轮即中止：未做 ROT 枚举
        Assert.Same(launched, cleaned);       // 失败清理仍只针对本方进程
    }
}

/// <summary>假天正实例：满足 Gate1（Version 非空 + 1 文档）。**必须 public**——
/// dynamic 绑定按调用方（产品程序集）判可见性。</summary>
public sealed class PidFilterGateApp
{
    public string Version => "19.1s (LMS Tech)";
    public PidFilterGateDocs Documents => new();
    public bool Visible { get; set; }
    public int QuitCalls { get; private set; }
    public void Quit() => QuitCalls++;
}

public sealed class PidFilterGateDocs
{
    public int Count => 1;
}
