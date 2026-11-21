using System.Collections.Concurrent;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad;

/// <summary>
/// 中望 CAD COM 适配器（spec §3/§9）：dynamic COM（ProgID ZWCAD.Application，
/// 不编译期引用厂商 interop DLL——版本碎片化隔离）；所有 COM 调用经专用 STA 线程
/// 任务队列串行化；崩溃→CadRuntimeException，Restart 重建。
/// 真机验证：ZWCAD 2025（2026-11-20 探针 + 本适配器集成测试）。
/// </summary>
public sealed class ZwcadRuntime : ICadRuntime
{
    private const string ProgId = "ZWCAD.Application";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _staThread;
    private dynamic? _app;
    private dynamic? _doc;
    private bool _disposed;

    public ZwcadRuntime()
    {
        _staThread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
    }

    private T Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void Run(Action a) => Run<object?>(() => { a(); return null; });

    private dynamic EnsureApp()
    {
        if (_app == null)
        {
            var type = Type.GetTypeFromProgID(ProgId)
                ?? throw new CadRuntimeException($"ZWCAD COM 未注册: {ProgId}");
            _app = Activator.CreateInstance(type)
                ?? throw new CadRuntimeException("ZWCAD COM 实例创建失败");
            _app.Visible = false;
        }
        return _app;
    }

    public RuntimeInfo GetInfo() => Run(() =>
    {
        if (Type.GetTypeFromProgID(ProgId) == null)
            return new RuntimeInfo("ZWCAD", "", false);
        string version = EnsureApp().Version;
        return new RuntimeInfo("ZWCAD", version, true);
    });

    public void Open(string drawingPath) => Run(() =>
    {
        try { _doc = EnsureApp().Documents.Open(drawingPath); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"打开图纸失败: {ex.Message}", ex); }
    });

    public void SaveAs(string outputPath) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        try { _doc.SaveAs(outputPath); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"SaveAs 失败: {ex.Message}", ex); }
    });

    public void Close(bool save) => Run(() =>
    {
        if (_doc != null) { _doc.Close(save); _doc = null; }
    });

    public void Restart() => Run(() =>
    {
        try { _app?.Quit(); } catch { /* 崩溃后 Quit 可能失败，忽略 */ }
        _app = null;
        _doc = null;
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Run(() =>
            {
                try { _app?.Quit(); } catch { /* 退出时忽略 */ }
                _app = null;
                _doc = null;
            });
        }
        catch { /* 队列已关闭等 */ }
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(10));
    }
}