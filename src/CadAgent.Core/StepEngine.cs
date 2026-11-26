using System.Text.Json;

namespace CadAgent.Core;

public interface IDrawingSession
{
    string SourcePath { get; }
    string ComputeHash();
    void SaveWorking();
    void Reload();
    /// <summary>步级 undo（3b-ii 加性）：捕获当前模型状态（内存序列化，不触磁盘）。</summary>
    byte[] CaptureState();
    /// <summary>恢复捕获的模型状态。</summary>
    void RestoreState(byte[] state);
}

public sealed record StepSpec(string Kind, JsonElement Params);
public sealed record StepPlan(string PlanId, IReadOnlyList<StepSpec> Steps);
public sealed record StepResult(bool Ok, string Error, string TargetHandle);
public sealed record TxResult(string TxId, bool Ok, int StepsDone, string Error);

public sealed class InvalidPlanException : Exception
{
    public InvalidPlanException(string message) : base(message) { }
}

public static class StepPlanParser
{
    public static StepPlan Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidPlanException($"计划 JSON 非法: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("planId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new InvalidPlanException("计划缺少 planId");
            if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidPlanException("计划缺少 steps 数组");
            var steps = new List<StepSpec>();
            foreach (var s in stepsEl.EnumerateArray())
            {
                if (!s.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
                    throw new InvalidPlanException("步骤缺少 kind");
                JsonElement paramsEl;
                if (s.TryGetProperty("params", out var p))
                {
                    paramsEl = p.Clone();
                }
                else
                {
                    using var empty = JsonDocument.Parse("{}");
                    paramsEl = empty.RootElement.Clone();
                }
                steps.Add(new StepSpec(kindEl.GetString()!, paramsEl));
            }
            return new StepPlan(idEl.GetString()!, steps);
        }
    }
}

public interface IStepHandler
{
    string Kind { get; }
    StepResult Apply(IDrawingSession session, JsonElement parameters);
}

public sealed class StepEngine
{
    private readonly IAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly IIdentityProvider _identity;
    private readonly RoleChecker _roles;
    private readonly Dictionary<string, IStepHandler> _handlers;
    private readonly ISemanticGate? _semanticGate;
    private readonly ModelUndoStack? _undoStack;

    public StepEngine(IAuditStore audit, SnapshotStore snapshots, IIdentityProvider identity,
        RoleChecker roles, IEnumerable<IStepHandler> handlers, ISemanticGate? semanticGate = null,
        ModelUndoStack? undoStack = null)
    {
        _audit = audit;
        _snapshots = snapshots;
        _identity = identity;
        _roles = roles;
        _handlers = handlers.ToDictionary(h => h.Kind);
        _semanticGate = semanticGate;
        _undoStack = undoStack;
    }

    public TxResult Run(StepPlan plan, IDrawingSession session, string sessionId)
    {
        var op = _identity.Current;
        _roles.Ensure(op, Role.Draw);
        string txId = Guid.NewGuid().ToString("N");
        string snapshot = _snapshots.Take(session.SourcePath, txId);
        string beforeHash = session.ComputeHash();
        Append(op, sessionId, "tx-start", session.SourcePath, plan.PlanId, beforeHash, beforeHash, snapshot, plan.PlanId);

        int done = 0;
        foreach (var step in plan.Steps)
        {
            if (!_handlers.TryGetValue(step.Kind, out var handler))
            {
                string err = $"未注册的步骤类型: {step.Kind}";
                Append(op, sessionId, "step-failed", step.Kind, err, beforeHash, session.ComputeHash(), snapshot, plan.PlanId);
                return new TxResult(txId, false, done, err);
            }
            // 操作语义闸口（加性）：仅当注入了闸口且处理器声明了语义需求才检查；
            // 拒绝→留痕后立即终止，不执行该步、不 SaveWorking。
            if (_semanticGate != null && handler is ISemanticAware aware && aware.RequiredSemantics.Count > 0)
            {
                string? targetHandle = null;
                string? sheetId = null;
                GateDecision decision;
                if (_semanticGate is ISemanticContextGate contextGate)
                {
                    if (step.Params.TryGetProperty("handle", out var hEl))
                    {
                        switch (hEl.ValueKind)
                        {
                            case JsonValueKind.Number:
                                // R11①：TryGetUInt64 替代 GetUInt64 裸抛——负数/小数/指数/超 ulong
                                // （LLM 任意数值输入）不得 FormatException/OverflowException 逸出 Run
                                // （§9 全入审计）；失败→审计拒绝 fail-closed，合法整数路径逐字保持。
                                if (!hEl.TryGetUInt64(out ulong hNum))
                                    return RefuseInvalidNumericParam(op, sessionId, step, "handle", hEl, beforeHash, snapshot, plan, done, txId);
                                targetHandle = hNum.ToString();
                                break;
                            case JsonValueKind.String:
                                targetHandle = hEl.GetString();
                                break;
                            default:
                                targetHandle = null;
                                break;
                        }
                    }
                    // T4：params 含 "sheetId" → 三参幅面作用域校验（防跨幅跑偏）；无 sheetId → 两参 legacy。
                    // R12：number sheetId 镜像 handle 先例 number→decimal string 强制转换 →
                    // 作用域生效（不再 fail-open 静默走两参 legacy）；string/缺失/其余非法形态行为保持。
                    // R11①：number 非法值同 handle 口径审计拒绝（不裸抛）。
                    if (step.Params.TryGetProperty("sheetId", out var sEl))
                    {
                        switch (sEl.ValueKind)
                        {
                            case JsonValueKind.Number:
                                if (!sEl.TryGetUInt64(out ulong sNum))
                                    return RefuseInvalidNumericParam(op, sessionId, step, "sheetId", sEl, beforeHash, snapshot, plan, done, txId);
                                sheetId = sNum.ToString();
                                break;
                            case JsonValueKind.String:
                                sheetId = sEl.GetString();
                                break;
                            default:
                                sheetId = null;
                                break;
                        }
                    }
                    if (sheetId != null)
                    {
                        decision = contextGate.CheckContext(aware.RequiredSemantics, targetHandle, sheetId);
                    }
                    else
                    {
                        decision = contextGate.CheckContext(aware.RequiredSemantics, targetHandle);
                    }
                }
                else
                {
                    decision = _semanticGate.Check(aware.RequiredSemantics);
                }
                if (!decision.Allowed)
                {
                    Append(op, sessionId, "semantic-gate-refused", step.Kind,
                        JsonSerializer.Serialize(new
                        {
                            required = aware.RequiredSemantics.Select(c => c.ToString()).ToArray(),
                            handle = targetHandle,
                            sheetId,
                            reason = decision.Reason
                        }),
                        beforeHash, beforeHash, snapshot, plan.PlanId);
                    return new TxResult(txId, false, done, decision.Reason);
                }
            }
            _undoStack?.Push(session.CaptureState());
            StepResult result;
            string afterHash = beforeHash;
            try
            {
                result = handler.Apply(session, step.Params);
                // 项3 §5：成功步先失效哈希缓存，再算 afterHash（否则缓存吞掉变更）。
                if (result.Ok) (session as IHashRevision)?.InvalidateHash();
                afterHash = session.ComputeHash();
            }
            catch (Exception ex)
            {
                // 处理器可能「先变更再抛出」（如 ImportSymbolHandler 建 BlockRecord 后
                // Req("x") 抛 KeyNotFound）——失败路径同样失效哈希缓存再重算，
                // 否则缓存的 beforeHash 会冒充 afterHash，审计丢失已发生的变更。
                (session as IHashRevision)?.InvalidateHash();
                afterHash = session.ComputeHash();
                result = new StepResult(false, ex.Message, "");
            }

            if (!result.Ok)
            {
                // 返回失败（非异常）不在此失效（spec §5）：失败调用方自行 Reload；
                // 异常路径已在上方失效并重算 afterHash（可能已半改模型）。
                Append(op, sessionId, "step-failed", result.TargetHandle,
                    JsonSerializer.Serialize(new { step.Kind, result.Error }), beforeHash, afterHash, snapshot, plan.PlanId);
                return new TxResult(txId, false, done, result.Error);
            }
            Append(op, sessionId, "step", result.TargetHandle,
                JsonSerializer.Serialize(new { step.Kind, Params = step.Params }), beforeHash, afterHash, snapshot, plan.PlanId);
            beforeHash = afterHash;
            done++;
        }
        try
        {
            session.SaveWorking();
            Append(op, sessionId, "tx-commit", session.SourcePath, plan.PlanId, beforeHash, session.ComputeHash(), snapshot, plan.PlanId);
        }
        catch (Exception ex)
        {
            // 终结记录：落盘或 tx-commit 审计失败也必须留下失败形态（tx-failed 自身 Append 失败则任其传播——审计存储故障不可恢复）
            Append(op, sessionId, "tx-failed", session.SourcePath,
                JsonSerializer.Serialize(new { plan.PlanId, Error = ex.Message }), beforeHash, beforeHash, snapshot, plan.PlanId);
            return new TxResult(txId, false, done, ex.Message);
        }
        return new TxResult(txId, true, done, "");
    }

    public void Rollback(string txId, IDrawingSession session, string sessionId)
    {
        var op = _identity.Current;
        _roles.Ensure(op, Role.Draw);
        string before = session.ComputeHash();
        try
        {
            _snapshots.Restore(txId, session.SourcePath);
            session.Reload();
        }
        catch (Exception ex)
        {
            // 无旁路：Restore/Reload 失败也须留下审计痕迹，再向上抛出
            Append(op, sessionId, "rollback-failed", session.SourcePath,
                JsonSerializer.Serialize(new { TxId = txId, Error = ex.Message }), before, before, txId, "");
            throw;
        }
        Append(op, sessionId, "rollback", session.SourcePath, txId, before, session.ComputeHash(), txId, "");
    }

    /// <summary>
    /// 步级 undo（3b-ii）：弹出最近一步执行前的模型快照并恢复，审计 step-undone。
    /// 空栈→false（不审计）。会话内有效；跨会话撤回靠事务快照 Rollback。
    /// </summary>
    public bool UndoLastStep(IDrawingSession session, string sessionId)
    {
        if (_undoStack == null) return false;
        byte[]? state = _undoStack.Pop();
        if (state == null) return false;
        var op = _identity.Current;
        _roles.Ensure(op, Role.Draw);
        string before = session.ComputeHash();
        session.RestoreState(state);
        Append(op, sessionId, "step-undone", session.SourcePath,
            JsonSerializer.Serialize(new { remaining = _undoStack.Count }), before, session.ComputeHash(), "", "");
        return true;
    }

    /// <summary>
    /// R11①：数值 handle/sheetId 非法（TryGetUInt64 失败：负数/小数/指数/溢出）→
    /// step-failed 审计拒绝 + TxResult(false) fail-closed（不执行该步、不 SaveWorking）。
    /// tx-start 已开组 → step-failed 按既有引擎语义闭合（失败步即事务终态，无 tx-commit）；
    /// params 携带非法值原文（GetRawText）+reason，审计者可复现 LLM 输入。
    /// </summary>
    private TxResult RefuseInvalidNumericParam(OperatorIdentity op, string sessionId, StepSpec step,
        string field, JsonElement el, string beforeHash, string snapshot, StepPlan plan, int done, string txId)
    {
        string reason = $"非法数值 {field}: {el.GetRawText()}（须为非负整数）";
        Append(op, sessionId, "step-failed", step.Kind,
            JsonSerializer.Serialize(new { step.Kind, field, raw = el.GetRawText(), reason }),
            beforeHash, beforeHash, snapshot, plan.PlanId);
        return new TxResult(txId, false, done, reason);
    }

    private void Append(OperatorIdentity op, string session, string kind, string target,
        string paramsJson, string before, string after, string snapshot, string plan) =>
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "plan", kind, target, paramsJson, before, after, snapshot, plan));
}
