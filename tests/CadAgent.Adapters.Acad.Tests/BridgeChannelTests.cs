using System.Diagnostics;
using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 桥通道单元测试（fake 缝，无真机/无 COM）：
/// 请求编号、.done 轮询、NETLOAD 看门狗（监视线程：20s 指引一次/3600s 硬超时终止本方会话）、
/// 注册探针握手（成功/误拒逐字）、DTO→Core 映射（含多 BLOCK 行 FIFO 配对），
/// 以及 ReadExplodeEvidence / ExplodeReplaceByHandle 全编排（fake 文档在通道目录内扮演桥）。
/// 通道目录=实例级（复审 F1）：目录与环境变量的创建/复用/Dispose 收尾在此逐项钉住。
/// </summary>
public class BridgeChannelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chan").FullName;
    private readonly Func<string, string, string> _origSend = BridgeChannel.SendCommand;
    private readonly TimeSpan _origProbe = BridgeChannel.ProbeTimeout;
    private readonly TimeSpan _origPoll = BridgeChannel.PollInterval;
    private readonly TimeSpan? _origTimeoutOverride = BridgeChannel.NetLoadTimeoutOverride;
    private readonly Action? _origHardHandler = BridgeChannel.HardTimeoutHandler;
    private readonly Func<string> _origDirFactory = AcadRuntime.ChannelDirFactory;
    private readonly string? _origDll = AcadRuntime.BridgeDllOverride;
    private readonly Func<int, bool> _origDialog = AcadRuntime.SecurityDialogDetector;
    private readonly Action<string>? _origGuidance = AcadRuntime.NetLoadSlowGuidance;
    private readonly TimeSpan _origNetLoadTimeout = AcadRuntime.NetLoadTimeout;
    private readonly TimeSpan _origThreshold = AcadRuntime.NetLoadNoProgressThreshold;
    private readonly TimeSpan _origCmdTimeout = AcadRuntime.BridgeCommandTimeout;
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<string, string?> _origTrustedSource = AcadRuntime.TrustedPathsSource;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly Func<string, Type?> _origProgId = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _origCreate = AcadRuntime.CreateComInstance;
    private readonly string? _origChannelEnv = Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar);

    public BridgeChannelTests()
    {
        // 确定性钉住：探针/轮询预算压到毫秒级；窗口/进程守卫缝不触真机。
        BridgeChannel.ProbeTimeout = TimeSpan.FromMilliseconds(150);
        BridgeChannel.PollInterval = TimeSpan.FromMilliseconds(10);
        BridgeChannel.NetLoadTimeoutOverride = null;
        BridgeChannel.HardTimeoutHandler = null;
        AcadRuntime.BridgeCommandTimeout = TimeSpan.FromSeconds(2);
        AcadRuntime.SecurityDialogDetector = _ => false;
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        BridgeChannel.SendCommand = _origSend;
        BridgeChannel.ProbeTimeout = _origProbe;
        BridgeChannel.PollInterval = _origPoll;
        BridgeChannel.NetLoadTimeoutOverride = _origTimeoutOverride;
        BridgeChannel.HardTimeoutHandler = _origHardHandler;
        AcadRuntime.ChannelDirFactory = _origDirFactory;
        AcadRuntime.BridgeDllOverride = _origDll;
        AcadRuntime.SecurityDialogDetector = _origDialog;
        AcadRuntime.NetLoadSlowGuidance = _origGuidance;
        AcadRuntime.NetLoadTimeout = _origNetLoadTimeout;
        AcadRuntime.NetLoadNoProgressThreshold = _origThreshold;
        AcadRuntime.BridgeCommandTimeout = _origCmdTimeout;
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadRuntime.TrustedPathsSource = _origTrustedSource;
        AcadAttach.ActiveObjectResolver = _origActive;
        VendorGuard.ProcessNames = _origNames;
        AcadRuntime.ProgIdResolver = _origProgId;
        AcadRuntime.CreateComInstance = _origCreate;
        Environment.SetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar, _origChannelEnv);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    // ---- 请求/轮询（spec §2 三文件协议）----

    [Fact]
    public void WriteRequest_IncrementsId()
    {
        Assert.Equal("1", BridgeChannel.WriteRequest(_dir, "a"));
        Assert.Equal("2", BridgeChannel.WriteRequest(_dir, "b"));
        Assert.Equal("a", File.ReadAllText(Path.Combine(_dir, "req-1.txt")));
    }

    [Fact]
    public void WaitDone_ReturnsResultContent()
    {
        File.WriteAllText(Path.Combine(_dir, "res-1.txt"), "status\tok\n");
        File.WriteAllText(Path.Combine(_dir, "res-1.done"), "1");
        Assert.Equal("status\tok\n", BridgeChannel.WaitDone(_dir, "1", TimeSpan.FromSeconds(1)));
    }

    // 复审 F1 连带：通道目录实例级复用后，超时/失败路径遗留的请求会一直被桥的 NextPending
    // 拾取（新请求永远轮不到）——写新请求前必须把历史未完成请求标记放弃（done 置位）。
    [Fact]
    public void WriteRequest_AbandonsStalePendingRequests()
    {
        File.WriteAllText(Path.Combine(_dir, "req-1.txt"), "stale-probe");

        Assert.Equal("2", BridgeChannel.WriteRequest(_dir, "fresh"));

        Assert.True(File.Exists(Path.Combine(_dir, "res-1.done")));
        Assert.False(File.Exists(Path.Combine(_dir, "res-2.done")));   // 新请求仍未完成
    }

    [Fact]
    public void WaitDone_TimesOut_ReturnsNull()
        => Assert.Null(BridgeChannel.WaitDone(_dir, "9", TimeSpan.FromMilliseconds(100)));

    // ---- NETLOAD 看门狗（监视线程：指引 + 硬超时）----

    [Fact]
    public void EnsureNetLoad_WatchdogNeverProgresses_ThrowsSoftTimeout()
    {
        BridgeChannel.SendCommand = (_, _) => "";   // 只发送、不产进展
        var ex = Assert.Throws<CadRuntimeException>(() =>
            BridgeChannel.EnsureNetLoad("b.dll", _dir, _ => false, TimeSpan.FromMilliseconds(120)));
        Assert.Contains("NETLOAD 超时", ex.Message);
    }

    // 复审 F3(a)：发送阻塞期间由监视线程在无进展阈值打印指引（只一次）。
    [Fact]
    public void EnsureNetLoad_SlowSend_GuidanceOnceWithTrustedPathsHint()
    {
        BridgeChannel.SendCommand = (_, _) => { Thread.Sleep(250); return ""; };
        var guided = new List<string>();

        BridgeChannel.EnsureNetLoad(
            @"C:\bridge\CadAgent.TArchBridge.dll", _dir, _ => true, TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(30), guided.Add);

        string text = Assert.Single(guided);
        Assert.Contains("TRUSTEDPATHS", text);
        Assert.Contains(@"C:\bridge", text);
    }

    // 复审 F3(a) 对照：快速返回不打印指引（监视线程随发送返回取消）。
    [Fact]
    public void EnsureNetLoad_FastSend_NoGuidance()
    {
        BridgeChannel.SendCommand = (_, _) => "";
        var guided = new List<string>();

        BridgeChannel.EnsureNetLoad("b.dll", _dir, _ => true, TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(30), guided.Add);
        Thread.Sleep(120);   // 给潜在迟到回调留窗（若误调度必现）

        Assert.Empty(guided);
    }

    // 复审 F3(b)：发送仍阻塞且总预算耗尽 → 硬超时处理器（终止本方会话）被调用一次，
    // 随后的发送异常包装为规格逐字文案。
    [Fact]
    public void EnsureNetLoad_BlockedSend_HardTimeoutInvokesHandlerAndWrapsExactMessage()
    {
        BridgeChannel.NetLoadTimeoutOverride = TimeSpan.FromMilliseconds(50);
        int handlerCalls = 0;
        BridgeChannel.HardTimeoutHandler = () => Interlocked.Increment(ref handlerCalls);
        BridgeChannel.SendCommand = (_, _) =>
        {
            Thread.Sleep(300);                                  // 模拟 COM 同步阻塞
            throw new InvalidOperationException("会话已终止");   // 进程被杀后 COM 抛错
        };

        var ex = Assert.Throws<CadRuntimeException>(() =>
            BridgeChannel.EnsureNetLoad("b.dll", _dir, _ => true, TimeSpan.FromSeconds(30)));

        Assert.Equal("NETLOAD 超时（3600s）：已终止本方 AutoCAD 会话", ex.Message);
        Assert.Equal(1, handlerCalls);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    // ---- 注册探针握手（R3 修订：误拒不靠回显文本，靠探针是否写出 .done）----

    [Fact]
    public void EnsureLoaded_ProbeDone_Proceeds()
    {
        var sent = new List<string>();
        BridgeChannel.SendCommand = (cmd, mark) =>
        {
            sent.Add(mark);
            if (mark != "netload") RespondFail(_dir);   // 桥收到探针：错图 fail + done
            return "";
        };

        BridgeChannel.EnsureLoaded("b.dll", _dir, _ => true, TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { "netload", "probe" }, sent);
    }

    // 复审 F5：误拒文案逐字（不得多"了"）。
    [Fact]
    public void EnsureLoaded_ProbeTimeout_ThrowsDeniedWithExactWording()
    {
        BridgeChannel.SendCommand = (_, _) => "";   // NETLOAD 被拒：命令未注册，探针无 done
        var ex = Assert.Throws<CadRuntimeException>(() =>
            BridgeChannel.EnsureLoaded("b.dll", _dir, _ => true, TimeSpan.FromSeconds(1)));
        Assert.Equal(
            "NETLOAD 被拒绝：可能点击【不加载】/TRUSTEDPATHS 未含桥目录/SECURELOAD 阻止",
            ex.Message);
        Assert.Equal(BridgeChannel.DeniedMessage, ex.Message);
    }

    private static void RespondFail(string channelDir)
    {
        string? id = LatestPendingId(channelDir);
        if (id == null) return;
        File.WriteAllText(Path.Combine(channelDir, "res-" + id + ".txt"),
            "status\tfail\nerror\tInvalidOperationException: 错图\n");
        File.WriteAllText(Path.Combine(channelDir, "res-" + id + ".done"), "1");
    }

    // 与桥 BridgeFiles.NextPending 同口径：**最小**未完成 id（旧请求残留会立刻暴露为新请求超时）。
    private static string? LatestPendingId(string channelDir)
    {
        if (string.IsNullOrEmpty(channelDir) || !Directory.Exists(channelDir)) return null;
        string? best = null;
        int bestId = int.MaxValue;
        foreach (var f in Directory.GetFiles(channelDir, "req-*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length <= 4 || !int.TryParse(name.Substring(4), out int n)) continue;
            if (File.Exists(Path.Combine(channelDir, "res-" + name.Substring(4) + ".done"))) continue;
            if (n < bestId) { bestId = n; best = name.Substring(4); }
        }
        return best;
    }

    // ---- DTO→Core 映射（T10 真机基准的形状先在宿主侧钉住）----

    internal static BridgeResult SampleOk()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto(
            "21E00", "TDbWall", "ImpCurve", "0dt",
            new[] { 127749.996, 120300.0, 0.0, 127849.996, 130100.0, 3000.0 }, true));
        r.Prims.Add(new PrimDto("21E00", 0, "AcDbLine", new[] { "start=0,0,0", "end=100,0,0" }));
        r.Prims.Add(new PrimDto("21E00", 0, "AcDbBlockReference", new[] { "extents=0,0,0..1,1,1" }));
        r.Prims.Add(new PrimDto("21E00", -1, "PRIM-TRUNCATED", new[] { "65" }));
        r.Blocks.Add(new BlockDto("21E00", "$TWTSYS$00000508", new[] { 500700.0, 1325.0, 0.0 }));
        r.XData.Add(new XDataDto("21E00", 1001, "HYDRANT_RADIUS"));
        r.XData.Add(new XDataDto("21E00", 1000, "28"));
        return r;
    }

    [Fact]
    public void MapExplodeEvidence_MapsPrimsBlocksAndXData()
    {
        var list = AcadRuntime.MapExplodeEvidence(BridgeResult.Parse(SampleOk().Write()));

        var e = Assert.Single(list);
        Assert.Equal("21E00", e.Handle);
        Assert.Equal("TDbWall", e.RxClass);
        Assert.Equal("ImpCurve", e.WrapperType);
        Assert.Equal("0dt", e.Layer);
        Assert.True(e.FullyExplodable);
        Assert.Equal(127749.996, e.Bbox![0], 3);
        Assert.Equal(2, e.PrimitiveCount);                       // PRIM-TRUNCATED 标记不计
        Assert.Equal(0, e.NestedTdbCount);
        Assert.Null(e.CenterlineJson);                           // 中心线由宿主推导（T6）

        Assert.Equal("line", e.Primitives[0].Kind);
        Assert.Equal(new double[] { 0, 0, 0, 100, 0, 0 }, e.Primitives[0].Points);

        // 块引用：桥只给 extents token，块名/插入点取自 BLOCK 行。
        Assert.Equal("block", e.Primitives[1].Kind);
        Assert.Equal("$TWTSYS$00000508", e.Primitives[1].BlockName);
        Assert.Equal(new double[] { 500700, 1325, 0 }, e.Primitives[1].Points);

        // XData：1001=键名（探针实证），随后值行归入该键。
        Assert.Equal("28", e.XData["HYDRANT_RADIUS"]);
    }

    [Fact]
    public void MapExplodeEvidence_TruncatedRow_DoesNotInflateCount()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("A1", "TDbWall", null, null, null, false));
        r.Prims.Add(new PrimDto("A1", -1, "PRIM-TRUNCATED", new[] { "100" }));
        r.Fails.Add(new FailDto("A1", "递归超深"));
        r.Fails.Add(new FailDto("A1", "extents:Exception"));

        var e = Assert.Single(AcadRuntime.MapExplodeEvidence(r));
        Assert.Empty(e.Primitives);
        Assert.Equal(0, e.PrimitiveCount);
        Assert.Equal(1, e.NestedTdbCount);                       // 仅"递归超深/非实体产物"计残留上界
        Assert.Null(e.Bbox);
    }

    // 复审 F4：同一句柄多条 BLOCK 行 → 块基元按出现序 FIFO 配对（旧实现恒取首条，全部错位）。
    [Fact]
    public void MapExplodeEvidence_MultipleBlockRows_PairByFifoOrder()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("B1", "TDbWall", null, null, null, true));
        r.Prims.Add(new PrimDto("B1", 0, "AcDbBlockReference", Array.Empty<string>()));
        r.Prims.Add(new PrimDto("B1", 0, "AcDbBlockReference", Array.Empty<string>()));
        r.Blocks.Add(new BlockDto("B1", "BLK-A", new[] { 1.0, 2.0, 0.0 }));
        r.Blocks.Add(new BlockDto("B1", "BLK-B", new[] { 3.0, 4.0, 0.0 }));

        var e = Assert.Single(AcadRuntime.MapExplodeEvidence(r));

        Assert.Equal(2, e.Primitives.Count);
        Assert.Equal("BLK-A", e.Primitives[0].BlockName);
        Assert.Equal(new double[] { 1, 2, 0 }, e.Primitives[0].Points);
        Assert.Equal("BLK-B", e.Primitives[1].BlockName);
        Assert.Equal(new double[] { 3, 4, 0 }, e.Primitives[1].Points);
    }

    // 复审 F4 边界：BLOCK 队列耗尽 → 块名 null 留痕（不猜、不重复使用已配对记录、不抛）。
    [Fact]
    public void MapExplodeEvidence_BlockRowExhausted_LeavesNameNull()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("B1", "TDbWall", null, null, null, true));
        r.Prims.Add(new PrimDto("B1", 0, "AcDbBlockReference", Array.Empty<string>()));

        var e = Assert.Single(AcadRuntime.MapExplodeEvidence(r));

        var prim = Assert.Single(e.Primitives);
        Assert.Equal("block", prim.Kind);
        Assert.Null(prim.BlockName);
    }

    [Fact]
    public void PrimitiveKind_FamiliesCollapse()
    {
        Assert.Equal("line", AcadRuntime.PrimitiveKind("AcDbLine"));
        Assert.Equal("arc", AcadRuntime.PrimitiveKind("AcDbArc"));
        Assert.Equal("circle", AcadRuntime.PrimitiveKind("AcDbCircle"));
        Assert.Equal("text", AcadRuntime.PrimitiveKind("AcDbText"));
        Assert.Equal("mtext", AcadRuntime.PrimitiveKind("AcDbMText"));
        Assert.Equal("polyline", AcadRuntime.PrimitiveKind("AcDb2dPolyline"));
        Assert.Equal("hatch", AcadRuntime.PrimitiveKind("AcDbHatch"));
        Assert.Equal("block", AcadRuntime.PrimitiveKind("AcDbBlockReference"));
        Assert.Equal("other", AcadRuntime.PrimitiveKind("AcDb3PointAngularDimension"));
    }

    // ---- 全编排：fake 文档在通道目录内扮演桥（无 COM）----

    [Fact]
    public void ReadExplodeEvidence_LaunchesOpensReadsCloses_KeepsChannelUntilDispose()
    {
        string channelDir = PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = name => { calls.Add("Launch(" + name + ")"); return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        var list = rt.ReadExplodeEvidence("16#1116.dwg");

        var e = Assert.Single(list);
        Assert.Equal("21E00", e.Handle);
        Assert.Equal("28", e.XData["HYDRANT_RADIUS"]);
        Assert.Equal(2, e.PrimitiveCount);

        Assert.Equal(
            new[]
            {
                "Launch(TElec20V5)", "Open(16#1116.dwg, ReadOnly=True)",
                "NETLOAD", "READ",              // 探针握手（dwg=__probe__ → 桥回 fail）
                "READ",                          // 正式 READ
                "Close(False)",
            },
            calls);
        // 复审 F1：通道目录=实例级资源——调用结束仍存在（桥进程正持该目录）；
        // 目录删除与环境变量还原只在 Dispose。
        Assert.True(Directory.Exists(channelDir));
        Assert.Equal(channelDir, Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar));

        rt.Dispose();

        Assert.False(Directory.Exists(channelDir));
        Assert.Equal(_origChannelEnv, Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar));
    }

    [Fact]
    public void ReadExplodeEvidence_SecondCall_SkipsNetLoad()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        rt.ReadExplodeEvidence("16#1116.dwg");

        calls.Clear();   // 第二轮只看它自己的调用序
        rt.ReadExplodeEvidence("16#1116.dwg");

        // 同实例第二次：不重发 NETLOAD、不重跑探针——只有 开图/READ/关图。
        Assert.Equal(new[] { "Open(16#1116.dwg, ReadOnly=True)", "READ", "Close(False)" }, calls);
    }

    // 复审 F1(i)：/p 启动瞬间（CreateProcess 前）环境变量必须已指向本实例通道目录——
    // 否则子进程快照不到该变量，桥永远读不到请求（fake 测试曾掩盖此点）。
    [Fact]
    public void ReadExplodeEvidence_ChannelEnvSetBeforeLaunch()
    {
        string channelDir = PrepareBridge();
        string? envAtLaunch = "unset";
        AcadRuntime.ProcessLauncher = _ =>
        {
            envAtLaunch = Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar);
            return new Process();
        };
        AcadAttach.ActiveObjectResolver = _ => new FakeBridgeApp(new FakeBridgeDoc(new List<string>()));

        using var rt = new AcadRuntime();
        rt.ReadExplodeEvidence("16#1116.dwg");

        Assert.Equal(channelDir, envAtLaunch);
    }

    // 复审 F1(ii)：两次 READ + 一次 EXPLODE_REPLACE 全程同一目录（目录工厂只调用一次）。
    [Fact]
    public void BridgeCalls_ReuseSingleInstanceChannelDir()
    {
        string channelDir = Path.Combine(_dir, "channel");
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        File.WriteAllText(dll, "stub");
        AcadRuntime.BridgeDllOverride = dll;
        int factoryCalls = 0;
        AcadRuntime.ChannelDirFactory = () => { factoryCalls++; return channelDir; };

        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        int launches = 0;
        AcadRuntime.ProcessLauncher = _ => { launches++; return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        rt.ReadExplodeEvidence("16#1116.dwg");
        rt.ReadExplodeEvidence("16#1116.dwg");
        rt.Open("live.dwg");
        rt.ExplodeReplaceByHandle("21E00", Path.Combine(_dir, "out.live.dwg"));

        Assert.Equal(1, factoryCalls);          // 目录只建一次（实例级，非每次调用新建）
        Assert.Equal(1, launches);              // 同实例复用进程
        Assert.NotEmpty(doc.ChannelDirs);
        Assert.All(doc.ChannelDirs, d => Assert.Equal(channelDir, d));
    }

    // 复审 F1(iii)：Dispose 删目录并还原宿主原环境变量。
    [Fact]
    public void Dispose_CleansChannelDirAndRestoresEnv()
    {
        string channelDir = Path.Combine(_dir, "channel");
        AcadRuntime.ChannelDirFactory = () => channelDir;
        AcadRuntime.TrustedPathsSource = _ => null;   // R17：强制走 COM 回退分支（本用例钉住启动路径的副作用）
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => new FakeBridgeApp(new FakeBridgeDoc(new List<string>()));

        var rt = new AcadRuntime();
        rt.GetTrustedPaths();   // 走天正 profile 启动路径→建实例级通道

        Assert.True(Directory.Exists(channelDir));
        Assert.Equal(channelDir, Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar));

        rt.Dispose();

        Assert.False(Directory.Exists(channelDir));
        Assert.Equal(_origChannelEnv, Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar));
    }

    // 复审 F2：TRUSTEDPATHS 必须取自天正 profile（/p）实例——不得走 EnsureApp()（默认 profile）
    // 而缓存错误实例、返回错误 profile 的信任路径。
    // Fix round 4（R17）：此口径只在**注册表无值时的 COM 回退分支**成立；缝注入 null 强制走该分支。
    [Fact]
    public void GetTrustedPaths_LaunchesTArchProfile()
    {
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        AcadRuntime.TrustedPathsSource = _ => null;   // R17：注册表优先不开实例；钉住回退分支
        AcadRuntime.ProcessLauncher = name => { calls.Add("Launch(" + name + ")"); return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        string trusted = rt.GetTrustedPaths();

        Assert.Equal("C:\\trusted;C:\\other", trusted);
        Assert.Equal("TRUSTEDPATHS", doc.LastVariableName);   // 文档面 GetVariable（Application 无该系统变量 API）
        Assert.Equal(new[] { "Launch(TElec20V5)" }, calls);   // 以 /p 天正 profile 启动
    }

    // 复审 F2：复用路径（_app 已由 EnsureApp() 建立）不得跳过 Gate1——可经 Version 读取计数观测。
    [Fact]
    public void EnsureAppTArch_ReusePath_StillWaitsGate1()
    {
        int launches = 0;
        var app = new FakeBridgeApp(new FakeBridgeDoc(new List<string>()));
        AcadRuntime.ProcessLauncher = _ => { launches++; return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        rt.EnsureAppTArch("TElec20V5");
        int afterFirst = app.VersionReads;
        Assert.True(afterFirst > 0);

        rt.EnsureAppTArch("TElec20V5");   // 复用：仍执行 Gate1 就绪等待
        Assert.True(app.VersionReads > afterFirst);
        Assert.Equal(1, launches);        // 复用不重启
    }

    [Fact]
    public void ReadExplodeEvidence_WhenDocumentAlreadyOpen_ThrowsGuard()
    {
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => app;

        using var rt = new AcadRuntime();
        rt.Open("already-open.dwg");   // 生产 Open 路径：_doc 就位

        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence("16#1116.dwg"));
        Assert.Contains("已有打开的图纸", ex.Message);
        Assert.Contains("ReadExplodeEvidence(path)", ex.Message);
    }

    [Fact]
    public void ExplodeReplaceByHandle_ReturnsReplaceResult()
    {
        string channelDir = PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => app;

        string liveOut = Path.Combine(_dir, "out.live.dwg");
        using var rt = new AcadRuntime();
        rt.Open("live.dwg");
        var r = rt.ExplodeReplaceByHandle("21E00", liveOut);

        Assert.Equal("21E00", r.Handle);
        Assert.Equal(7, r.NewCount);
        Assert.Equal(liveOut, r.SavedPath);
        Assert.True(r.BboxMatch);
        Assert.Equal(1e-3, r.Tolerance, 6);
        // 首次桥操作：NETLOAD+探针后发 EXPLODE_REPLACE；请求携带 handle/liveout。
        Assert.Equal(new[] { "Open(live.dwg)", "NETLOAD", "READ", "EXPLODE_REPLACE" }, calls);
        Assert.Contains("op\texplode-replace", doc.LastRequest);
        Assert.Contains("handle\t21E00", doc.LastRequest);
        Assert.Contains("liveout\t", doc.LastRequest);
        Assert.True(Directory.Exists(channelDir));   // 实例级通道（Dispose 才清）
    }

    [Fact]
    public void ReadExplodeEvidence_NullPath_Throws()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence(null!));
        Assert.Contains("无打开的图纸参数", ex.Message);
    }

    // 低优先补齐：status fail → "桥失败: " + Error（逐字），且失败路径仍只读关闭本文档。
    [Fact]
    public void ReadExplodeEvidence_StatusFail_WrapsBridgeError()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls) { FailRealRead = true };
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence("16#1116.dwg"));

        Assert.Equal("桥失败: InvalidOperationException: 炸开失败", ex.Message);
        Assert.Contains("Close(False)", calls);
    }

    // ---- Fix round 5（R18）：桥 SendCommand 忙窗拒绝有界重试 ----
    // 真机 run7 实证：三条真机用例皆在 Document.SendCommand 命中 TArch 启动忙窗 →
    // RPC_E_CALL_REJECTED(0x80010001)「被呼叫方拒绝接收呼叫」。单写调用可安全重试。

    /// <summary>前两次发送被拒、第三次成功 → 全编排（NETLOAD→探针→正式 READ）恢复绿；
    /// NETLOAD 尝试次数=3（被拒 2 次 + 成功 1 次），调用序与无拒绝时一致（不重复发命令）。</summary>
    [Fact]
    public void ReadExplodeEvidence_SendCommandBusyTwiceThenSucceeds_RetriesAndCompletes()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls) { BusyRejectionsRemaining = 2 };
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        var origAttempts = AcadRuntime.StartupRetryAttempts;
        var origDelay = AcadRuntime.StartupRetryDelay;
        AcadRuntime.StartupRetryAttempts = 5;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var rt = new AcadRuntime();
            var list = rt.ReadExplodeEvidence("16#1116.dwg");

            Assert.Equal("21E00", Assert.Single(list).Handle);
            Assert.Equal(3, doc.NetLoadAttempts);            // 被拒 2 + 成功 1 = 有界重试生效
            Assert.Equal(
                new[] { "Open(16#1116.dwg, ReadOnly=True)", "NETLOAD", "READ", "READ", "Close(False)" },   // 探针 READ + 正式 READ
                calls);
        }
        finally
        {
            AcadRuntime.StartupRetryAttempts = origAttempts;
            AcadRuntime.StartupRetryDelay = origDelay;
        }
    }

    /// <summary>始终被拒 → 重试耗尽（=StartupRetryAttempts）后异常上抛，经既有 catch 包装为
    /// CadRuntimeException（消息含真因「被呼叫方拒绝」+「深度炸开读取失败」，绝不静默吞）。</summary>
    [Fact]
    public void ReadExplodeEvidence_SendCommandAlwaysBusy_ThrowsWrappedAfterBoundedAttempts()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls) { BusyRejectionsRemaining = int.MaxValue };
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        var origAttempts = AcadRuntime.StartupRetryAttempts;
        var origDelay = AcadRuntime.StartupRetryDelay;
        AcadRuntime.StartupRetryAttempts = 3;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var rt = new AcadRuntime();
            var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence("16#1116.dwg"));

            Assert.Contains("深度炸开读取失败", ex.Message);
            Assert.Contains("被呼叫方拒绝", ex.Message);
            Assert.Equal(3, doc.NetLoadAttempts);            // 有界：==StartupRetryAttempts（非无限挂起）
        }
        finally
        {
            AcadRuntime.StartupRetryAttempts = origAttempts;
            AcadRuntime.StartupRetryDelay = origDelay;
        }
    }

    // 低优先补齐：桥 DLL 缺失文案逐字（先于 NETLOAD，不触进程）。
    [Fact]
    public void BridgeDllPath_Missing_MessageExact()
    {
        AcadRuntime.BridgeDllOverride = null;

        var ex = Assert.Throws<CadRuntimeException>(() => AcadRuntime.BridgeDllPath());

        Assert.Equal("桥未构建（需 AutoCAD 19.1 引用）：CadAgent.TArchBridge.dll", ex.Message);
    }

    // 公共编排前奏：桥 DLL 占位 + 固定通道目录（fake 文档经环境变量定位该目录）。
    private string PrepareBridge()
    {
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        File.WriteAllText(dll, "stub");
        AcadRuntime.BridgeDllOverride = dll;
        string channelDir = Path.Combine(_dir, "channel");
        AcadRuntime.ChannelDirFactory = () => channelDir;
        return channelDir;
    }

    /// <summary>
    /// 假文档：SendCommand 扮演桥——NETLOAD 记一笔；READ/EXPLODE_REPLACE 按通道目录内最新
    /// 待处理请求回写结果。通道目录一律经环境变量解析（顺带记录每次发送时看到的目录，供
    /// "实例级复用"断言使用）。
    /// </summary>
    public sealed class FakeBridgeDoc
    {
        public FakeBridgeDoc(List<string> calls) { Calls = calls; }
        public List<string> Calls { get; }
        /// <summary>每次 SendCommand 时环境变量里的通道目录（F1 复用断言用）。</summary>
        public List<string> ChannelDirs { get; } = new();
        /// <summary>正式 READ 返回 status fail（探针仍回"错图"fail）。</summary>
        public bool FailRealRead { get; set; }
        /// <summary>
        /// 忙窗拒绝注入（Fix round 5，R18）：前 N 次 SendCommand 抛 COMException
        /// HResult=0x80010001（RPC_E_CALL_REJECTED）——真机 run7 形态。
        /// </summary>
        public int BusyRejectionsRemaining { get; set; }
        /// <summary>NETLOAD 发送尝试次数（含被拒重试；"有界重试次数"断言用）。</summary>
        public int NetLoadAttempts { get; private set; }
        public string Name => "16#1116.dwg";
        public string LastRequest { get; private set; } = "";
        /// <summary>最近一次文档面系统变量读取（GetVariable）的变量名（TRUSTEDPATHS 口径观测）。</summary>
        public string? LastVariableName { get; private set; }
        public string GetVariable(string name)
        {
            LastVariableName = name;
            return "C:\\trusted;C:\\other";
        }
        public void Close(bool save) => Calls.Add("Close(" + save + ")");

        public void SendCommand(string command)
        {
            string cmd = command.TrimEnd('\r', '\n');
            bool isNetLoad = cmd.StartsWith("(command \"NETLOAD\"", StringComparison.Ordinal);
            if (isNetLoad) NetLoadAttempts++;
            // R18：忙窗拒绝注入——先计数后抛，模拟 COM 在 TArch 启动期直接拒绝该次调用。
            if (BusyRejectionsRemaining > 0)
            {
                BusyRejectionsRemaining--;
                throw new System.Runtime.InteropServices.COMException(
                    "被呼叫方拒绝接收呼叫。 (0x80010001 (RPC_E_CALL_REJECTED))",
                    unchecked((int)0x80010001));
            }
            string channel = Environment.GetEnvironmentVariable(AcadRuntime.BridgeChannelEnvVar) ?? "";
            ChannelDirs.Add(channel);
            if (isNetLoad)
            {
                Calls.Add("NETLOAD");
                return;
            }
            bool read = cmd.StartsWith(BridgeChannel.ReadCommand, StringComparison.Ordinal);
            bool replace = cmd.StartsWith(BridgeChannel.ExplodeReplaceCommand, StringComparison.Ordinal);
            if (!read && !replace)
            {
                Calls.Add("UNEXPECTED:" + cmd);
                return;
            }
            Calls.Add(read ? "READ" : "EXPLODE_REPLACE");
            string? id = LatestPendingId(channel);
            if (id == null) return;
            string req = File.ReadAllText(Path.Combine(channel, "req-" + id + ".txt"));
            LastRequest = req;
            string body;
            if (!read)
                body = SampleReplace(liveOut: LastLiveOut(req)).Write();
            else if (req.Contains("dwg\t" + BridgeChannel.ProbeDwg, StringComparison.Ordinal))
                body = "status\tfail\nerror\tInvalidOperationException: 错图\n";
            else if (FailRealRead)
                body = "status\tfail\nerror\tInvalidOperationException: 炸开失败\n";
            else
                body = SampleOk().Write();
            File.WriteAllText(Path.Combine(channel, "res-" + id + ".txt"), body);
            File.WriteAllText(Path.Combine(channel, "res-" + id + ".done"), "1");
        }

        private static string LastLiveOut(string request)
        {
            foreach (string line in request.Split('\n'))
                if (line.StartsWith("liveout\t", StringComparison.Ordinal))
                    return TsCodec.Unescape(line.Substring("liveout\t".Length));
            return "";
        }
    }

    internal static BridgeResult SampleReplace(string liveOut)
    {
        var r = new BridgeResult
        {
            Status = "ok",
            Replace = new ReplaceDto("21E00", 7, liveOut, true, 0.001),
        };
        return r;
    }

    /// <summary>
    /// 假 app：EnsureAppTArch 的 Gate1（Version/Documents.Count）与 Open 走此面；
    /// VersionReads 供"复用路径仍执行 Gate1"（F2）观测。真机实证 Application 无
    /// GetSystemVariable → TRUSTEDPATHS 由 <see cref="FakeBridgeDoc.GetVariable"/>（文档面）承担。
    /// </summary>
    public sealed class FakeBridgeApp
    {
        private readonly FakeBridgeDoc _doc;
        private int _versionReads;
        public FakeBridgeApp(FakeBridgeDoc doc) => _doc = doc;
        public string Version
        {
            get { Interlocked.Increment(ref _versionReads); return "19.1s (LMS Tech)"; }
        }
        public int VersionReads => Volatile.Read(ref _versionReads);
        public FakeBridgeDocuments Documents => new(_doc);
        public FakeBridgeDoc ActiveDocument => _doc;
        public bool Visible { get; set; }
        public void Quit() { }
    }

    /// <summary>假 Documents：/p 默认已开一图（Count=1）；Open 返回同一假文档并记录调用。</summary>
    public sealed class FakeBridgeDocuments
    {
        private readonly FakeBridgeDoc _doc;
        public FakeBridgeDocuments(FakeBridgeDoc doc) => _doc = doc;
        public int Count => 1;
        public FakeBridgeDoc Open(string path)
        {
            _doc.Calls.Add("Open(" + path + ")");
            return _doc;
        }
        public FakeBridgeDoc Open(string path, bool readOnly)
        {
            _doc.Calls.Add("Open(" + path + ", ReadOnly=" + readOnly + ")");
            return _doc;
        }
    }
}
