using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad;

/// <summary>天正 profile 感知附着（spec 切片A §4.1，spike 协议步骤2-3）。
/// AutoCAD 仅当启动参数含 /p "TElec20V5" 才加载天正 TArch——普通 COM 激活加载默认
/// profile（无 TArch）。故需从 ROT（Running Object Table）附着到已由 /p 拉起的实例。
/// P/Invoke 归属：CLSIDFromProgID 在 ole32、GetActiveObject 在 oleaut32
/// （标错 DLL→EntryPointNotFound，且异常易被上层静默吞——spike 实证）。
/// fix/acad-rot-pid-filter（真机实证 3/4 全量失败）：<c>GetActiveObject</c> 取"当前活动对象"，
/// 类间交接时前一真机类刚 Quit 的实例注册可能仍在 ROT → 附着到**他方/已退出**实例 → 归属校验
/// 伪失败"ROT 附着实例非本方启动"。故新增按**本方进程 PID** 过滤的附着通道
/// （<see cref="AttachByPid"/>：枚举 ROT→逐 moniker <c>IRunningObjectTable.GetObject</c> 查表→实例窗口
/// PID==本方 PID 且进程存活）；<see cref="AttachOnly"/> 保留为"本方 PID 不可判定"时的回退路径，语义不变。</summary>
public static class AcadAttach
{
    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string progid, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    // ROT 枚举通道（fix/acad-rot-pid-filter）：GetRunningObjectTable 在 ole32。
    // 评审 follow-up 1（I1）：候选对象改由 IRunningObjectTable.GetObject 查表直取——不再 CreateBindCtx
    // + IMoniker.BindToObject（绑定语义在文档 moniker 的服务器已退出时会触发激活副作用）。
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    // 窗口→进程 ID（user32；返回 0=不可判定/窗口已销毁）。与 AcadWindows 内的同名 P/Invoke
    // 各自独立：AcadAttach 不依赖窗口诊断面（AcadWindows 的缝服务关窗/安全框），此处缝专供 ROT 归属过滤。
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    // 缝：测试注入替代真 COM 查询（单元测试不得触真机）；默认=ROT GetActiveObject 包装。
    internal static Func<string, object?> ActiveObjectResolver = progid =>
    {
        if (CLSIDFromProgID(progid, out var clsid) < 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) < 0 ? null : obj;
    };

    /// <summary>有界轮询附着：启动窗口内 ROT 尚未注册（/p 启动需数秒加载）→ null 后等 1s 重试。
    /// 天正加载慢于普通启动，默认 180 次（180s）覆盖冷启动；耗尽→响亮拒绝（CLI 降级）。
    /// 仅用于"本方 PID 不可判定"（fake/未启动进程、真机 PID 读取失败）的回退路径——可判定时
    /// 一律走 <see cref="AttachByPid"/>（PID 过滤，不会命中交接残留的他方实例）。</summary>
    internal static object AttachOnly(string progId, int maxAttempts = 180)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var app = ActiveObjectResolver(progId);
            if (app != null) return app;
            if (i < maxAttempts - 1) Thread.Sleep(1000);   // 末轮不再睡（耗尽即抛，不白等一节拍）
        }
        throw new CadRuntimeException($"天正实例 ROT 附着超时（{maxAttempts}s）：{progId} 未就绪");
    }

    /// <summary>本方进程已退出时的附着中止文案（逐字钉住；调用方/单测按此判定"中止"而非"超时"）。</summary>
    internal const string ProcessExitedMessage = "本方 AutoCAD 进程已退出（附着中止）";

    // ---- fix/acad-rot-pid-filter：按本方 PID 过滤的 ROT 附着 ----

    /// <summary>ROT 候选过滤器（缝，单测注入；默认 <see cref="DefaultRotAttachByPid"/>）：
    /// 返回**属于 <paramref name="pid"/> 且进程仍存活**的实例对象；无匹配→null（调用方轮询）。
    /// 只按 PID 过滤，不按 ProgID：ROT 显示名是 exe 路径/文档名而非 ProgID，按名过滤会漏掉本方实例。
    /// 查表失败（MK_E_UNAVAILABLE）是 **HRESULT 返回值**而非异常（见 <see cref="DefaultRotAttachByPid"/>）；
    /// ROT 不可用/枚举中断等 COM 层异常一律吞掉返回 null——附着是轮询式等待，单轮失败不等于终局。</summary>
    internal static Func<string, int, object?> RotAttachByPid = DefaultRotAttachByPid;

    /// <summary>实例窗口句柄读取缝（默认 <c>(int)app.HWND</c>；单测注入，绝不触 COM）。</summary>
    internal static Func<object, int> HwndReader = app => (int)((dynamic)app).HWND;

    /// <summary>窗口句柄→所属进程 ID 缝（默认 user32 GetWindowThreadProcessId；0=不可判定）。</summary>
    internal static Func<int, int> WindowProcessId = DefaultWindowProcessId;

    /// <summary>进程存活判定缝（默认 Process.GetProcessById + HasExited；单测注入）。
    /// ROT 里可能残留已退出实例的注册——归属匹配不够，还须确认进程真的活着。</summary>
    internal static Func<int, bool> PidAlive = DefaultPidAlive;

    /// <summary>按 PID 过滤的有界轮询附着：1s 一次（与 <see cref="AttachOnly"/> 同节拍），
    /// 每轮**先问** <paramref name="abortIf"/>：true=本方进程已退出→立即中止
    /// （<see cref="ProcessExitedMessage"/>，不空转满 180s 上界——进程没了再等也无意义）。
    /// 耗尽→响亮拒绝（消息含本方 PID 与轮询秒数，真机可据此区分"未启动"与"启动过慢"）。
    /// 与 <see cref="AttachOnly"/> 的区别：不会附着到"ROT 里当前活动但非本方"的实例。</summary>
    internal static object AttachByPid(
        string progId, int pid, int maxAttempts = 180, Func<bool>? abortIf = null)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            if (abortIf != null && abortIf()) throw new CadRuntimeException(ProcessExitedMessage);
            var app = RotAttachByPid(progId, pid);
            if (app != null) return app;
            if (i < maxAttempts - 1) Thread.Sleep(1000);   // 末轮不再睡（耗尽即抛）
        }
        throw new CadRuntimeException(
            $"未见本方 AutoCAD 实例注册（PID {pid}，ROT 过滤 {maxAttempts}s 超时）");
    }

    /// <summary>ROT 候选归属判定（纯逻辑，可单测）：实例窗口属目标 PID 且该进程仍存活。
    /// 任一环节不可判定（无候选/无目标 PID/窗口句柄 0/读不出 HWND/窗口→PID 失败）→ false：
    /// **宁可漏认不可错认**——错认会把后续 Quit 打到他方实例上并留下本方孤儿进程。</summary>
    internal static bool OwnedByPid(object? app, int pid)
    {
        if (app == null || pid <= 0) return false;
        int hwnd;
        try { hwnd = HwndReader(app); }
        catch { return false; }   // HWND 面不可读（无 IDispatch/实例退出中）：跳过该候选
        if (hwnd == 0) return false;
        int ownerPid;
        try { ownerPid = WindowProcessId(hwnd); }
        catch { return false; }
        if (ownerPid != pid) return false;
        try { return PidAlive(pid); }
        catch { return false; }
    }

    /// <summary>默认过滤器：GetRunningObjectTable→EnumRunning→逐个 moniker
    /// **<c>IRunningObjectTable.GetObject</c> 查表直取**（无 moniker 绑定语义——文档 moniker 的
    /// 服务器已退出时 BindToObject 会触发激活副作用）。
    /// **该接口方法是 PreserveSig**（BCL 签名 <c>int GetObject(IMoniker, out object)</c>，net8.0 实测）：
    /// 表内已无效时以**返回值** MK_E_UNAVAILABLE(0x800401E3) 报告而**不**抛 COMException——故按
    /// HRESULT&lt;0 显式判负，把候选置 null 跳过（此时 out 形参无定义/未写，绝不得当候选用）。
    /// → 读 HWND→窗口 PID→与目标 PID 比对→进程存活→返回**首个命中**者；全不命中→null。
    /// try/catch 只保留在**确实会抛**的环节（HWND 读取/窗口→PID/存活判定，见 <see cref="OwnedByPid"/>）
    /// 与 ROT 层整体（GetRunningObjectTable/EnumRunning/Next 异常→本轮 null，交调用方轮询）。
    /// **释放对称性**（评审 M-8）：moniker 无论接受/拒绝**一律** best-effort 释放——它只是枚举句柄，
    /// 命中后不再需要；**候选对象只在被拒时释放**——接受的候选所有权转交调用方（若同一 RCW 经不同
    /// moniker 再次出现，已返回的那个绝不能被这里二次 Release，否则调用方持到的是被减过的引用）。
    /// 枚举器/ROT 在 finally 内释放；不接受也不读取 moniker 显示名（DisplayName-free）。</summary>
    private static object? DefaultRotAttachByPid(string progId, int pid)
    {
        // progId 参与签名以保持缝口径与诊断一致；过滤维度只有 PID（见 RotAttachByPid 注释）。
        _ = progId;
        if (pid <= 0) return null;
        IRunningObjectTable? rot = null;
        IEnumMoniker? enumMoniker = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) < 0 || rot == null) return null;
            rot.EnumRunning(out enumMoniker);
            if (enumMoniker == null) return null;
            var batch = new IMoniker[1];
            while (true)
            {
                // S_OK=取到 1 个；S_FALSE/失败=枚举结束。
                if (enumMoniker.Next(1, batch, IntPtr.Zero) != 0) break;
                IMoniker? moniker = batch[0];
                if (moniker == null) break;
                // PreserveSig（评审 follow-up 2 更正）：失败形态是 HRESULT 返回值（表内无效→
                // MK_E_UNAVAILABLE=0x800401E3），**不**抛 COMException——判负即置 null 跳过该候选。
                // 此形态下 out 形参无定义（未写），不得使用其内容。
                object? candidate = rot.GetObject(moniker, out object resolved) < 0 ? null : resolved;
                if (candidate != null && OwnedByPid(candidate, pid))
                {
                    ReleaseQuietly(moniker);      // M-8：moniker 不随命中泄漏（候选已取得，句柄无用）
                    return candidate;             // 候选所有权转交调用方——此处**不**释放
                }
                ReleaseQuietly(candidate);        // M-8：只有被拒候选才释放（接受的绝不二次 Release）
                ReleaseQuietly(moniker);
            }
            return null;
        }
        catch
        {
            return null;   // ROT 层 COM 异常（不可用/枚举中断）：交给调用方轮询
        }
        finally
        {
            ReleaseQuietly(enumMoniker);
            ReleaseQuietly(rot);
        }
    }

    // RCW 释放（best-effort）：非 COM 对象/已释放/枚举器仍在使用均可能抛——一律吞掉，
    // 释放失败只影响引用计数回收时机，不影响附着结论。
    private static void ReleaseQuietly(object? comObject)
    {
        if (comObject == null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch { /* 释放 best-effort */ }
    }

    private static int DefaultWindowProcessId(int windowHandle)
    {
        if (windowHandle == 0) return 0;
        try
        {
            return GetWindowThreadProcessId(new IntPtr(windowHandle), out int pid) == 0 ? 0 : pid;
        }
        catch { return 0; }   // 会话无桌面/句柄失效：不可判定
    }

    private static bool DefaultPidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }   // 进程不存在/已退出/无访问权限：按"不存活"处理（fail-closed）
    }
}
