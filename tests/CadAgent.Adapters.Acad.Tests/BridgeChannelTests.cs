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
    // I6（复审修复波）：命令等待缝迁到 BridgeChannel.CommandWait（测试压到毫秒级）。
    private readonly TimeSpan _origCmdWait = BridgeChannel.CommandWait;
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
        BridgeChannel.CommandWait = TimeSpan.FromSeconds(2);
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
        BridgeChannel.CommandWait = _origCmdWait;
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
        // F4 对照：未注入安全框探测（=未检出）→ 原文案逐字，不附加诊断后缀。
        Assert.DoesNotContain(BridgeChannel.SecurityDialogSeenSuffix, text);
    }

    // F4（最终复审）：监视线程按节拍探测安全框，命中至少一次 → 指引文案加性附加
    // "（已检测到安全框）"，且仍只打印一次（快速返回/未检出路径文案不变）。
    [Fact]
    public void EnsureNetLoad_SlowSend_SecurityDialogDetected_AppendsDiagnosticSuffixOnce()
    {
        BridgeChannel.SendCommand = (_, _) => { Thread.Sleep(250); return ""; };
        var guided = new List<string>();
        int probes = 0;

        BridgeChannel.EnsureNetLoad(
            @"C:\bridge\CadAgent.TArchBridge.dll", _dir, _ => true, TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(30), guided.Add, () => { Interlocked.Increment(ref probes); return true; });

        string text = Assert.Single(guided);
        Assert.Contains(BridgeChannel.SecurityDialogSeenSuffix, text);
        Assert.Contains("TRUSTEDPATHS", text);
        Assert.True(probes >= 1);   // 首发立即探测
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
        // B1：块定义行与 ENTITY（模型空间实例）分离——同一响应携带两段互不混同。
        r.BlockDefs.Add(new BlockDefDto("$equip$00002832", "TDbECabletry", 1364));
        r.BlockDefs.Add(new BlockDefDto("门块A", "TDbEFitting", 2));
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

    // A2（边界补齐）/ PR32：FailReason 取该句柄**首条** FAIL 行原因，FailReasons 携带该句柄
    // **全部** FAIL（按桥输出序）——桥内 explodes 失败的分类（extents:degenerate / extents:Exception /
    // explode:eInvalidInput）由此如实透出，宿主不做二次推断。
    [Fact]
    public void MapExplodeEvidence_FailReason_FirstFailRowPerHandle()
    {
        var r = new BridgeResult { Status = "ok" };
        r.Evidence.Add(new EvidenceDto("D1", "TDbWall", null, null, null, true));
        r.Evidence.Add(new EvidenceDto("S1", "TDbSymbArrow", null, null, null, false));
        r.Evidence.Add(new EvidenceDto("OK1", "TDbWall", null, null, null, true));
        r.Fails.Add(new FailDto("D1", "extents:degenerate"));
        r.Fails.Add(new FailDto("D1", "explode:Exception"));       // 后续 FAIL 不覆盖首条，但须入 FailReasons
        r.Fails.Add(new FailDto("S1", "explode:eInvalidInput"));

        var list = AcadRuntime.MapExplodeEvidence(r);

        var d1 = list.Single(e => e.Handle == "D1");
        Assert.Equal("extents:degenerate", d1.FailReason);
        Assert.NotNull(d1.FailReasons);
        Assert.Equal(new[] { "extents:degenerate", "explode:Exception" }, d1.FailReasons!);
        var s1 = list.Single(e => e.Handle == "S1");
        Assert.Equal("explode:eInvalidInput", s1.FailReason);
        Assert.NotNull(s1.FailReasons);
        Assert.Equal(new[] { "explode:eInvalidInput" }, s1.FailReasons!);
        Assert.Null(list.Single(e => e.Handle == "OK1").FailReason);   // 无 FAIL 行 → null
        Assert.Null(list.Single(e => e.Handle == "OK1").FailReasons);
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

    // B1（边界补齐）：块定义伴生读数——与 ReadExplodeEvidence 同一次桥会话的读数，
    // 由实例缓存直接返回（**不二次开图/不发命令**；相对/绝对路径按同一实体归一）。
    [Fact]
    public void ReadExplodeBlockDefs_AfterReadExplodeEvidence_CacheHit_NoSecondOpen()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls);
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        rt.ReadExplodeEvidence("16#1116.dwg");
        int afterRead = calls.Count;

        var defs = rt.ReadExplodeBlockDefs("16#1116.dwg");

        Assert.Equal(2, defs.Count);
        Assert.Contains(defs, d => d.RxClass == "TDbECabletry" && d.Count == 1364);
        Assert.Contains(defs, d => d.BlockName == "门块A" && d.RxClass == "TDbEFitting" && d.Count == 2);
        Assert.Equal(afterRead, calls.Count);   // 缓存命中：无新增 Open/READ

        // 路径形态差异（相对/绝对）按同一实体归一 → 仍命中缓存。
        var again = rt.ReadExplodeBlockDefs(Path.GetFullPath("16#1116.dwg"));
        Assert.Equal(defs.Count, again.Count);
        Assert.Equal(afterRead, calls.Count);
    }

    // B1：无先读即取块定义 → 响亮拒绝（缓存缺失不得静默空表，也不得隐式二次开图）。
    [Fact]
    public void ReadExplodeBlockDefs_NoPriorRead_Throws()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeBlockDefs("16#1116.dwg"));
        Assert.Contains("ReadExplodeEvidence", ex.Message);
    }

    // M-3(a)（修复波1）：Dispose 清空块定义缓存——实例销毁后不得再返回上一会话的块定义表
    //（缓存生命周期 = 桥会话生命周期；残留缓存会让已销毁实例"复活"旧读数）。
    [Fact]
    public void ReadExplodeBlockDefs_AfterDispose_Throws()
    {
        PrepareBridge();
        var doc = new FakeBridgeDoc(new List<string>());
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        var rt = new AcadRuntime();
        try
        {
            rt.ReadExplodeEvidence("16#1116.dwg");
            Assert.NotEmpty(rt.ReadExplodeBlockDefs("16#1116.dwg"));   // 销毁前命中
        }
        finally
        {
            rt.Dispose();
        }

        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeBlockDefs("16#1116.dwg"));
        Assert.Contains("块定义缓存缺失", ex.Message);
    }

    // M-3(b)（修复波1）：读证据成功但 Close 失败 → 整体按失败上抛，块定义缓存**不得**
    // 被本轮读数填充（缓存写入须在 Close 成功之后；否则后续 ReadExplodeBlockDefs 会把
    // 一次失败会话的块定义当有效读数返回）。
    [Fact]
    public void ReadExplodeEvidence_CloseFailure_LeavesBlockDefCacheInvalid()
    {
        PrepareBridge();
        var doc = new FakeBridgeDoc(new List<string>()) { FailClose = true };
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence("16#1116.dwg"));
        Assert.Contains("Close 失败", ex.Message);

        var cacheEx = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeBlockDefs("16#1116.dwg"));
        Assert.Contains("块定义缓存缺失", cacheEx.Message);
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
        WriteRealBridgeMarker(dll);   // I2：占位桥必须含真桥命令标记（否则被判空桩）
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

    // ---- 切片B 复审修复波（I2 空桩识别 / I3 可配路径 / I6 编排超时与缺 REPLACED）----

    /// <summary>占位桥文件：含真桥 READ 命令标记（ASCII）——I2 的空桩扫描据此判"非空桩"。</summary>
    private static void WriteRealBridgeMarker(string path)
        => File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(BridgeChannel.ReadCommand));

    /// <summary>I2：同名空桩产物（只有 NoAcadRefsStub 类型名、无任何真桥命令标记）→ 逐字拒绝，
    /// 绝不当作可加载桥（否则 NETLOAD 会在 AutoCAD 里静默失败/挂起）。</summary>
    [Fact]
    public void BridgeDllPath_StubAssembly_MessageExact()
    {
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        File.WriteAllBytes(dll, System.Text.Encoding.ASCII.GetBytes("NoAcadRefsStub"));
        AcadRuntime.BridgeDllOverride = dll;

        var ex = Assert.Throws<CadRuntimeException>(() => AcadRuntime.BridgeDllPath());

        Assert.Equal("桥未构建（空桩产物，需 CADAGENT_ACMG_DIR 与 AcMgd/AcDbMgd/AcCoreMgd 引用）", ex.Message);
    }

    /// <summary>I2 对照：真桥命令标记（UTF-16 形态亦算）+ 空桩类型名共存 → 判非空桩
    /// （真桥产物元数据里可能同时出现 stub 符号串，故只按"无 TArch 标记且有 stub 标记"判空桩）。</summary>
    [Fact]
    public void BridgeDllPath_RealMarkerPresentButAlsoStubSymbol_NotStub()
    {
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        var bytes = System.Text.Encoding.Unicode.GetBytes(BridgeChannel.ExplodeReplaceCommand)
            .Concat(System.Text.Encoding.ASCII.GetBytes("NoAcadRefsStub")).ToArray();
        File.WriteAllBytes(dll, bytes);
        AcadRuntime.BridgeDllOverride = dll;

        Assert.Equal(dll, AcadRuntime.BridgeDllPath());
    }

    /// <summary>I3：生产可配路径——无覆盖缝时读 CADAGENT_BRIDGE_DLL（测试后还原环境变量）。</summary>
    [Fact]
    public void BridgeDllPath_EnvVar_HonoredWhenNoOverride()
    {
        string dll = Path.Combine(_dir, "deployed", "CadAgent.TArchBridge.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        WriteRealBridgeMarker(dll);
        string? orig = Environment.GetEnvironmentVariable(AcadRuntime.BridgeDllEnvVar);
        AcadRuntime.BridgeDllOverride = null;
        Environment.SetEnvironmentVariable(AcadRuntime.BridgeDllEnvVar, dll);
        try
        {
            Assert.Equal(dll, AcadRuntime.BridgeDllPath());
            Assert.Equal(dll, AcadRuntime.ResolveBridgeDllPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AcadRuntime.BridgeDllEnvVar, orig);
        }
    }

    /// <summary>I1/I2：预检把"缺失"翻译成 dll-missing（覆盖缝指向不存在文件，不启动任何进程）。</summary>
    [Fact]
    public void CheckBridgeAvailability_DllMissing_ReportsProblem()
    {
        AcadRuntime.BridgeDllOverride = Path.Combine(_dir, "nope", "CadAgent.TArchBridge.dll");

        var b = AcadRuntime.CheckBridgeAvailability();

        Assert.False(b.Available);
        Assert.Equal("dll-missing", b.Problem);
        Assert.Contains("CADAGENT_BRIDGE_DLL", b.Guidance);
    }

    /// <summary>I1：桥存在且非空桩但目录不在 TRUSTEDPATHS → untrusted（NETLOAD 会弹安全框）。</summary>
    [Fact]
    public void CheckBridgeAvailability_UntrustedDir_ReportsUntrusted()
    {
        string dll = Path.Combine(_dir, "bridge", "CadAgent.TArchBridge.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        WriteRealBridgeMarker(dll);
        AcadRuntime.BridgeDllOverride = dll;
        AcadRuntime.TrustedPathsSource = _ => @"C:\other";

        var b = AcadRuntime.CheckBridgeAvailability();

        Assert.False(b.Available);
        Assert.Equal("untrusted", b.Problem);
        Assert.Contains("TRUSTEDPATHS", b.Guidance);
    }

    /// <summary>I1 正例：桥目录（或其子目录）在 TRUSTEDPATHS → Available，且 DllPath 为解析结果。</summary>
    [Fact]
    public void CheckBridgeAvailability_TrustedDir_Available()
    {
        string dll = Path.Combine(_dir, "bridge", "CadAgent.TArchBridge.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        WriteRealBridgeMarker(dll);
        AcadRuntime.BridgeDllOverride = dll;
        // 条目带引号（真机实际值形态）+ 环境变量形态都要能判中。
        AcadRuntime.TrustedPathsSource = _ => "\"C:\\nothing\";" + _dir + "\\";

        var b = AcadRuntime.CheckBridgeAvailability();

        Assert.True(b.Available);
        Assert.Null(b.Problem);
        Assert.Equal(dll, b.DllPath);
    }

    /// <summary>信任判定纯函数（I1）：条目本身/子目录/引号/环境变量；不相关目录不受信；空值不受信。</summary>
    [Fact]
    public void IsTrustedDirectory_EntryAndSubdirectory_QuotedAndExpanded()
    {
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:\a", @"C:\a"));
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:\a", @"C:\a\b\c"));
        Assert.True(AcadRuntime.IsTrustedDirectory(@"""C:\a;C:\z""", @"C:\z\bridge"));
        Assert.False(AcadRuntime.IsTrustedDirectory(@"C:\aa", @"C:\a\b"));
        Assert.False(AcadRuntime.IsTrustedDirectory("", @"C:\a"));
        Assert.False(AcadRuntime.IsTrustedDirectory(null, @"C:\a"));
        Assert.True(AcadRuntime.IsTrustedDirectory("%TEMP%", Path.GetTempPath()));
    }

    /// <summary>
    /// M5（PR#30 终审）：盘根条目（<c>C:\</c>/<c>C:/</c>）= **整盘信任**（显式判据：目标同盘即命中，
    /// 不再依赖 "C:" + StartsWith 的隐式归一）；残缺形态 <c>C:</c>（用户手写无分隔符）**不得**
    /// 被 GetFullPath 解析成"该盘当前目录"后悄悄放行——非法条目 fail-closed；普通目录条目口径不变。
    /// </summary>
    [Fact]
    public void IsTrustedDirectory_DriveRootEntry_WholeDriveTrust_BareDriveRefused()
    {
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:\", @"C:\x\y"));      // 根条目→同盘任意路径
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:/", @"c:\x"));        // 正斜杠 + 大小写不敏感
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:\", @"C:\"));         // 目标也是盘根
        Assert.False(AcadRuntime.IsTrustedDirectory(@"C:\", @"D:\x\y"));     // 他盘不受信
        Assert.False(AcadRuntime.IsTrustedDirectory("C:", @"C:\x\y"));       // 残缺形态：不匹配
        // 普通目录条目（回归）：精确/子目录命中，同前缀不命中。
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:\a", @"C:\a"));
        Assert.True(AcadRuntime.IsTrustedDirectory(@"C:\a", @"C:\a\b"));
        Assert.False(AcadRuntime.IsTrustedDirectory(@"C:\a", @"C:\ab"));
    }

    /// <summary>I6：桥命令永不写 .done → 命令等待（BridgeChannel.CommandWait 压到 200ms）超时响亮上抛，
    /// 不挂到生产 120s；失败路径仍只读关闭本文档。</summary>
    [Fact]
    public void ReadExplodeEvidence_CommandNeverDone_ThrowsCommandTimeout()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls) { TimeoutRealRead = true };
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProcessLauncher = _ => new Process();
        AcadAttach.ActiveObjectResolver = _ => app;
        BridgeChannel.CommandWait = TimeSpan.FromMilliseconds(200);

        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence("16#1116.dwg"));

        Assert.Contains("桥命令超时", ex.Message);
        Assert.Contains(BridgeChannel.ReadCommand, ex.Message);
        Assert.Contains("Close(False)", calls);
    }

    /// <summary>I6：桥回 status ok 但缺 REPLACED/VERIFY 行 → 不得空结果扮成功，逐字上抛。</summary>
    [Fact]
    public void ExplodeReplaceByHandle_MissingReplaceRecord_Throws()
    {
        PrepareBridge();
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(calls) { OmitReplaceRecord = true };
        var app = new FakeBridgeApp(doc);
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => app;

        using var rt = new AcadRuntime();
        rt.Open("live.dwg");
        var ex = Assert.Throws<CadRuntimeException>(() =>
            rt.ExplodeReplaceByHandle("21E00", Path.Combine(_dir, "out.live.dwg")));

        Assert.Equal("桥未返回 REPLACED", ex.Message);
    }

    /// <summary>I6 守卫①：无打开文档 → 逐字拒绝（不触通道/不发命令）。</summary>
    [Fact]
    public void ExplodeReplaceByHandle_NoOpenDocument_Throws()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ExplodeReplaceByHandle("21E00", "x.live.dwg"));
        Assert.Contains("无打开的图纸", ex.Message);
    }

    /// <summary>I6 守卫②：空/空白句柄 → 逐字拒绝（空句柄会让桥去炸"空句柄"实体）。</summary>
    [Fact]
    public void ExplodeReplaceByHandle_EmptyHexHandle_Throws()
    {
        using var rt = new AcadRuntime();
        OpenFakeDocument(rt, new FakeBridgeDoc(new List<string>()));
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ExplodeReplaceByHandle(" ", "x.live.dwg"));
        Assert.Contains("无句柄参数", ex.Message);
    }

    /// <summary>I6 守卫③：空/空白 liveout 路径 → 逐字拒绝（桥内 db.SaveAs 空路径无意义）。</summary>
    [Fact]
    public void ExplodeReplaceByHandle_EmptyLiveOut_Throws()
    {
        using var rt = new AcadRuntime();
        OpenFakeDocument(rt, new FakeBridgeDoc(new List<string>()));
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ExplodeReplaceByHandle("21E00", " "));
        Assert.Contains("无 liveout 参数", ex.Message);
    }

    /// <summary>以 COM 假件打开一份"活文档"（守卫用例共用的起步状态）。</summary>
    private static void OpenFakeDocument(AcadRuntime rt, FakeBridgeDoc doc)
    {
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => new FakeBridgeApp(doc);
        rt.Open("live.dwg");
    }

    // 公共编排前奏：桥 DLL 占位 + 固定通道目录（fake 文档经环境变量定位该目录）。
    private string PrepareBridge()
    {
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        WriteRealBridgeMarker(dll);   // I2：占位桥必须含真桥命令标记（否则被判空桩）
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
        /// <summary>正式 READ 永不写 res/.done（I6：命令等待超时分支；探针照常回 fail）。</summary>
        public bool TimeoutRealRead { get; set; }
        /// <summary>EXPLODE_REPLACE 只回 status ok，不写 REPLACED/VERIFY（I6：缺替换记录分支）。</summary>
        public bool OmitReplaceRecord { get; set; }
        /// <summary>M-3(b)（修复波1）：Close 失败注入——读证据收尾需失败上抛且缓存不得被填充。</summary>
        public bool FailClose { get; set; }
        public string Name => "16#1116.dwg";
        public string LastRequest { get; private set; } = "";
        /// <summary>最近一次文档面系统变量读取（GetVariable）的变量名（TRUSTEDPATHS 口径观测）。</summary>
        public string? LastVariableName { get; private set; }
        public string GetVariable(string name)
        {
            LastVariableName = name;
            return "C:\\trusted;C:\\other";
        }
        public void Close(bool save)
        {
            Calls.Add("Close(" + save + ")");
            if (FailClose) throw new InvalidOperationException("fake close failure");
        }

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
            bool probe = req.Contains("dwg\t" + BridgeChannel.ProbeDwg, StringComparison.Ordinal);
            // I6：正式 READ 永不回写结果（探针照常回 fail——否则 EnsureLoaded 先一步判误拒）。
            if (read && !probe && TimeoutRealRead) return;
            string body;
            if (!read)
                body = OmitReplaceRecord
                    ? "status\tok\n"                              // I6：缺 REPLACED/VERIFY
                    : SampleReplace(liveOut: LastLiveOut(req)).Write();
            else if (probe)
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
