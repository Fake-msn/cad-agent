using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// ZwcadRuntime 单元测试（无 COM，无 RequiresZwcad 门控）：ProgIdResolver/CreateComInstance
/// seam 注入（PR#18 合并前修复波 Important#1，与 AcadRuntimeTests 同款）。
/// 钉住 Close COM 失败→CadRuntimeException 包装契约（ExportService 只捕该型：
/// Restart 重试 + export-failed 审计依赖；裸 COMException 逃逸会静默旁路），
/// 顺带钉住 Close-before-Quit 顺序（CUIX 竞态根因）。
/// 静态 seam 用完即还原（本程序集 runner.json 已禁集合并行）。
/// </summary>
public class ZwcadRuntimeUnitTests : IDisposable
{
    private readonly Func<string, Type?> _originalResolver = ZwcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _originalCreate = ZwcadRuntime.CreateComInstance;
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;

    public ZwcadRuntimeUnitTests()
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        ZwcadRuntime.ProgIdResolver = _originalResolver;
        ZwcadRuntime.CreateComInstance = _originalCreate;
        VendorGuard.ProcessNames = _originalNames;
    }

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
        ZwcadRuntime.ProgIdResolver = _ => typeof(object);
        ZwcadRuntime.CreateComInstance = _ => new FakeApp(calls, new FakeDoc(calls, closeThrows: true));
        using var rt = new ZwcadRuntime();
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
        ZwcadRuntime.ProgIdResolver = _ => typeof(object);
        ZwcadRuntime.CreateComInstance = _ => new FakeApp(calls, new FakeDoc(calls, closeThrows: false));
        using var rt = new ZwcadRuntime();
        rt.Open("fake.dwg");
        rt.Restart();
        // 带开文档 Quit 会被 ZWCAD 判异常终止→下次启动弹修复对话框；顺序不可回退。
        Assert.Equal(new[] { "Doc.Close", "App.Quit" }, calls);
    }

    [Fact]
    public void Dispose_ClosesDocBeforeQuit()
    {
        var calls = new List<string>();
        ZwcadRuntime.ProgIdResolver = _ => typeof(object);
        ZwcadRuntime.CreateComInstance = _ => new FakeApp(calls, new FakeDoc(calls, closeThrows: false));
        using (var rt = new ZwcadRuntime())
        {
            rt.Open("fake.dwg");
        }
        Assert.Equal(new[] { "Doc.Close", "App.Quit" }, calls);
    }
}
