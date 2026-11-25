using System.Diagnostics;
using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 桥通道单元测试（fake 缝，无真机/无 COM）：
/// 请求编号、.done 轮询、NETLOAD 看门狗（超时/指引一次）、注册探针握手（成功/误拒）、
/// DTO→Core 映射，以及 ReadExplodeEvidence 全编排（fake 文档在通道目录内扮演桥）。
/// </summary>
public class BridgeChannelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("chan").FullName;
    private readonly Func<string, string, string> _origSend = BridgeChannel.SendCommand;
    private readonly TimeSpan _origProbe = BridgeChannel.ProbeTimeout;
    private readonly TimeSpan _origPoll = BridgeChannel.PollInterval;
    private readonly Action<TimeSpan, Action> _origSchedule = BridgeChannel.ScheduleGuidance;
    private readonly Func<string> _origDirFactory = AcadRuntime.ChannelDirFactory;
    private readonly string? _origDll = AcadRuntime.BridgeDllOverride;
    private readonly Func<int, bool> _origDialog = AcadRuntime.SecurityDialogDetector;
    private readonly Action<string>? _origGuidance = AcadRuntime.NetLoadSlowGuidance;
    private readonly TimeSpan _origNetLoadTimeout = AcadRuntime.NetLoadTimeout;
    private readonly TimeSpan _origThreshold = AcadRuntime.NetLoadNoProgressThreshold;
    private readonly TimeSpan _origCmdTimeout = AcadRuntime.BridgeCommandTimeout;
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly Func<string, Type?> _origProgId = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _origCreate = AcadRuntime.CreateComInstance;

    public BridgeChannelTests()
    {
        // 确定性钉住：探针/轮询预算压到毫秒级；窗口/进程守卫缝不触真机。
        BridgeChannel.ProbeTimeout = TimeSpan.FromMilliseconds(150);
        BridgeChannel.PollInterval = TimeSpan.FromMilliseconds(10);
        AcadRuntime.BridgeCommandTimeout = TimeSpan.FromSeconds(2);
        AcadRuntime.SecurityDialogDetector = _ => false;
        AcadRuntime.NetLoadSlowGuidance = null;
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        BridgeChannel.SendCommand = _origSend;
        BridgeChannel.ProbeTimeout = _origProbe;
        BridgeChannel.PollInterval = _origPoll;
        BridgeChannel.ScheduleGuidance = _origSchedule;
        AcadRuntime.ChannelDirFactory = _origDirFactory;
        AcadRuntime.BridgeDllOverride = _origDll;
        AcadRuntime.SecurityDialogDetector = _origDialog;
        AcadRuntime.NetLoadSlowGuidance = _origGuidance;
        AcadRuntime.NetLoadTimeout = _origNetLoadTimeout;
        AcadRuntime.NetLoadNoProgressThreshold = _origThreshold;
        AcadRuntime.BridgeCommandTimeout = _origCmdTimeout;
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadAttach.ActiveObjectResolver = _origActive;
        VendorGuard.ProcessNames = _origNames;
        AcadRuntime.ProgIdResolver = _origProgId;
        AcadRuntime.CreateComInstance = _origCreate;
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

    [Fact]
    public void WaitDone_TimesOut_ReturnsNull()
        => Assert.Null(BridgeChannel.WaitDone(_dir, "9", TimeSpan.FromMilliseconds(100)));

    // ---- NETLOAD 看门狗 ----

    [Fact]
    public void EnsureNetLoad_WatchdogNeverProgresses_ThrowsTimeout()
    {
        BridgeChannel.SendCommand = (_, _) => "";   // 只发送、不产进展
        var ex = Assert.Throws<CadRuntimeException>(() =>
            BridgeChannel.EnsureNetLoad("b.dll", _dir, _ => false, TimeSpan.FromMilliseconds(120)));
        Assert.Contains("NETLOAD 超时", ex.Message);
    }

    [Fact]
    public void EnsureNetLoad_SlowGuidance_InvokedOnceWithTrustedPathsHint()
    {
        BridgeChannel.ScheduleGuidance = (_, action) => action();   // 立即执行（确定性）
        BridgeChannel.SendCommand = (_, _) => "";
        var guided = new List<string>();

        BridgeChannel.EnsureNetLoad(
            @"C:\bridge\CadAgent.TArchBridge.dll", _dir, _ => true, TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1), guided.Add);

        string text = Assert.Single(guided);
        Assert.Contains("TRUSTEDPATHS", text);
        Assert.Contains(@"C:\bridge", text);
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

    [Fact]
    public void EnsureLoaded_ProbeTimeout_ThrowsDenied()
    {
        BridgeChannel.SendCommand = (_, _) => "";   // NETLOAD 被拒：命令未注册，探针无 done
        var ex = Assert.Throws<CadRuntimeException>(() =>
            BridgeChannel.EnsureLoaded("b.dll", _dir, _ => true, TimeSpan.FromSeconds(1)));
        Assert.Contains("NETLOAD 被拒绝", ex.Message);
        Assert.Contains("TRUSTEDPATHS", ex.Message);
    }

    private static void RespondFail(string channelDir)
    {
        string? id = LatestPendingId(channelDir);
        if (id == null) return;
        File.WriteAllText(Path.Combine(channelDir, "res-" + id + ".txt"),
            "status\tfail\nerror\tInvalidOperationException: 错图\n");
        File.WriteAllText(Path.Combine(channelDir, "res-" + id + ".done"), "1");
    }

    private static string? LatestPendingId(string channelDir)
    {
        string? best = null;
        int bestId = -1;
        foreach (var f in Directory.GetFiles(channelDir, "req-*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length <= 4 || !int.TryParse(name.Substring(4), out int n)) continue;
            if (File.Exists(Path.Combine(channelDir, "res-" + name.Substring(4) + ".done"))) continue;
            if (n > bestId) { bestId = n; best = name.Substring(4); }
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
    public void ReadExplodeEvidence_LaunchesOpensReadsClosesAndCleansChannel()
    {
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        File.WriteAllText(dll, "stub");
        AcadRuntime.BridgeDllOverride = dll;
        AcadRuntime.ChannelDirFactory = () => Path.Combine(_dir, "channel");

        var calls = new List<string>();
        var doc = new FakeBridgeDoc(Path.Combine(_dir, "channel"), calls);
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
        // 通道目录清理（临时目录不残留协议文件）。
        Assert.False(Directory.Exists(Path.Combine(_dir, "channel")));
    }

    [Fact]
    public void ReadExplodeEvidence_SecondCall_SkipsNetLoad()
    {
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        File.WriteAllText(dll, "stub");
        AcadRuntime.BridgeDllOverride = dll;
        AcadRuntime.ChannelDirFactory = () => Path.Combine(_dir, "channel");

        var calls = new List<string>();
        var doc = new FakeBridgeDoc(Path.Combine(_dir, "channel"), calls);
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

    [Fact]
    public void ReadExplodeEvidence_WhenDocumentAlreadyOpen_ThrowsGuard()
    {
        var calls = new List<string>();
        var doc = new FakeBridgeDoc(Path.Combine(_dir, "channel"), calls);
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
        string dll = Path.Combine(_dir, "CadAgent.TArchBridge.dll");
        File.WriteAllText(dll, "stub");
        AcadRuntime.BridgeDllOverride = dll;
        AcadRuntime.ChannelDirFactory = () => Path.Combine(_dir, "channel");

        var calls = new List<string>();
        var doc = new FakeBridgeDoc(Path.Combine(_dir, "channel"), calls);
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
        Assert.False(Directory.Exists(Path.Combine(_dir, "channel")));
    }

    [Fact]
    public void ReadExplodeEvidence_NullPath_Throws()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadExplodeEvidence(null!));
        Assert.Contains("无打开的图纸参数", ex.Message);
    }

    /// <summary>假文档：SendCommand 扮演桥——NETLOAD 记一笔；READ 按最新待处理请求回写结果。</summary>
    public sealed class FakeBridgeDoc
    {
        private readonly string _channel;
        public FakeBridgeDoc(string channel, List<string> calls) { _channel = channel; Calls = calls; }
        public List<string> Calls { get; }
        public string Name => "16#1116.dwg";
        public string LastRequest { get; private set; } = "";
        public void Close(bool save) => Calls.Add("Close(" + save + ")");

        public void SendCommand(string command)
        {
            string cmd = command.TrimEnd('\r', '\n');
            if (cmd.StartsWith("(command \"NETLOAD\"", StringComparison.Ordinal))
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
            string? id = LatestPendingId(_channel);
            if (id == null) return;
            string req = File.ReadAllText(Path.Combine(_channel, "req-" + id + ".txt"));
            LastRequest = req;
            string body;
            if (!read)
                body = SampleReplace(liveOut: LastLiveOut(req)).Write();
            else if (req.Contains("dwg\t" + BridgeChannel.ProbeDwg, StringComparison.Ordinal))
                body = "status\tfail\nerror\tInvalidOperationException: 错图\n";
            else
                body = SampleOk().Write();
            File.WriteAllText(Path.Combine(_channel, "res-" + id + ".txt"), body);
            File.WriteAllText(Path.Combine(_channel, "res-" + id + ".done"), "1");
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

    /// <summary>假 app：EnsureAppTArch 的 Gate1（Version/Documents.Count）与 Open 走此面。</summary>
    public sealed class FakeBridgeApp
    {
        private readonly FakeBridgeDoc _doc;
        public FakeBridgeApp(FakeBridgeDoc doc) => _doc = doc;
        public string Version => "19.1s (LMS Tech)";
        public FakeBridgeDocuments Documents => new(_doc);
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
