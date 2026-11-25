namespace CadAgent.Adapters.Acad;

/// <summary>
/// B7（技术债批次B）：ROT 按 PID 过滤**耗尽**的内部终局信号。
/// <para>语义：过滤路径在轮询上界内未见"属于本方 PID 且进程存活"的实例——与"本方进程已退出"
/// （<see cref="AcadAttach.ProcessExitedMessage"/>，终局不回溯）不同，本信号允许
/// <see cref="AcadRuntime"/> 做**一次** GetActiveObject 小预算回退。</para>
/// <para>为何不派生 <c>CadRuntimeException</c>：本类型只在适配器内部传达"可回退"这一控制流；
/// **包装点是 <c>AcadRuntime.AttachByPidWithFallback</c> 的回退边界**（相位预算处理内——
/// 回退已发起/回退失败即包装为 <c>CadRuntimeException</c>），调用方契约不变；
/// 本类型不外泄（<c>EnsureAppTArch</c> 不捕获它，旧 catch 已按不可达删除）。
/// 按**类型**判定取代旧的消息子串匹配（"ROT 过滤"+"超时"），避免文案调整即静默改变回退行为。</para>
/// </summary>
internal sealed class RotFilterExhaustedException : Exception
{
    internal RotFilterExhaustedException(string message) : base(message)
    {
    }
}
