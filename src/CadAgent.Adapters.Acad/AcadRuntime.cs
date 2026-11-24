using System.Collections.Concurrent;
using System.Diagnostics;
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

    // 重名碰撞守卫（复审微修复波）：本文档会话经 EnsureBlock 成功创建的块名集合。
    // 重入补全只对"本方创建"的块清除重建；预存用户块（名不在集合）一律复用早退——
    // skill 符号名与用户图纸自有块重名（如 "DOOR"）时不得删除用户内容（恢复旧非破坏行为）。
    // AutoCAD 块名大小写不敏感（与 insert-symbol 符号匹配同口径 OrdinalIgnoreCase）；
    // Open 换文档即清空：跨文档同名块不属于本文档会话的"本方残留"。
    private readonly HashSet<string> _createdBlocks = new(StringComparer.OrdinalIgnoreCase);

    internal static Func<string, Type?> ProgIdResolver = Type.GetTypeFromProgID;
    internal static Func<Type, object?> CreateComInstance = t => Activator.CreateInstance(t);

    // 切片A §4.1：天正 profile 感知启动的 exe 启动缝（测试注入；单元测试不得拉起真机）。
    // 默认硬编码 "acad.exe"（安装 AutoCAD 2014 的真机环境 PATH 可解析；不做注册表
    // LocalServer32 路径发现——T10 真机核定该假定，失败再收紧，避免提前过度设计）。
    // 返回 null=启动失败（EnsureAppTArch 统一转 CadRuntimeException；UseShellExecute 时
    // Process.Start 亦可能返回 null）。
    internal static Func<string, Process?> ProcessLauncher = profileName =>
    {
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "acad.exe",
                Arguments = $"/p \"{profileName}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            return null;
        }
    };

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

    // Run=纯 STA 调度（I2，PR#24 修复波）：不再整 lambda 忙重试——复合方法
    // （AddX+SetLayer/EnsureBlock 多基元）后段拒绝→整段重试会重复前段副作用（重复实体）。
    // 忙重试降至单个 COM 基元调用粒度（RetryBusy），由各入口/基元自行包装。
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

    // 应用层忙拒绝有界重试（真机核定 2026-11-24）：AutoCAD 忙时（Open 后再生窗口等）
    // IDispatch::Invoke 直接把 RPC_E_CALL_REJECTED(0x80010001)/RETRYLATER(0x8001010A)
    // 作为调用结果返回——RPC 层消息过滤器兜不住此形态（f9 实证：SaveAs 仍被拒）。
    // **单调用口径（I2 更正）**：拒绝=该单个 COM 基元调用未被执行，仅对单调用重试安全；
    // 复合方法不得整段重试（后段拒绝→前段副作用重复），须逐基元包装（见 Add*/EnsureBlock）。
    // 只读取值（ModelSpace()/Blocks/Count 等 getter）无副作用，可与后续单写调用同组重试。
    // 非忙错误立即上抛不吞诊断；操作内层已把 COMException 包装为 CadRuntimeException→
    // 沿 InnerException 链判定。
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

    private static void RetryBusy(Action a) => RetryBusy<object?>(() => { a(); return null; });

    private static bool IsBusyRejection(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is COMException com &&
                (com.HResult == unchecked((int)0x80010001) || com.HResult == unchecked((int)0x8001010A)))
                return true;
            // GetIDsOfNames 层拒绝被 C# 绑定器包装为 MissingMemberException（消息含 HRESULT）。
            // M3 脆弱面披露（PR#24 修复波）：消息文本匹配依赖绑定器消息格式含 HRESULT 十六进制
            // 串——区域设置/运行时版本变化可能失配（漏判→忙拒绝当普通错误上抛，安全侧失败，
            // 不产生重复副作用）。COMException HResult 直判为主通道；此分支为 f6 实证形态兜底。
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

    // ---- 切片A §4.1：天正 profile 感知启动（/p 启动 + ROT 附着 + Gate1）----
    // 与既有 EnsureApp（普通 COM 激活→默认 profile，无 TArch）互斥：_app 已存在即复用，
    // 不得再拉起第二实例（双实例共享用户 profile 会竞态损坏 CUIX，VendorGuard 语义）。
    // 流程：守卫→/p "profileName" 启动 acad.exe→ROT 附着（AcadAttach）→Gate1 就绪等待。
    // 调用方（ReadNativeParams，Task 5）经 Run 在 STA 队列内执行。
    internal dynamic EnsureAppTArch(string profileName)
    {
        if (_app != null) return _app;
        VendorGuard.EnsureNoForeignInstance(Profile);
        Process? proc;
        try { proc = ProcessLauncher(profileName); }
        catch (Exception ex) { throw new CadRuntimeException("天正实例启动失败", ex); }
        if (proc == null)
            throw new CadRuntimeException("天正实例启动失败");
        _app = AcadAttach.AttachOnly(Profile.ProgId);
        WaitTArchGate1();
        return _app;
    }

    // Gate1（spike 协议步骤3）：ROT 附着成功≠天正初始化完成——Version 非空字符串且
    // 至少 1 个文档（/p 启动默认开 Drawing1.dwg）才可安全读参。就绪条件不满足→按
    // StartupRetryDelay 有界重试（StartupRetryAttempts 次）；忙拒绝由内层 RetryBusy
    // 自行有界重试，其耗尽即"确实未就绪"→直接 Gate1 终局（不叠加外层重试放大总时长）。
    // 耗尽→响亮拒绝（不无限挂起，CLI 据此降级）。
    private void WaitTArchGate1()
    {
        Exception? last = null;
        for (int i = 0; i < StartupRetryAttempts; i++)
        {
            try
            {
                var version = RetryBusy(() => (string)_app!.Version);
                if (!string.IsNullOrEmpty(version) && RetryBusy(() => (int)_app!.Documents.Count) >= 1)
                    return;
            }
            catch (Exception ex) when (IsBusyRejection(ex))
            {
                // RetryBusy 已用完 StartupRetryAttempts 次忙重试仍被拒——终局不再叠加。
                throw new CadRuntimeException("天正实例未就绪（Gate1 超时）", ex);
            }
            catch (Exception ex) { last = ex; /* 其他未就绪错误：按 Gate1 预算重试到界 */ }
            Thread.Sleep(StartupRetryDelay);
        }
        throw last == null
            ? new CadRuntimeException("天正实例未就绪（Gate1 超时）")
            : new CadRuntimeException("天正实例未就绪（Gate1 超时）", last);
    }

    // 孤儿注册/激活失败/守卫超时均优雅降级 Available=false（CLI 据此走 DXF 降级+警示）。
    // 守卫超时（已有实例运行）也报不可用→降级，安全侧选择；真机协议注记此语义。
    public RuntimeInfo GetInfo() => Run(() =>
    {
        if (ProgIdResolver(Profile.ProgId) == null)
            return new RuntimeInfo("AutoCAD", "", false);
        try
        {
            // 激活（含启动窗口有界重试）先行：失败直达 catch→Available=false（旧语义保持，
            // 不得经忙重试路径绕过启动窗口——EnsureApp 失败后 _app 已缓存，重试会跳过 Visible）。
            var app = EnsureApp();
            // 单调用入口：Version 只读 getter 独立忙重试。
            string version = RetryBusy(() => (string)app.Version);
            return new RuntimeInfo("AutoCAD", version, true);
        }
        catch
        {
            return new RuntimeInfo("AutoCAD", "", false);
        }
    });

    public void Open(string drawingPath) => Run(() =>
    {
        // 单调用入口：激活先行（自带启动窗口重试，失败即上抛）；Documents.Open 单个 COM
        // 基元独立忙重试——拒绝=未执行，重试不重复打开。
        try
        {
            var app = EnsureApp();
            _doc = RetryBusy<object?>(() => app.Documents.Open(drawingPath));
            // 换文档即失效本方创建块名集合（重名碰撞守卫按文档会话口径，防跨文档误清）。
            _createdBlocks.Clear();
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"打开图纸失败: {ex.Message}", ex); }
    });

    public void SaveAs(string outputPath) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        // 单调用入口：SaveAs 单个 COM 基元独立忙重试（f9 实证形态）。
        try { RetryBusy(() => { _doc.SaveAs(outputPath); }); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"SaveAs 失败: {ex.Message}", ex); }
    });

    public void Close(bool save) => Run(() =>
    {
        // COM 失败统一 CadRuntimeException（与 SaveAs 同款）；
        // 仅 Close 成功后清空 _doc——失败时文档可能仍开，保留引用供重试/Restart。
        // 单调用入口：Close 单个 COM 基元独立忙重试；耗尽→包装上抛（_doc 保留）。
        try { if (_doc != null) { RetryBusy(() => { _doc.Close(save); }); _doc = null; } }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"Close 失败: {ex.Message}", ex); }
    });

    public void Restart() => Run(() =>
    {
        // 先关文档再 Quit：带开文档 Quit 会被判异常终止→下次启动弹 CUIX 修复对话框（根因 a）。
        // I2：每个基元独立忙重试——Close 成功后 Quit 被拒仅重试 Quit，不重复 Close。
        // 忙拒绝耗尽→上抛（M3 边界，调用方降级）；非忙错误忽略（崩溃后 Close/Quit 可能失败）。
        try { RetryBusy(() => { _doc?.Close(false); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 崩溃后 Close 可能失败，忽略 */ }
        _doc = null;
        try { RetryBusy(() => { _app?.Quit(); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 崩溃后 Quit 可能失败，忽略 */ }
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
        // I2：Item 只读探测 / Add 单写调用各自独立忙重试（拒绝=该调用未执行，不重复建层）。
        try { dynamic l = RetryBusy<object?>(() => _doc.Layers.Item(layer))!; if (l != null) return; }
        catch { /* 不存在→创建 */ }
        RetryBusy(() => { _doc.Layers.Add(layer); });
    }

    private void SetLayer(dynamic e, string? layer)
    {
        if (layer == null) return;
        EnsureLayer(layer);
        // I2：Layer 属性写入为独立单写调用——AddX 成功后此处被拒仅重试写入，不重复建实体。
        RetryBusy(() => { e.Layer = layer; });
    }

    // I2（PR#24 修复波）：复合方法=AddX+SetLayer 多 COM 调用——每个基元独立忙重试，
    // 组合不外层重试（后段拒绝→整段重试会重复实体）。ModelSpace() 只读 getter 无副作用，
    // 与后续单写调用同组重试安全。
    public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().AddLine(Pt(x1, y1), Pt(x2, y2)))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddLine 失败: {ex.Message}", ex); }
    });

    public void AddText(double x, double y, string text, double height, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().AddText(text, Pt(x, y), height))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddText 失败: {ex.Message}", ex); }
    });

    public void AddCircle(double x, double y, double radius, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().AddCircle(Pt(x, y), radius))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddCircle 失败: {ex.Message}", ex); }
    });

    public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().InsertBlock(Pt(x, y), blockName, scale, scale, scale, rotation))!;
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
            // I2：逐基元独立忙重试，无整段外层重试（中途拒绝→整段重试会重复基元）。
            dynamic blocks = RetryBusy<object?>(() => _doc.Blocks)!;
            dynamic? existing = null;
            try { existing = RetryBusy<object?>(() => blocks.Item(blockName)); }
            catch { existing = null; /* 不存在→创建 */ }
            if (existing != null)
            {
                // 重名碰撞守卫（复审微修复波）：名不在本方创建集合=预存用户块（skill 符号名
                // 撞用户自有块）→复用早退（恢复旧非破坏行为），绝不 Delete 用户内容。
                if (!_createdBlocks.Contains(blockName)) return;
                // I2 重入补全（PR#24 修复波）：本方先前失败可能残留部分块（中途忙拒绝耗尽）——
                // 静默早退会留下静默部分块（读回计数闸只数模型空间，兜不住块内容）。
                // 比对块内实体数与期望基元数：完整→复用（幂等保持）；不一致（不足=残留）→
                // 清除残留基元再重建。仅本方创建块（_createdBlocks 守卫）清除安全；
                // 块定义本身保留（origin 不变，引用不受影响）。
                int count = RetryBusy(() => (int)existing.Count);
                if (count == primitives.Count) return;
                for (int i = count - 1; i >= 0; i--)
                {
                    dynamic item = RetryBusy<object?>(() => existing.Item(i))!;
                    RetryBusy(() => { item.Delete(); });
                }
                AddBlockPrimitives(existing, primitives);
                return;
            }
            // AutoCAD 2014 实测序=(origin, Name)（2026-11-24 探针核定，与文档序相反，同 ZWCAD）。
            // 厂商差异仅数据：翻 VendorProfiles.Acad.BlocksAddNameFirst 即可，无需改此逻辑。
            dynamic block = RetryBusy<object?>(() => Profile.BlocksAddNameFirst
                ? blocks.Add(blockName, Pt(0, 0))
                : blocks.Add(Pt(0, 0), blockName))!;
            // 创建成功即入本方集合（先于基元填充）：中途忙拒绝耗尽残留的部分块也属"本方"，
            // 重入走补全重建通道；预存用户块永不入集合。
            _createdBlocks.Add(blockName);
            AddBlockPrimitives(block, primitives);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EnsureBlock 失败: {ex.Message}", ex); }
    });

    // 块基元逐个独立忙重试（I2）：每个 AddX 为单写调用，拒绝=未执行，重试不重复。
    private static void AddBlockPrimitives(dynamic block, IReadOnlyList<BlockPrimitive> primitives)
    {
        foreach (var p in primitives)
        {
            switch (p)
            {
                case BlockLine l: RetryBusy(() => { block.AddLine(Pt(l.X1, l.Y1), Pt(l.X2, l.Y2)); }); break;
                case BlockCircle c: RetryBusy(() => { block.AddCircle(Pt(c.X, c.Y), c.Radius); }); break;
                case BlockText t: RetryBusy(() => { block.AddText(t.Value, Pt(t.X, t.Y), t.Height); }); break;
                default: throw new CadRuntimeException($"不支持的块基元: {p.GetType().Name}");
            }
        }
    }

    public int EntityCount() => Run(() =>
    {
        // 只读组合（ModelSpace getter + Count getter）无副作用，同组忙重试安全。
        try { return RetryBusy(() => (int)ModelSpace().Count); }
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
                // I2：每个基元独立忙重试（同 Restart）；忙拒绝耗尽由 RetryBusy 上抛→
                // 外层 catch 兜底（Dispose 不抛，best-effort 收尾；有界尝试次数单测钉住）。
                // 其余退出期错误忽略。
                try { RetryBusy(() => { _doc?.Close(false); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 退出时忽略 */ }
                _doc = null;
                try { RetryBusy(() => { _app?.Quit(); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 退出时忽略 */ }
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
