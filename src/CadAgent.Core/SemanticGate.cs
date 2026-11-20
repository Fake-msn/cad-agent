namespace CadAgent.Core;

/// <summary>
/// 处理器可选实现：声明本步骤依赖的语义类。计划 1 几何处理器均不实现
/// （机制先行切片；真实语义需求随计划 3 布灯处理器落地）。
/// </summary>
public interface ISemanticAware
{
    IReadOnlyList<SemanticClass> RequiredSemantics { get; }
}

public sealed record GateDecision(bool Allowed, string Reason);

/// <summary>
/// 操作语义闸口：执行前校验所需语义是否已在叠加层确认。
/// Core 只定义契约（纯 BCL，无厂商依赖）；实现在 CadAgent.Data.Semantic。
/// </summary>
public interface ISemanticGate
{
    GateDecision Check(IReadOnlyList<SemanticClass> required);
}