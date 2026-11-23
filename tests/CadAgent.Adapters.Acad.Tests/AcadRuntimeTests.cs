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
}
