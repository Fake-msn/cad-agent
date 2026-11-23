using System.Reflection;
using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AcadRuntime 单元测试（无 COM）：ProgIdResolver/CreateComInstance seam 注入。
/// 本机事实：AutoCAD 孤儿 ProgID 注册（CLSID 无 LocalServer32）→ 激活必败 →
/// GetInfo 必须优雅返回 Available=false（不得抛挂 CLI）。
/// </summary>
public class AcadRuntimeTests : IDisposable
{
    private readonly Func<string, Type?> _originalResolver = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _originalCreate = AcadRuntime.CreateComInstance;
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;

    public AcadRuntimeTests()
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.ProgIdResolver = _originalResolver;
        AcadRuntime.CreateComInstance = _originalCreate;
        VendorGuard.ProcessNames = _originalNames;
    }

    [Fact]
    public void GetInfo_ProgIdMissing_ReturnsUnavailable()
    {
        AcadRuntime.ProgIdResolver = _ => null;
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.Equal("AutoCAD", info.Vendor);
        Assert.False(info.Available);
        Assert.Equal("", info.Version);
    }

    [Fact]
    public void GetInfo_ActivationThrows_ReturnsUnavailable()
    {
        // 孤儿注册场景：ProgID 在但 COM 服务器不存在
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => throw new COMException("没有注册类 (0x80040154)");
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.False(info.Available);
    }

    [Fact]
    public void SaveAs_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.SaveAs("x.dwg"));
        Assert.Contains("无打开的图纸", ex.Message);
    }

    [Fact]
    public void EntityCount_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        Assert.Throws<CadRuntimeException>(() => rt.EntityCount());
    }

    [Fact]
    public void EnsureBlock_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        Assert.Throws<CadRuntimeException>(() => rt.EnsureBlock("B", Array.Empty<BlockPrimitive>()));
    }

    [Fact]
    public void Dispose_WaitsForAcadProcessExit()
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { "acad" } : Array.Empty<string>();
        var originalExit = VendorGuard.ExitWait;
        VendorGuard.ExitWait = TimeSpan.FromSeconds(5);
        try
        {
            using (var rt = new AcadRuntime()) { }
            Assert.True(calls >= 3);
        }
        finally { VendorGuard.ExitWait = originalExit; }
    }

    // ---- PR#18 合并前修复波 Important#1：Close COM 失败包装回归钉住 ----
    // ExportService 只捕 CadRuntimeException（Restart 重试 + export-failed 审计依赖此类型契约）；
    // 裸 COMException 逃逸会静默跳过重试与审计。以下测试经 COM 激活 seam 注入假 app/doc，
    // 钉住包装类型 + 消息 + InnerException，顺带钉住 Close-before-Quit 顺序（CUIX 根因 a）。
    // 假类型必须 public：dynamic 绑定从适配器程序集发起，internal 成员不可达。

    /// <summary>假文档：Close 可配置抛 COMException（模拟厂商 COM 崩溃）；记录调用序。</summary>
    public sealed class FakeDoc
    {
        private readonly List<string> _calls;
        private readonly bool _closeThrows;
        public FakeDoc(List<string> calls, bool closeThrows) { _calls = calls; _closeThrows = closeThrows; }
        public void Close(bool save)
        {
            _calls.Add("Doc.Close");
            if (_closeThrows) throw new COMException("文档关闭失败 (0x8000FFFF)");
        }
    }

    /// <summary>假 Documents 集合：Open 返回假文档。</summary>
    public sealed class FakeDocuments
    {
        private readonly FakeDoc _doc;
        public FakeDocuments(FakeDoc doc) => _doc = doc;
        public FakeDoc Open(string path) => _doc;
    }

    /// <summary>假 app：Visible 可写；Quit 记录调用序（钉住 Close-before-Quit）。</summary>
    public sealed class FakeApp
    {
        private readonly List<string> _calls;
        private readonly FakeDoc _doc;
        public FakeApp(List<string> calls, FakeDoc doc) { _calls = calls; _doc = doc; }
        public bool Visible { get; set; }
        public FakeDocuments Documents => new(_doc);
        public void Quit() => _calls.Add("App.Quit");
    }

    [Fact]
    public void Close_ComFailure_ThrowsCadRuntimeException()
    {
        var calls = new List<string>();
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => new FakeApp(calls, new FakeDoc(calls, closeThrows: true));
        using var rt = new AcadRuntime();
        rt.Open("fake.dwg");
        var ex = Assert.Throws<CadRuntimeException>(() => rt.Close(false));
        Assert.Contains("Close 失败", ex.Message);
        var com = Assert.IsType<COMException>(ex.InnerException);
        Assert.Contains("0x8000FFFF", com.Message);
        // Close 失败不得清空文档引用：Restart 仍先尝试 Close 再 Quit（保留引用供重试 + 顺序不变量）。
        rt.Restart();
        Assert.Equal(new[] { "Doc.Close", "Doc.Close", "App.Quit" }, calls);
    }

    [Fact]
    public void Restart_ClosesDocBeforeQuit()
    {
        var calls = new List<string>();
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => new FakeApp(calls, new FakeDoc(calls, closeThrows: false));
        using var rt = new AcadRuntime();
        rt.Open("fake.dwg");
        rt.Restart();
        // 带开文档 Quit 会被判异常终止→下次启动弹 CUIX 修复对话框；顺序不可回退。
        Assert.Equal(new[] { "Doc.Close", "App.Quit" }, calls);
    }

    [Fact]
    public void Dispose_ClosesDocBeforeQuit()
    {
        var calls = new List<string>();
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => new FakeApp(calls, new FakeDoc(calls, closeThrows: false));
        using (var rt = new AcadRuntime())
        {
            rt.Open("fake.dwg");
        }
        Assert.Equal(new[] { "Doc.Close", "App.Quit" }, calls);
    }

    // ---- 真机核定修复（2026-11-24，AutoCAD 2014 R19.1）：启动窗口 COM 拒绝重试 ----
    // 真机诊断实证（outputs/autocad-verify-20261123/r2-diag-activation-steps.txt）：
    // CreateInstance 返回≠初始化完成，紧随的 Visible=false 抛 RPC_E_CALL_REJECTED
    // (0x80010001)，约 0.4s 后调用即恢复。适配器须有界重试等待就绪，而非判不可用。

    /// <summary>假 app：前 N 次 Visible 写入抛 RPC_E_CALL_REJECTED（模拟真机启动窗口）。</summary>
    public sealed class StartupRejectApp
    {
        private int _remaining;
        public StartupRejectApp(int rejects) { _remaining = rejects; }
        public bool Visible
        {
            set
            {
                if (_remaining > 0)
                {
                    _remaining--;
                    throw new COMException("Call was rejected by callee. (0x80010001)", unchecked((int)0x80010001));
                }
            }
        }
        public string Version => "19.1s (LMS Tech)";
        public void Quit() { }
    }

    [Fact]
    public void GetInfo_StartupCallRejected_RetriesUntilReady()
    {
        var originalDelay = AcadRuntime.StartupRetryDelay;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            AcadRuntime.ProgIdResolver = _ => typeof(object);
            AcadRuntime.CreateComInstance = _ => new StartupRejectApp(3);
            using var rt = new AcadRuntime();
            var info = rt.GetInfo();
            Assert.True(info.Available);
            Assert.Equal("19.1s (LMS Tech)", info.Version);
        }
        finally { AcadRuntime.StartupRetryDelay = originalDelay; }
    }

    [Fact]
    public void GetInfo_StartupRejectionNeverClears_BoundedUnavailable()
    {
        var originalAttempts = AcadRuntime.StartupRetryAttempts;
        var originalDelay = AcadRuntime.StartupRetryDelay;
        AcadRuntime.StartupRetryAttempts = 3;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            AcadRuntime.ProgIdResolver = _ => typeof(object);
            AcadRuntime.CreateComInstance = _ => new StartupRejectApp(int.MaxValue);
            using var rt = new AcadRuntime();
            var info = rt.GetInfo();
            // 有界重试耗尽→优雅降级 Available=false（不得无限挂起）。
            Assert.False(info.Available);
        }
        finally
        {
            AcadRuntime.StartupRetryAttempts = originalAttempts;
            AcadRuntime.StartupRetryDelay = originalDelay;
        }
    }

    // ---- 真机核定修复（2026-11-24）：消息过滤器双拒绝型有界重试 ----
    // 真机实证（outputs/autocad-verify-20261123/f6-live.txt）：Open DXF 后紧随的 SaveAs
    // 被拒 0x80010001（GetIDsOfNames 层→MissingMemberException），dwRejectType=
    // SERVERCALL_ISHANDLED(0)；旧实现只重试 RETRYLATER(2)→漏掉此形态。钉住判定逻辑。
    [Theory]
    [InlineData(0)] // SERVERCALL_ISHANDLED：拒绝但稍后可能接受（真机 SaveAs 形态）
    [InlineData(2)] // SERVERCALL_RETRYLATER：服务器忙
    public void MessageFilter_Rejection_BothTypes_RetryWithinBound(int rejectType)
    {
        var filter = CreateMessageFilter();
        // 新近调用（tick=现在）→ 返回重试延迟（>0），不得取消。
        int result = InvokeRetryRejectedCall(filter, Environment.TickCount, rejectType);
        Assert.True(result > 0, $"拒绝型 {rejectType} 应有界重试，实际返回 {result}");
    }

    [Fact]
    public void MessageFilter_Rejection_BoundExceeded_Cancels()
    {
        var filter = CreateMessageFilter();
        // 发起 tick 已过去 31s（>30s 上限）→ 取消（-1），拒绝上抛由上层降级。
        int result = InvokeRetryRejectedCall(filter, Environment.TickCount - 31_000, 2);
        Assert.Equal(-1, result);
    }

    private static object CreateMessageFilter()
    {
        var t = typeof(ComMessageFilter).GetNestedType("Filter", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(t)!;
    }

    private static int InvokeRetryRejectedCall(object filter, int tick, int rejectType)
    {
        var m = filter.GetType().GetMethod("RetryRejectedCall")!;
        return (int)m.Invoke(filter, new object[] { IntPtr.Zero, tick, rejectType })!;
    }

    // ---- 真机核定修复（2026-11-24）：Run 调度层应用级忙拒绝有界重试 ----
    // 真机实证（f9-live.txt）：Open 后紧随的 SaveAs 被 AutoCAD IDispatch 以调用结果
    // 形式返回 RPC_E_CALL_REJECTED——消息过滤器（RPC 层）兜不住。Run 层统一重试；
    // 拒绝=调用未执行，重试无副作用重复。非忙错误不得重试（不吞诊断、不放大故障）。

    /// <summary>假文档：前 N 次 SaveAs 抛指定 HRESULT 的 COMException；记录调用次数。</summary>
    public sealed class BusySaveAsDoc
    {
        private int _remaining;
        private readonly int _hresult;
        public int SaveAsCalls;
        public BusySaveAsDoc(int rejects, int hresult) { _remaining = rejects; _hresult = hresult; }
        public void SaveAs(string path)
        {
            SaveAsCalls++;
            if (_remaining > 0) { _remaining--; throw new COMException("忙拒绝模拟", _hresult); }
        }
        public void Close(bool save) { }
    }

    public sealed class BusySaveAsDocuments
    {
        private readonly BusySaveAsDoc _doc;
        public BusySaveAsDocuments(BusySaveAsDoc doc) => _doc = doc;
        public BusySaveAsDoc Open(string path) => _doc;
    }

    public sealed class BusySaveAsApp
    {
        private readonly BusySaveAsDoc _doc;
        public BusySaveAsApp(BusySaveAsDoc doc) => _doc = doc;
        public bool Visible { get; set; }
        public BusySaveAsDocuments Documents => new(_doc);
        public void Quit() { }
    }

    [Fact]
    public void SaveAs_BusyRejected_RetriesUntilAccepted()
    {
        var originalDelay = AcadRuntime.StartupRetryDelay;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var doc = new BusySaveAsDoc(3, unchecked((int)0x80010001));
            AcadRuntime.ProgIdResolver = _ => typeof(object);
            AcadRuntime.CreateComInstance = _ => new BusySaveAsApp(doc);
            using var rt = new AcadRuntime();
            rt.Open("fake.dxf");
            rt.SaveAs("out.dwg"); // 前 3 次被拒→第 4 次成功，不得上抛
            Assert.Equal(4, doc.SaveAsCalls);
        }
        finally { AcadRuntime.StartupRetryDelay = originalDelay; }
    }

    [Fact]
    public void SaveAs_NonBusyComFailure_NotRetried()
    {
        var doc = new BusySaveAsDoc(int.MaxValue, unchecked((int)0x8000FFFF));
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => new BusySaveAsApp(doc);
        using var rt = new AcadRuntime();
        rt.Open("fake.dxf");
        var ex = Assert.Throws<CadRuntimeException>(() => rt.SaveAs("out.dwg"));
        Assert.Contains("SaveAs 失败", ex.Message);
        Assert.Equal(1, doc.SaveAsCalls); // 非忙错误一次即止
    }

    /// <summary>假 app：前 N 次 Quit 抛 RPC_E_CALL_REJECTED（模拟退出窗口忙拒绝）。</summary>
    public sealed class BusyQuitApp
    {
        private int _remaining;
        public int QuitCalls;
        public BusyQuitApp(int rejects) { _remaining = rejects; }
        public bool Visible { get; set; }
        public string Version => "19.1s (LMS Tech)";
        public void Quit()
        {
            QuitCalls++;
            if (_remaining > 0) { _remaining--; throw new COMException("忙拒绝模拟", unchecked((int)0x80010001)); }
        }
    }

    [Fact]
    public void Dispose_QuitBusyRejected_RetriesUntilAccepted()
    {
        // 真机实证（f12/f13）：Dispose 内层全吞忙拒绝→Quit 未生效→进程残留→守卫拒绝。
        // 钉住：Quit 忙拒绝必须重试至接受（不得一次即吞）。
        var originalDelay = AcadRuntime.StartupRetryDelay;
        AcadRuntime.StartupRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var app = new BusyQuitApp(2);
            AcadRuntime.ProgIdResolver = _ => typeof(object);
            AcadRuntime.CreateComInstance = _ => app;
            using (var rt = new AcadRuntime()) { rt.GetInfo(); }
            Assert.Equal(3, app.QuitCalls);
        }
        finally { AcadRuntime.StartupRetryDelay = originalDelay; }
    }
}
