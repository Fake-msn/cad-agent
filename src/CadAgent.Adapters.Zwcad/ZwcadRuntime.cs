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

    // COM 激活 seam（与 AcadRuntime 同款，PR#18 修复波 Important#1 回归测试注入点）：
    // 生产默认真实反射/激活，行为不变；测试注入假 app/doc 钉住 Close 包装契约。
    internal static Func<string, Type?> ProgIdResolver = Type.GetTypeFromProgID;
    internal static Func<Type, object?> CreateComInstance = t => Activator.CreateInstance(t);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _staThread;
    private dynamic? _app;
    private dynamic? _doc;
    private bool _disposed;

    // 重名碰撞守卫（对齐 AcadRuntime 已审查语义）：本文档会话经 EnsureBlock 成功创建的块名集合。
    // 重入补全只对"本方创建"的块清除重建；预存用户块（名不在集合）一律复用早退——
    // skill 符号名与用户图纸自有块重名（如 "DOOR"）时不得删除用户内容（非破坏）。
    // ZWCAD 块名大小写不敏感（与 insert-symbol 符号匹配同口径 OrdinalIgnoreCase）；
    // Open 换文档即清空：跨文档同名块不属于本文档会话的"本方残留"。
    private readonly HashSet<string> _createdBlocks = new(StringComparer.OrdinalIgnoreCase);

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
            VendorGuard.EnsureNoForeignInstance(VendorProfiles.Zwcad);
            var type = ProgIdResolver(ProgId)
                ?? throw new CadRuntimeException($"ZWCAD COM 未注册: {ProgId}");
            _app = CreateComInstance(type)
                ?? throw new CadRuntimeException("ZWCAD COM 实例创建失败");
            _app.Visible = false;
        }
        return _app;
    }

    public RuntimeInfo GetInfo() => Run(() =>
    {
        if (ProgIdResolver(ProgId) == null)
            return new RuntimeInfo("ZWCAD", "", false);
        string version = EnsureApp().Version;
        return new RuntimeInfo("ZWCAD", version, true);
    });

    public void Open(string drawingPath) => Run(() =>
    {
        try
        {
            _doc = EnsureApp().Documents.Open(drawingPath);
            // 换文档即失效本方创建块名集合（重名碰撞守卫按文档会话口径，防跨文档误清，镜像 Acad）。
            _createdBlocks.Clear();
        }
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
        // 与 AcadRuntime.Close 同款：COM 失败统一 CadRuntimeException（ExportService 只捕该型，
        // 裸 COMException 逃逸会跳过 Restart 重试与 export-failed 审计）；
        // 仅 Close 成功后清空 _doc——失败时文档可能仍开，保留引用供重试/Restart。
        try { if (_doc != null) { _doc.Close(save); _doc = null; } }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"Close 失败: {ex.Message}", ex); }
    });

    public void Restart() => Run(() =>
    {
        // 先关文档再 Quit：带开文档 Quit 会被 ZWCAD 判异常终止→下次启动弹修复对话框
        try { _doc?.Close(false); } catch { /* 崩溃后 Close 可能失败，忽略 */ }
        _doc = null;
        try { _app?.Quit(); } catch { /* 崩溃后 Quit 可能失败，忽略 */ }
        _app = null;
    });

    // 3j 绘制操作：ModelSpace dynamic COM（ZWCAD 镜像 AutoCAD API）；
    // 点以 double[3] variant 数组传递；失败统一 CadRuntimeException。
    private dynamic ModelSpace()
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        return _doc.ModelSpace;
    }

    private static double[] Pt(double x, double y) => new[] { x, y, 0.0 };

    /// <summary>图层幂等确保（镜像影子侧 ResolveLayer；厂商文档赋不存在层会 COM 报错）。</summary>
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
            dynamic? existing = null;
            // Item 宽 catch 与 Acad 现状一致（收窄 tracked 不扩）：不存在/COM 探测失败均走创建通道。
            try { existing = blocks.Item(blockName); }
            catch { existing = null; /* 不存在→创建 */ }
            if (existing != null)
            {
                // 重名碰撞守卫（对齐 Acad）：名不在本方创建集合=预存用户块（skill 符号名
                // 撞用户自有块）→复用早退（非破坏），绝不 Delete 用户内容。
                if (!_createdBlocks.Contains(blockName)) return;
                // 重入补全（对齐 Acad）：本方先前失败可能残留部分块（中途 COM 失败）——
                // 静默早退会留下静默部分块（读回计数闸只数模型空间，兜不住块内容）。
                // 比对块内实体数与期望基元数：完整→复用（幂等保持）；不一致（不足=残留）→
                // 倒序清除残留基元再重建。仅本方创建块（_createdBlocks 守卫）清除安全；
                // 块定义本身保留（origin 不变，引用不受影响）。
                // ZWCAD COM 与 AutoCAD 同构：block.Count/.Item(i)/.Delete()（真机测试核定）。
                int count = (int)existing.Count;
                if (count == primitives.Count) return;
                for (int i = count - 1; i >= 0; i--)
                {
                    dynamic item = existing.Item(i);
                    item.Delete();
                }
                AddBlockPrimitives(existing, primitives);
                return;
            }
            // Blocks.Add 签名序按厂商 profile 分支（ZWCAD 实测 origin 先；AutoCAD 文档 Name 先）
            dynamic block = VendorProfiles.Zwcad.BlocksAddNameFirst
                ? blocks.Add(blockName, Pt(0, 0))
                : blocks.Add(Pt(0, 0), blockName);
            // 创建成功即入本方集合（先于基元填充）：中途失败残留的部分块也属"本方"，
            // 重入走补全重建通道；预存用户块永不入集合。
            _createdBlocks.Add(blockName);
            AddBlockPrimitives(block, primitives);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EnsureBlock 失败: {ex.Message}", ex); }
    });

    // 块基元填充（创建与重入补全共用；Zwcad 无忙重试基础设施，单次 COM 失败即上抛）。
    private static void AddBlockPrimitives(dynamic block, IReadOnlyList<BlockPrimitive> primitives)
    {
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
                try { _doc?.Close(false); } catch { /* 退出时忽略 */ }
                _doc = null;
                try { _app?.Quit(); } catch { /* 退出时忽略 */ }
                _app = null;
            });
        }
        catch { /* 队列已关闭等 */ }
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(10));
        // Quit 返回≠进程退出：CUIX 回写发生在退出尾段；测试宿主若先退出会留下
        // 不完整收尾（ZCR 残留簇证据）。等待进程退出闭合该窗口（2026-11-22 复现分析）。
        VendorGuard.WaitForProcessExit(VendorProfiles.Zwcad, VendorGuard.ExitWait);
    }
}
