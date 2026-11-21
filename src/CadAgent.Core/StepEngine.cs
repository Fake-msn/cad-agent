using System.Text.Json;

namespace CadAgent.Core;

public interface IDrawingSession
{
    string SourcePath { get; }
    string ComputeHash();
    void SaveWorking();
    void Reload();
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

    public StepEngine(IAuditStore audit, SnapshotStore snapshots, IIdentityProvider identity,
        RoleChecker roles, IEnumerable<IStepHandler> handlers, ISemanticGate? semanticGate = null)
    {
        _audit = audit;
        _snapshots = snapshots;
        _identity = identity;
        _roles = roles;
        _handlers = handlers.ToDictionary(h => h.Kind);
        _semanticGate = semanticGate;
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
                GateDecision decision;
                if (_semanticGate is ISemanticContextGate contextGate)
                {
                    if (step.Params.TryGetProperty("handle", out var hEl))
                    {
                        targetHandle = hEl.ValueKind switch
                        {
                            JsonValueKind.Number => hEl.GetUInt64().ToString(),
                            JsonValueKind.String => hEl.GetString(),
                            _ => null
                        };
                    }
                    decision = contextGate.CheckContext(aware.RequiredSemantics, targetHandle);
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
                            reason = decision.Reason
                        }),
                        beforeHash, beforeHash, snapshot, plan.PlanId);
                    return new TxResult(txId, false, done, decision.Reason);
                }
            }
            StepResult result;
            string afterHash = beforeHash;
            try
            {
                result = handler.Apply(session, step.Params);
                afterHash = session.ComputeHash();
            }
            catch (Exception ex) { result = new StepResult(false, ex.Message, ""); }

            if (!result.Ok)
            {
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

    private void Append(OperatorIdentity op, string session, string kind, string target,
        string paramsJson, string before, string after, string snapshot, string plan) =>
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "plan", kind, target, paramsJson, before, after, snapshot, plan));
}
