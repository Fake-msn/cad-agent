using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AcadAttach 单元测试（无真机/无 COM）：ActiveObjectResolver 缝注入。
/// 附着轮询 delay=1000ms 是生产常量；失败用例 maxAttempts:2 → 2s 内必然终局（确定性）。
/// </summary>
public class AcadAttachTests : IDisposable
{
    private readonly Func<string, object?> _origResolver = AcadAttach.ActiveObjectResolver;

    public void Dispose() => AcadAttach.ActiveObjectResolver = _origResolver;

    [Fact]
    public void Attach_WhenResolverReturnsNull_ThrowsCadRuntime()
    {
        AcadAttach.ActiveObjectResolver = _ => null;
        var ex = Assert.Throws<CadRuntimeException>(
            () => AcadAttach.AttachOnly("AutoCAD.Application", maxAttempts: 2));
        Assert.Contains("ROT 附着超时", ex.Message);
    }

    [Fact]
    public void Attach_WhenResolverSucceeds_ReturnsApp()
    {
        var fake = new object();
        AcadAttach.ActiveObjectResolver = _ => fake;
        Assert.Same(fake, AcadAttach.AttachOnly("AutoCAD.Application", maxAttempts: 2));
    }

    [Fact]
    public void Attach_RetryAfterFailure_Succeeds()
    {
        int calls = 0;
        var fake = new object();
        AcadAttach.ActiveObjectResolver = _ => (++calls >= 3) ? fake : null;
        Assert.Same(fake, AcadAttach.AttachOnly("AutoCAD.Application", maxAttempts: 5));
        Assert.Equal(3, calls);
    }
}

/// <summary>
/// EnsureAppTArch 单元测试（无真机/无 COM）：VendorGuard.ProcessNames / ProcessLauncher /
/// ActiveObjectResolver 三缝全注入——不启动真实 acad.exe、不触 ROT/COM。
/// Gate1 用例把 StartupRetryAttempts/StartupRetryDelay 调小保持确定性。
/// </summary>
public class EnsureAppTArchTests : IDisposable
{
    private readonly Func<string, Type?> _origResolver = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _origCreate = AcadRuntime.CreateComInstance;
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly Func<Process, bool> _origCleanup = AcadRuntime.TarchProcessCleanup;
    private readonly Func<dynamic, Process, bool> _origOwnership = AcadRuntime.OwnershipMatches;
    private readonly int _origAttempts = AcadRuntime.StartupRetryAttempts;
    private readonly TimeSpan _origDelay = AcadRuntime.StartupRetryDelay;

    public EnsureAppTArchTests()
    {
        // 守卫缝：本机 acad 进程存在与否不影响单测（真机守卫语义由 VendorGuard 自测覆盖）。
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.ProgIdResolver = _origResolver;
        AcadRuntime.CreateComInstance = _origCreate;
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadAttach.ActiveObjectResolver = _origActive;
        VendorGuard.ProcessNames = _origNames;
        AcadRuntime.TarchProcessCleanup = _origCleanup;
        AcadRuntime.OwnershipMatches = _origOwnership;
        AcadRuntime.StartupRetryAttempts = _origAttempts;
        AcadRuntime.StartupRetryDelay = _origDelay;
    }

    /// <summary>假 app：Gate1 读 Version + Documents.Count（两者可配）；Visible 供复用用例走 GetInfo；
    /// QuitCalls 供失败清理用例断言"优雅 Quit 优先"。</summary>
    public sealed class GateApp
    {
        private readonly string _version;
        private readonly int _docCount;
        public GateApp(string version, int docCount) { _version = version; _docCount = docCount; }
        public string Version => _version;
        public GateDocs Documents => new(_docCount);
        public bool Visible { get; set; }
        public int QuitCalls { get; private set; }
        public void Quit() => QuitCalls++;
    }

    public sealed class GateDocs
    {
        private readonly int _count;
        public GateDocs(int count) => _count = count;
        public int Count => _count;
    }

    /// <summary>假 app：Version 恒忙拒绝（模拟附着后启动窗口未就绪）。</summary>
    public sealed class BusyGateApp
    {
        public string Version => throw new COMException("起不来 (0x80010001)", unchecked((int)0x80010001));
        public GateDocs Documents => new(0);
        public void Quit() { }
    }

    /// <summary>假 app：读参/Quit 全被忙拒绝——失败清理的优雅 Quit 也失败，走进程兜底 Kill。</summary>
    public sealed class BusyQuitGateApp
    {
        public string Version => throw new COMException("起不来 (0x80010001)", unchecked((int)0x80010001));
        public GateDocs Documents => new(0);
        public void Quit() =>
            throw new COMException("退不出 (0x80010001)", unchecked((int)0x80010001));
    }

    [Fact]
    public void EnsureAppTArch_LauncherNull_Throws()
    {
        AcadRuntime.ProcessLauncher = _ => null;
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Contains("天正实例启动失败", ex.Message);
    }

    [Fact]
    public void EnsureAppTArch_LaunchesWithProfile_AttachesAndReturnsApp()
    {
        string? launchedProfile = null;
        AcadRuntime.ProcessLauncher = p => { launchedProfile = p; return new Process(); };
        var fake = new GateApp("19.1s (LMS Tech)", 1);
        AcadAttach.ActiveObjectResolver = _ => fake;
        using var rt = new AcadRuntime();
        var app = (object)rt.EnsureAppTArch("TElec20V5");
        Assert.Same(fake, app);
        Assert.Equal("TElec20V5", launchedProfile); // /p 参数即天正 profile 名
    }

    [Fact]
    public void EnsureAppTArch_Gate1NoDocument_ThrowsTimeout()
    {
        AcadRuntime.StartupRetryAttempts = 3;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => new GateApp("19.1s (LMS Tech)", 0); // 无文档=未就绪
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Contains("Gate1 超时", ex.Message);
    }

    [Fact]
    public void EnsureAppTArch_Gate1BusyExhaustion_ThrowsTimeoutWithoutStacking()
    {
        // 忙拒绝由 RetryBusy 内部有界重试；耗尽即终局 Gate1（不叠加外层 60 次放大挂死）。
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => new BusyGateApp();
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Contains("Gate1 超时", ex.Message);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public void EnsureAppTArch_ExistingApp_ReusedNoSecondLaunch()
    {
        int launches = 0;
        AcadRuntime.ProcessLauncher = _ => { launches++; return new Process(); };
        // 复用路径若未早退会触真 ROT 查询——注入抛出使回归立即失败而非挂 180s。
        AcadAttach.ActiveObjectResolver = _ => throw new InvalidOperationException("不应触 ROT 附着");
        var fake = new GateApp("19.1s (LMS Tech)", 1);
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => fake;
        using var rt = new AcadRuntime();
        rt.GetInfo(); // 既有 COM 激活路径使 _app 就位
        var app = (object)rt.EnsureAppTArch("TElec20V5");
        Assert.Same(fake, app); // 互斥复用：不得重启/再附着
        Assert.Equal(0, launches);
    }

    // 复审修复波（spec §5「启动失败不残留进程」）：已附着但优雅 Quit 也被忙拒绝 → 对本次
    // 启动的 Process 兜底清理；原 CadRuntimeException 消息原样上抛；_app 置空使再次
    // EnsureAppTArch 真正重启（不得因残留缓存早退返回已失效对象）。
    // 清理缝注入：不 Start 真进程（默认缝在未 Start 的 fake Process 上抛异常并吞掉）。
    [Fact]
    public void EnsureAppTArch_Gate1Timeout_CleansUpLaunchedProcess_AndOriginalMessageSurvives()
    {
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        int launches = 0;
        Process? launched = null;
        AcadRuntime.ProcessLauncher = _ => { launches++; return launched = new Process(); };
        Process? cleaned = null;
        AcadRuntime.TarchProcessCleanup = p => { cleaned = p; return true; };
        // 忙拒绝穿透 Gate1，且优雅 Quit 同样被拒→退出降级为进程兜底。
        AcadAttach.ActiveObjectResolver = _ => new BusyQuitGateApp();
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Contains("Gate1 超时", ex.Message); // 清理不得掩盖原异常
        Assert.Same(launched, cleaned);            // 清理对象=本方启动的 Process

        // _app 已置空：第二次调用重新启动并再次响亮失败（而非早退返回残留对象）。
        Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Equal(2, launches);
    }

    // 复审修复波：附着阶段抛非超时内部错误（如 P/Invoke 缺失）→ 仍清理本方进程，
    // 原异常原样上抛（清理不得包装/掩盖根因）。
    [Fact]
    public void EnsureAppTArch_AttachThrows_CleansUpLaunchedProcess_AndRethrowsOriginal()
    {
        Process? launched = null;
        AcadRuntime.ProcessLauncher = _ => launched = new Process();
        Process? cleaned = null;
        AcadRuntime.TarchProcessCleanup = p => { cleaned = p; return true; };
        AcadAttach.ActiveObjectResolver = _ => throw new TypeLoadException("EntryPointNotFound");
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<TypeLoadException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Equal("EntryPointNotFound", ex.Message);
        Assert.Same(launched, cleaned);
    }

    // 复审修复波：已附着（_app 由 ROT 赋值）→ 优雅 Quit 优先；Quit 成功不再动进程句柄。
    [Fact]
    public void EnsureAppTArch_Gate1Timeout_AttachedApp_QuitsGracefullyWithoutProcessKill()
    {
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        AcadRuntime.ProcessLauncher = _ => new Process();
        bool cleanupCalled = false;
        AcadRuntime.TarchProcessCleanup = _ => { cleanupCalled = true; return true; };
        var fake = new GateApp("19.1s (LMS Tech)", 0); // 附着成功但未就绪
        AcadAttach.ActiveObjectResolver = _ => fake;
        using var rt = new AcadRuntime();

        Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Equal(1, fake.QuitCalls);   // 优雅优先
        Assert.False(cleanupCalled);       // Quit 成功→不升级为 Kill
    }

    // 复审修复波（finding 2）：launcher 异常不得被吞——包装消息须含 Win32 真因，inner 保留。
    [Fact]
    public void EnsureAppTArch_LauncherThrows_WrapsWin32ReasonNotSwallowed()
    {
        AcadRuntime.ProcessLauncher = _ => throw new Win32Exception("找不到 acad.exe");
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Contains("天正实例启动失败", ex.Message);
        Assert.Contains("找不到 acad.exe", ex.Message);
        Assert.IsType<Win32Exception>(ex.InnerException);
    }

    // 复审 finding 4（归属校验）：ROT 附着到的实例若非本方启动（用户同时开了 AutoCAD），
    // 清理/Dispose 的 Quit 会误杀他方实例并留下本方孤儿进程。注入归属不符→响亮拒绝，
    // **绝不 Quit 附着实例**（QuitCalls==0），只清理本方启动的 Process；_app 置空使下次
    // 调用真正重启（不得早退复用未校验/已置空对象）。
    [Fact]
    public void EnsureAppTArch_OwnershipMismatch_CleansOnlyLaunchedProcess_NoQuit()
    {
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        int launches = 0;
        Process? launched = null;
        AcadRuntime.ProcessLauncher = _ => { launches++; return launched = new Process(); };
        Process? cleaned = null;
        AcadRuntime.TarchProcessCleanup = p => { cleaned = p; return true; };
        AcadRuntime.OwnershipMatches = (_, __) => false;  // 注入：附着实例非本方
        var fake = new GateApp("19.1s (LMS Tech)", 1);    // 就绪（若未拒会走到 Gate1 成功）
        AcadAttach.ActiveObjectResolver = _ => fake;
        using var rt = new AcadRuntime();

        var ex = Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Contains("ROT 附着实例非本方启动", ex.Message);
        Assert.Contains("归属校验失败", ex.Message);
        Assert.Equal(0, fake.QuitCalls);                  // 绝不 Quit 他方实例
        Assert.Same(launched, cleaned);                   // 只清理本方启动的进程
        Assert.Equal(1, launches);

        // _app 已置空：第二次调用重新启动（而非早退返回已置空/未校验实例）。
        Assert.Throws<CadRuntimeException>(() => { rt.EnsureAppTArch("TElec20V5"); });
        Assert.Equal(2, launches);
    }

    // 复审 finding 4 对照：归属相符→正常走进 Gate1 并返回附着实例（不清理、不 Quit）。
    [Fact]
    public void EnsureAppTArch_OwnershipMatch_AttachesAndReturnsApp()
    {
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadRuntime.OwnershipMatches = (_, __) => true;
        var fake = new GateApp("19.1s (LMS Tech)", 1);
        AcadAttach.ActiveObjectResolver = _ => fake;
        using var rt = new AcadRuntime();

        var app = (object)rt.EnsureAppTArch("TElec20V5");
        Assert.Same(fake, app);
    }

    // 复审修复波：默认清理缝对"未 Start 的 fake Process"必须吞异常不抛（测试/真机句柄失效常态）。
    [Fact]
    public void DefaultTarchCleanup_NotStartedProcess_SwallowedReturnsFalse()
    {
        Assert.False(_origCleanup(new Process()));
    }
}
