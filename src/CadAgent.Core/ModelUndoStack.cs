namespace CadAgent.Core;

/// <summary>
/// 会话内模型快照栈（3b-ii 步级 undo）：每步执行前 Push 捕获态，UndoLastStep Pop 恢复。
/// 会话级（引擎实例持有）；跨会话撤回仍靠事务快照（既有机制兜底）。
/// </summary>
public sealed class ModelUndoStack
{
    private readonly Stack<byte[]> _states = new();

    public int Count => _states.Count;

    public void Push(byte[] state) => _states.Push(state);

    public byte[]? Pop() => _states.Count > 0 ? _states.Pop() : null;
}