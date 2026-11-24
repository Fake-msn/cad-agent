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
        AcadRuntime.StartupRetryAttempts = _origAttempts;
        AcadRuntime.StartupRetryDelay = _origDelay;
    }

    /// <summary>假 app：Gate1 读 Version + Documents.Count（两者可配）；Visible 供复用用例走 GetInfo。</summary>
    public sealed class GateApp
    {
        private readonly string _version;
        private readonly int _docCount;
        public GateApp(string version, int docCount) { _version = version; _docCount = docCount; }
        public string Version => _version;
        public GateDocs Documents => new(_docCount);
        public bool Visible { get; set; }
        public void Quit() { }
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
}
