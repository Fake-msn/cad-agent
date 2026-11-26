namespace CadAgent.Core;

/// <summary>
/// 可选能力（项3 §5）：会话可告知内容已被绕过引擎的路径修改，使 ComputeHash 缓存失效。
/// 契约：直接变更文档而未经 StepEngine 的调用方必须先调用 <see cref="InvalidateHash"/>，
/// 否则 ComputeHash 返回最近一次计算的旧值；非实现者（测试 fake）行为不变。
/// </summary>
public interface IHashRevision
{
    long Revision { get; }
    void InvalidateHash();
}
