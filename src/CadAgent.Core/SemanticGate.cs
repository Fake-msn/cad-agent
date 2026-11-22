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

/// <summary>
/// 上下文闸口（计划 3a 加性）：实体级语义检查——步骤目标句柄须携带所需类标注。
/// targetHandle null → 退化为操作级 Check 语义。闸口自持叠加层（引擎不持有）。
/// 实现 ISemanticContextGate 的闸口被引擎优先以 CheckContext 调用。
/// </summary>
public interface ISemanticContextGate : ISemanticGate
{
    GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle);

    /// <summary>
    /// 幅面作用域（T4 加性）：sheetId 非 null 时所需类标注须归属该幅面
    /// （SheetId==sheetId）或为 legacy 全局（SheetId==null）；targetHandle null →
    /// 操作级同幅过滤。默认实现委托两参——仅实现两参的既有闸口/fake 不感知幅面亦不破坏。
    /// </summary>
    GateDecision CheckContext(IReadOnlyList<SemanticClass> required, string? targetHandle, string? sheetId)
        => CheckContext(required, targetHandle);
}
