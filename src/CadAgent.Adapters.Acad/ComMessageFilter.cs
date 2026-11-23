using System.Runtime.InteropServices;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// COM 消息过滤器（IMessageFilter）：启动窗口/模态态下厂商拒绝入站调用
/// （RPC_E_CALL_REJECTED 0x80010001 / RPC_E_SERVERCALL_RETRYLATER 0x8001010A）时，
/// 在 COM 层自动延迟重试，而非把拒绝抛给调用方。
/// 真机核定（2026-11-24，AutoCAD 2014 R19.1）：CreateInstance 返回≠初始化完成，
/// 紧随的 dynamic 调用在 IDispatch.GetIDsOfNames 层即被拒——C# 绑定器将其包装为
/// MissingMemberException（非 COMException），上层 catch COMException 的重试无法兜住；
/// 消息过滤器在绑定器之下生效，两种拒绝形态统一消解（Office/AutoCAD 自动化标准模式）。
/// 仅在 STA 线程注册有效（AcadRuntime 专用 STA 线程启动时注册）；有界 30s 防挂死。
/// </summary>
internal static class ComMessageFilter
{
    private const int ServerCallIsHandled = 0;
    private const int ServerCallRetryLater = 2;
    private const int PendingMsgWaitDefProcess = 2;
    private const int RetryDelayMs = 200;
    private const int MaxWaitMs = 30_000;

    [ComImport, Guid("00000016-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    private sealed class Filter : IMessageFilter
    {
        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
            => ServerCallIsHandled;

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            // dwTickCount=调用发起时的 tick；超过 30s 上限→取消（-1），拒绝上抛由上层降级。
            // 真机核定（2026-11-24）：Open DXF 后紧随的 SaveAs 被拒 0x80010001，
            // dwRejectType=SERVERCALL_ISHANDLED(0)（非 RETRYLATER）——只重试 2 会漏掉
            // 此形态（binder 包装为 MissingMemberException 上抛）。两种拒绝型统一有界重试。
            if ((dwRejectType == ServerCallIsHandled || dwRejectType == ServerCallRetryLater)
                && unchecked(Environment.TickCount - dwTickCount) < MaxWaitMs)
                return RetryDelayMs;
            return -1;
        }

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
            => PendingMsgWaitDefProcess;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IMessageFilter? newFilter, out IMessageFilter? oldFilter);

    /// <summary>在当前 STA 线程注册过滤器；失败不抛（降级为无过滤器，上层重试兜底）。</summary>
    public static void Register()
    {
        try { CoRegisterMessageFilter(new Filter(), out _); }
        catch { /* 非 STA/平台不支持等：不阻断适配器构造 */ }
    }
}
