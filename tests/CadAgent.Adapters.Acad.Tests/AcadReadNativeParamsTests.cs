using System.Diagnostics;
using System.Runtime.InteropServices;
using CadAgent.Adapters.Acad;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// ReadNativeParams 读参编排单元测试（fake COM 缝，无真机/无 COM）：逐实体读缝注入，
/// 编排路径另注入 ProgId/启动/附着/进程守卫缝——不启动真实 acad.exe、不触 ROT。
/// </summary>
public class AcadReadNativeParamsTests : IDisposable
{
    private readonly Func<dynamic, string?> _origObj = AcadRuntime.ObjectNameReader;
    private readonly Func<dynamic, string, string?> _origParam = AcadRuntime.ParamReader;
    private readonly Func<dynamic, double[]?> _origBbox = AcadRuntime.BboxReader;
    private readonly Func<dynamic, string?> _origLayer = AcadRuntime.LayerReader;
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly Func<string, Type?> _origProgId = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _origCreate = AcadRuntime.CreateComInstance;
    private readonly int _origAttempts = AcadRuntime.StartupRetryAttempts;
    private readonly TimeSpan _origDelay = AcadRuntime.StartupRetryDelay;

    public AcadReadNativeParamsTests()
    {
        // 守卫缝：本机 acad 进程存在与否不影响单测（真机守卫语义由 VendorGuard 自测覆盖）。
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.ObjectNameReader = _origObj;
        AcadRuntime.ParamReader = _origParam;
        AcadRuntime.BboxReader = _origBbox;
        AcadRuntime.LayerReader = _origLayer;
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadAttach.ActiveObjectResolver = _origActive;
        VendorGuard.ProcessNames = _origNames;
        AcadRuntime.ProgIdResolver = _origProgId;
        AcadRuntime.CreateComInstance = _origCreate;
        AcadRuntime.StartupRetryAttempts = _origAttempts;
        AcadRuntime.StartupRetryDelay = _origDelay;
    }

    private static Dictionary<string, string?> _params = new();
    private void Seams(string? objName)
    {
        AcadRuntime.ObjectNameReader = _ => objName;
        AcadRuntime.ParamReader = (_, n) => _params.TryGetValue(n, out var v) ? v : null;
        AcadRuntime.BboxReader = _ => new[] { 0.0, 0, 0, 1, 1, 1 };
        AcadRuntime.LayerReader = _ => "0dt";
    }

    [Fact]
    public void Read_TDbWall_ReadsParams()
    {
        _params = new() { ["LeftWidth"] = "100", ["Height"] = "3000", ["Volume"] = "2.895" };
        Seams("TDbWall");
        var p = AcadRuntime.ReadOneEntity(new object(), "21E00", DateTimeOffset.UtcNow)!;
        Assert.Equal("TDbWall", p.ObjectName);
        Assert.Equal(SemanticClass.Wall, p.Class);
        Assert.Equal("100", p.Params["LeftWidth"]);
        Assert.True(p.ReadOk);
    }

    [Fact]
    public void Read_UnknownNameProperty_RecordsDefaultNotFail()
    {
        _params = new() { ["LeftWidth"] = "100" };  // Height 等缺→UNKNOWNNAME(null)
        Seams("TDbWall");
        var p = AcadRuntime.ReadOneEntity(new object(), "21E00", DateTimeOffset.UtcNow)!;
        Assert.True(p.ReadOk);                       // 单属性缺不致 ReadOk=false
        Assert.False(p.Params.ContainsKey("Height"));
    }

    [Fact]
    public void Read_ObjectNameUnreadable_ReadOkFalse()
    {
        Seams(null);  // ObjectName 不可读（忙窗耗尽）
        var p = AcadRuntime.ReadOneEntity(new object(), "21E00", DateTimeOffset.UtcNow)!;
        Assert.False(p.ReadOk);
    }

    [Fact]
    public void Read_NonTDb_ReturnsNull()
    {
        Seams("AcDbLine");
        Assert.Null(AcadRuntime.ReadOneEntity(new object(), "524C", DateTimeOffset.UtcNow));
    }

    // ---- 公开编排路径（R1 消歧）：null=仅读当前文档；path!=null=自启自开读完即关 ----

    [Fact]
    public void ReadNativeParams_NoOpenDrawing_Throws()
    {
        // _doc==null 且未给路径→不得启实例/开图，直接响亮拒绝。
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadNativeParams());
        Assert.Contains("无打开的图纸", ex.Message);
    }

    /// <summary>假实体：Handle 为标准 COM 属性口径（"21E00" 大写十六进制串）；ObjectName 由
    /// default 缝之外的注入缝读取，故这里仅承载 Handle 与测试侧标识。</summary>
    public sealed class FakeNativeEntity
    {
        public FakeNativeEntity(string handle, string objectName)
        { Handle = handle; ObjectNameForTest = objectName; }
        public string Handle { get; }
        public string ObjectNameForTest { get; }
    }

    /// <summary>假模型空间：按序传实体（Item(i) 索引访问，镜像 COM ModelSpace）。</summary>
    public sealed class FakeNativeModelSpace
    {
        private readonly List<FakeNativeEntity> _entities;
        public FakeNativeModelSpace(params FakeNativeEntity[] entities) => _entities = entities.ToList();
        public int Count => _entities.Count;
        public FakeNativeEntity Item(int index) => _entities[index];
    }

    /// <summary>假文档：记录 Close 调用（钉住"自开自关"）；ModelSpace 供 Gate2 与逐实体遍历。</summary>
    public sealed class FakeNativeDoc
    {
        private readonly List<string> _calls;
        public FakeNativeDoc(FakeNativeModelSpace ms, List<string> calls) { ModelSpace = ms; _calls = calls; }
        public FakeNativeModelSpace ModelSpace { get; }
        public bool Closed { get; private set; }
        public void Close(bool save) { _calls.Add($"Doc.Close({save})"); Closed = true; }
    }

    /// <summary>假 Documents：Gate1 读 Count（≥1）；Open 两种形态——单参（生产 Open 读改写，
    /// native-live 执行窗口用）与双参 ReadOnly（读参路径，复审 finding 3）各记调用串。</summary>
    public sealed class FakeNativeDocuments
    {
        private readonly FakeNativeDoc _doc;
        private readonly List<string> _calls;
        public FakeNativeDocuments(FakeNativeDoc doc, List<string> calls) { _doc = doc; _calls = calls; }
        public int Count => 1;  // /p 启动默认开 Drawing1.dwg（Gate1 就绪条件）
        public FakeNativeDoc Open(string path) { _calls.Add($"Documents.Open({path})"); return _doc; }
        public FakeNativeDoc Open(string path, bool readOnly)
        {
            _calls.Add($"Documents.Open({path}, ReadOnly={readOnly})");
            return _doc;
        }
    }

    /// <summary>假 app：Version/Documents/Visible/Quit（EnsureAppTArch 复用路径不触发 COM 激活）。</summary>
    public sealed class FakeNativeApp
    {
        private readonly FakeNativeDoc _doc;
        private readonly List<string> _calls;
        public FakeNativeApp(FakeNativeDoc doc, List<string> calls) { _doc = doc; _calls = calls; }
        public string Version => "19.1s (LMS Tech)";
        public FakeNativeDocuments Documents => new(_doc, _calls);
        public bool Visible { get; set; }
        public void Quit() => _calls.Add("App.Quit");
    }

    [Fact]
    public void ReadNativeParams_WithPath_LaunchesOpensReadsAndCloses()
    {
        var calls = new List<string>();
        var ms = new FakeNativeModelSpace(
            new FakeNativeEntity("21E00", "TDbWall"),
            new FakeNativeEntity("524C", "AcDbLine"));
        var doc = new FakeNativeDoc(ms, calls);
        var app = new FakeNativeApp(doc, calls);
        AcadRuntime.ProcessLauncher = p => { calls.Add($"Launch({p})"); return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;
        // 逐实体 ObjectName 按实体身份区分（TDb 读参、非 TDb 跳过）；参数/bbox/layer 缝注入。
        _params = new() { ["LeftWidth"] = "100", ["Height"] = "3000", ["Volume"] = "2.895" };
        AcadRuntime.ObjectNameReader = e => ((FakeNativeEntity)e).ObjectNameForTest;
        AcadRuntime.ParamReader = (_, n) => _params.TryGetValue(n, out var v) ? v : null;
        AcadRuntime.BboxReader = _ => new[] { 0.0, 0, 0, 1, 1, 1 };
        AcadRuntime.LayerReader = _ => "0dt";

        using var rt = new AcadRuntime();
        var list = rt.ReadNativeParams("16#1116.dwg");

        var p = Assert.Single(list);                       // 非 TDb（AcDbLine）过滤跳过
        Assert.Equal("21E00", p.Handle);
        Assert.Equal("TDbWall", p.ObjectName);
        Assert.Equal(SemanticClass.Wall, p.Class);
        Assert.Equal("100", p.Params["LeftWidth"]);
        Assert.Equal("0dt", p.Layer);
        Assert.True(p.ReadOk);
        // 自启（/p 天正 profile）→自开→读完即关（顺序钉住；不得残留开文档）。
        // 复审 finding 3：读参原图以 ReadOnly=true 打开（设计承诺只读，不另建副本）。
        Assert.Equal(
            new[] { "Launch(TElec20V5)", "Documents.Open(16#1116.dwg, ReadOnly=True)", "Doc.Close(False)" },
            calls);
        Assert.True(doc.Closed);
    }

    /// <summary>
    /// 复审 finding 3/12 守卫：已有打开文档（native-live 执行窗口）时再走 path 形态必须
    /// 响亮拒绝——不得开第二份覆盖 _doc（前文档失锚、句柄层操作错文档）。
    /// </summary>
    [Fact]
    public void ReadNativeParams_WithPath_WhenDocumentAlreadyOpen_Throws()
    {
        var calls = new List<string>();
        var doc = new FakeNativeDoc(new FakeNativeModelSpace(), calls);
        var app = new FakeNativeApp(doc, calls);
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => app;
        using var rt = new AcadRuntime();
        rt.Open("already-open.dwg");  // 生产 Open 读改写（执行侧），_doc 就位

        var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadNativeParams("16#1116.dwg"));

        Assert.Contains("已有打开的图纸", ex.Message);
        Assert.Contains("ReadNativeParams(path)", ex.Message);
        // 只有执行窗口那一次 Open；path 形态未再开第二份（守卫先于 EnsureAppTArch）。
        Assert.Single(calls, c => c.StartsWith("Documents.Open", StringComparison.Ordinal));
    }

    // ---- ISTQB 合并前审查修复波：Gate2 空图守卫（I2）/读参 HRESULT 双路径（I4）/bbox（Minor 13）----

    /// <summary>
    /// I2：path 形态打开后 ModelSpace.Count==0 → 有界 Gate2 重试（2 次/10ms）耗尽 →
    /// CadRuntimeException("Gate2 超时")；读参失败仍自开自关（不残留开文档）。
    /// </summary>
    [Fact]
    public void ReadNativeParams_WithPath_EmptyModelSpace_Gate2Timeout()
    {
        var calls = new List<string>();
        var doc = new FakeNativeDoc(new FakeNativeModelSpace(), calls); // Count==0 空图
        var app = new FakeNativeApp(doc, calls);
        AcadRuntime.ProcessLauncher = p => { calls.Add($"Launch({p})"); return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var rt = new AcadRuntime();
            var ex = Assert.Throws<CadRuntimeException>(() => rt.ReadNativeParams("empty.dwg"));
            Assert.Contains("Gate2 超时", ex.Message);
        }
        finally
        {
            AcadRuntime.StartupRetryAttempts = _origAttempts;
            AcadRuntime.StartupRetryDelay = _origDelay;
        }
        Assert.True(doc.Closed); // 失败路径自开自关（best-effort 关闭本方文档）
    }

    /// <summary>反射属性面：getter 抛 COMException（经 InvokeMember 必被包装为
    /// TargetInvocationException——考核链式 HRESULT 归类而非直判）。</summary>
    public sealed class ThrowingPropertyEntity
    {
        private readonly int _hresult;
        public ThrowingPropertyEntity(int hresult) => _hresult = hresult;
        public string Height => throw new COMException("fake-param", _hresult);
    }

    /// <summary>反射 bbox 面：与 ReadBbox 同形的 by-ref 出参签名，内部抛 COMException。</summary>
    public sealed class ThrowingBboxEntity
    {
        private readonly int _hresult;
        public ThrowingBboxEntity(int hresult) => _hresult = hresult;
        public void GetBoundingBox(ref object min, ref object max)
            => throw new COMException("fake-bbox", _hresult);
    }

    private static void AssertChainContainsHResult(Exception ex, int hresult)
    {
        bool found = false;
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is COMException com && com.HResult == hresult) { found = true; break; }
        Assert.True(found, $"异常链未含 HRESULT 0x{hresult:X8}: {ex}");
    }

    [Fact]
    public void ParamReader_UnknownNameInsideWrapperChain_ReturnsNull()
    {
        // I4：UNKNOWNNAME(0x80020006) 被反射包装后仍判"属性缺省"→null（不重试）。
        Assert.Null(AcadRuntime.ParamReader(
            new ThrowingPropertyEntity(unchecked((int)0x80020006)), "Height"));
    }

    [Fact]
    public void ParamReader_BusyRejectionSurvivesRetry_PropagatesWithChain()
    {
        // I4：忙拒绝（0x80010001）经 RetryBusy 有界重试仍被拒 → 整读上抛（不得吞成缺省 null）。
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => AcadRuntime.ParamReader(
                new ThrowingPropertyEntity(unchecked((int)0x80010001)), "Height"));
            AssertChainContainsHResult(ex, unchecked((int)0x80010001));
        }
        finally
        {
            AcadRuntime.StartupRetryAttempts = _origAttempts;
            AcadRuntime.StartupRetryDelay = _origDelay;
        }
    }

    [Fact]
    public void BboxReader_UnknownNameInsideWrapperChain_ReturnsNull()
    {
        // Minor 13：bbox 面缺失(0x80020006)→null（spec §4 nullable，ReadOk 仍 true）。
        Assert.Null(AcadRuntime.BboxReader(new ThrowingBboxEntity(unchecked((int)0x80020006))));
    }

    [Fact]
    public void BboxReader_BusyRejectionSurvivesRetry_Propagates()
    {
        // Minor 13：忙拒绝耗尽≠面不存在——整读上抛（空读不得伪装成"无 bbox"）。
        AcadRuntime.StartupRetryAttempts = 2;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                AcadRuntime.BboxReader(new ThrowingBboxEntity(unchecked((int)0x8001010A))));
            AssertChainContainsHResult(ex, unchecked((int)0x8001010A));
        }
        finally
        {
            AcadRuntime.StartupRetryAttempts = _origAttempts;
            AcadRuntime.StartupRetryDelay = _origDelay;
        }
    }
}
