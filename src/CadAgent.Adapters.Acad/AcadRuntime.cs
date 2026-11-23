using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// AutoCAD COM 适配器（spec §3/§11）：dynamic COM（ProgID AutoCAD.Application
/// 版本无关→CurVer；不编译期引用厂商 interop DLL）；所有 COM 调用经专用 STA 线程
/// 任务队列串行化；崩溃→CadRuntimeException，Restart 重建。
/// 状态：CompatibleUnverified——开发机无 AutoCAD（孤儿注册），真机验证按
/// docs/verification/autocad-adapter-protocol.md 在工作环境执行。
/// 与 ZwcadRuntime 同源结构；差异经 VendorProfiles.Acad 参数化
/// （Blocks.Add 实测序 origin 先，2026-11-24 真机探针核定，与文档相反）。
/// </summary>
public sealed class AcadRuntime : ICadRuntime
{
    private static readonly VendorProfile Profile = VendorProfiles.Acad;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _staThread;
    private dynamic? _app;
    private dynamic? _doc;
    private bool _disposed;

    internal static Func<string, Type?> ProgIdResolver = Type.GetTypeFromProgID;
    internal static Func<Type, object?> CreateComInstance = t => Activator.CreateInstance(t);

    // 真机核定（2026-11-24，AutoCAD 2014 R19.1）：CreateInstance 返回≠初始化完成，
    // 启动窗口内 COM 调用抛 RPC_E_CALL_REJECTED(0x80010001)/RPC_E_SERVERCALL_RETRYLATER(0x8001010A)
    // （实测约 0.4s 后恢复）。有界重试等待就绪；默认上限 30s 防挂死，耗尽→上层优雅降级。
    internal static int StartupRetryAttempts = 60;
    internal static TimeSpan StartupRetryDelay = TimeSpan.FromMilliseconds(500);

    public AcadRuntime()
    {
        _staThread = new Thread(() =>
        {
            // 启动窗口 COM 拒绝在消息过滤器层自动重试（真机核定，见 ComMessageFilter 注释）。
            ComMessageFilter.Register();
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
            try { tcs.SetResult(RetryBusy(f)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    // 应用层忙拒绝有界重试（真机核定 2026-11-24）：AutoCAD 忙时（Open 后再生窗口等）
    // IDispatch::Invoke 直接把 RPC_E_CALL_REJECTED(0x80010001)/RETRYLATER(0x8001010A)
    // 作为调用结果返回——RPC 层消息过滤器兜不住此形态（f9 实证：SaveAs 仍被拒）。
    // 拒绝=调用未被执行，重试无副作用重复之虞；非忙错误立即上抛不吞诊断。
    // 操作内层已把 COMException 包装为 CadRuntimeException→沿 InnerException 链判定。
    private static T RetryBusy<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (Exception ex) when (IsBusyRejection(ex) && i < StartupRetryAttempts - 1)
            {
                Thread.Sleep(StartupRetryDelay);
            }
        }
    }

    private static bool IsBusyRejection(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is COMException com &&
                (com.HResult == unchecked((int)0x80010001) || com.HResult == unchecked((int)0x8001010A)))
                return true;
            // GetIDsOfNames 层拒绝被 C# 绑定器包装为 MissingMemberException（消息含 HRESULT）。
            if (e is MissingMemberException mm &&
                (mm.Message.Contains("0x80010001") || mm.Message.Contains("0x8001010A")))
                return true;
        }
        return false;
    }

    private void Run(Action a) => Run<object?>(() => { a(); return null; });

    private dynamic EnsureApp()
    {
        if (_app == null)
        {
            VendorGuard.EnsureNoForeignInstance(Profile);
            var type = ProgIdResolver(Profile.ProgId)
                ?? throw new CadRuntimeException($"AutoCAD COM 未注册: {Profile.ProgId}");
            try
            {
                _app = CreateComInstance(type)
                    ?? throw new CadRuntimeException("AutoCAD COM 实例创建失败");
            }
            catch (Exception ex) when (ex is not CadRuntimeException)
            {
                throw new CadRuntimeException(
                    $"AutoCAD COM 激活失败（ProgID 可能为孤儿注册，无 LocalServer32）: {ex.Message}", ex);
            }
            SetVisibleStartupReady();
        }
        return _app;
    }

    // 启动窗口拒绝重试：仅对 RPC_E_CALL_REJECTED/RPC_E_SERVERCALL_RETRYLATER 重试，
    // 其他 COM 错误（孤儿注册/许可失败等）立即上抛，不吞诊断信息。
    private void SetVisibleStartupReady()
    {
        for (int i = 0; ; i++)
        {
            try { _app!.Visible = false; return; }
            catch (COMException ex) when (
                (ex.HResult == unchecked((int)0x80010001) || ex.HResult == unchecked((int)0x8001010A))
                && i < StartupRetryAttempts - 1)
            {
                Thread.Sleep(StartupRetryDelay);
            }
        }
    }

    // 孤儿注册/激活失败/守卫超时均优雅降级 Available=false（CLI 据此走 DXF 降级+警示）。
    // 守卫超时（已有实例运行）也报不可用→降级，安全侧选择；真机协议注记此语义。
    public RuntimeInfo GetInfo() => Run(() =>
    {
        if (ProgIdResolver(Profile.ProgId) == null)
            return new RuntimeInfo("AutoCAD", "", false);
        try
        {
            string version = EnsureApp().Version;
            return new RuntimeInfo("AutoCAD", version, true);
        }
        catch
        {
            return new RuntimeInfo("AutoCAD", "", false);
        }
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
        // COM 失败统一 CadRuntimeException（与 SaveAs 同款）；
        // 仅 Close 成功后清空 _doc——失败时文档可能仍开，保留引用供重试/Restart。
        try { if (_doc != null) { _doc.Close(save); _doc = null; } }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"Close 失败: {ex.Message}", ex); }
    });

    public void Restart() => Run(() =>
    {
        // 先关文档再 Quit：带开文档 Quit 会被判异常终止→下次启动弹 CUIX 修复对话框（根因 a）。
        // 忙拒绝（0x80010001/0x8001010A）须上抛给 RetryBusy 重试——真机核定（f12/f13）：
        // 内层全吞会把忙拒绝当崩溃忽略→Quit 未生效→进程残留→下一测试守卫拒绝。
        try { _doc?.Close(false); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 崩溃后 Close 可能失败，忽略 */ }
        _doc = null;
        try { _app?.Quit(); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 崩溃后 Quit 可能失败，忽略 */ }
        _app = null;
    });

    private dynamic ModelSpace()
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        return _doc.ModelSpace;
    }

    private static double[] Pt(double x, double y) => new[] { x, y, 0.0 };

    private void EnsureLayer(string layer)
    {
        try { dynamic l = _doc.Layers.Item(layer); if (l != null) return; }
        catch { /* 不存在→创建 */ }
        _doc.Layers.Add(layer);
    }

    private void SetLayer(dynamic e, string? layer)
    {
        if (layer == null) return;
        EnsureLayer(layer);
        e.Layer = layer;
    }

    public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().AddLine(Pt(x1, y1), Pt(x2, y2));
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddLine 失败: {ex.Message}", ex); }
    });

    public void AddText(double x, double y, string text, double height, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().AddText(text, Pt(x, y), height);
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddText 失败: {ex.Message}", ex); }
    });

    public void AddCircle(double x, double y, double radius, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().AddCircle(Pt(x, y), radius);
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddCircle 失败: {ex.Message}", ex); }
    });

    public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = ModelSpace().InsertBlock(Pt(x, y), blockName, scale, scale, scale, rotation);
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddInsert 失败: {ex.Message}", ex); }
    });

    public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        try
        {
            dynamic blocks = _doc.Blocks;
            bool exists = false;
            try { dynamic b0 = blocks.Item(blockName); exists = b0 != null; }
            catch { exists = false; }
            if (exists) return;
            // AutoCAD 2014 实测序=(origin, Name)（2026-11-24 探针核定，与文档序相反，同 ZWCAD）。
            // 厂商差异仅数据：翻 VendorProfiles.Acad.BlocksAddNameFirst 即可，无需改此逻辑。
            dynamic block = Profile.BlocksAddNameFirst
                ? blocks.Add(blockName, Pt(0, 0))
                : blocks.Add(Pt(0, 0), blockName);
            foreach (var p in primitives)
            {
                switch (p)
                {
                    case BlockLine l: block.AddLine(Pt(l.X1, l.Y1), Pt(l.X2, l.Y2)); break;
                    case BlockCircle c: block.AddCircle(Pt(c.X, c.Y), c.Radius); break;
                    case BlockText t: block.AddText(t.Value, Pt(t.X, t.Y), t.Height); break;
                    default: throw new CadRuntimeException($"不支持的块基元: {p.GetType().Name}");
                }
            }
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EnsureBlock 失败: {ex.Message}", ex); }
    });

    public int EntityCount() => Run(() =>
    {
        try { return (int)ModelSpace().Count; }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EntityCount 失败: {ex.Message}", ex); }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Run(() =>
            {
                // 忙拒绝上抛给 RetryBusy 重试（同 Restart 注记）；其余退出期错误忽略。
                try { _doc?.Close(false); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 退出时忽略 */ }
                _doc = null;
                try { _app?.Quit(); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 退出时忽略 */ }
                _app = null;
            });
        }
        catch { /* 队列已关闭等 */ }
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(10));
        // Quit 返回≠进程退出：CUIX 回写发生在退出尾段（PR#17 机制，厂商无关）。
        VendorGuard.WaitForProcessExit(Profile, VendorGuard.ExitWait);
    }
}
