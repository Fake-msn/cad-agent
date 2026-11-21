using System.Text.Json;

namespace CadAgent.Core.Llm;

/// <summary>编排结果状态。</summary>
public enum OrchestrationStatus { Executed, BlockedEgress, RejectedByHuman, Failed, Escalated, BudgetExceeded }

/// <summary>编排结果：状态/事务Id/计划/错误/累计用量。</summary>
public sealed record OrchestrationResult(OrchestrationStatus Status, string? TxId, StepPlan? Plan, string? Error, TokenUsage? TotalUsage);

/// <summary>
/// 智能体编排核心（§3 边界2）：LLM 仅产计划 JSON；一切修改经 StepEngine（审计/快照/闸口）。
/// 出境闸（§8.5）→预算前置（§8.4）→读图摘要+意图→LLM 计划→schema+kind 校验→人工确认→
/// 执行→失败一次重规划（错误反馈）→仍失败升级人工。llm-call 全程审计（无图纸内容/密钥）。
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly ILlmClient _client;
    private readonly UsageMeter _meter;
    private readonly EgressPolicy _egress;
    private readonly StepEngine _engine;
    private readonly IAuditStore _audit;
    private readonly IIdentityProvider _identity;
    private readonly string _model;
    private readonly ProviderProfile _profile;

    public AgentOrchestrator(ILlmClient client, UsageMeter meter, EgressPolicy egress,
        StepEngine engine, IAuditStore audit, IIdentityProvider identity, string model, ProviderProfile profile)
    {
        _client = client;
        _meter = meter;
        _egress = egress;
        _engine = engine;
        _audit = audit;
        _identity = identity;
        _model = model;
        _profile = profile;
    }

    public async Task<OrchestrationResult> RunAsync(string intent, string drawingSummary,
        IDrawingSession session, string sessionId, IReadOnlySet<string> allowedKinds, string kindDocs,
        Func<StepPlan, bool> confirmPlan, Func<bool>? confirmCloudEgress = null, CancellationToken ct = default)
    {
        var op = _identity.Current;
        int promptTokens = 0, completionTokens = 0;

        // 1. 出境闸
        var decision = _egress.Evaluate(_profile, drawingData: true);
        if (decision == EgressDecision.BlockedLocalOnly)
        {
            AuditAgent("egress-blocked", sessionId, new { reason = "local-only 约束", profile = _profile.Name });
            return new OrchestrationResult(OrchestrationStatus.BlockedEgress, null, null, "数据出境被本地约束阻止", null);
        }
        if (decision == EgressDecision.NeedsFirstCloudConsent)
        {
            bool consent = confirmCloudEgress?.Invoke() ?? false;
            if (!consent)
            {
                AuditAgent("egress-blocked", sessionId, new { reason = "首次云端出境未获确认", profile = _profile.Name });
                return new OrchestrationResult(OrchestrationStatus.BlockedEgress, null, null, "首次云端出境未获确认", null);
            }
            _egress.ConfirmCloudConsent();
            AuditAgent("egress-consent", sessionId, new { profile = _profile.Name, endpoint = _profile.Endpoint });
        }

        // 2. 预算前置
        try { _meter.EnsureBudget(); }
        catch (LlmBudgetExceededException ex)
        {
            AuditAgent("agent-run", sessionId, new { status = "budget-exceeded", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.BudgetExceeded, null, null, ex.Message, null);
        }

        // 3. 首次计划生成
        string systemPrompt = "你是 CAD 制图智能体。根据用户意图与图纸摘要，只输出一个 JSON 步骤计划对象，" +
            "格式 {\"steps\":[{\"kind\":\"...\",\"params\":{...}}]}，不要输出任何其他文字。" +
            "可用步骤类型及参数：\n" + kindDocs;
        string userPrompt = $"意图：{intent}\n\n图纸摘要：\n{drawingSummary}";
        ChatResponse resp;
        try
        {
            resp = await _client.ChatAsync(new ChatRequest(new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            }, _profile.Limits.MaxTokensPerCall), ct);
        }
        catch (LlmException ex)
        {
            AuditAgent("agent-run", sessionId, new { status = "failed", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.Failed, null, null, ex.Message, null);
        }
        promptTokens += resp.Usage.PromptTokens;
        completionTokens += resp.Usage.CompletionTokens;
        _meter.Record(resp.Usage);

        // 4. 解析校验（失败→Failed，不重规划）
        StepPlan plan;
        try { plan = AgentPlanParser.ExtractPlan(resp.Content, allowedKinds); }
        catch (InvalidPlanException ex)
        {
            AuditLlmCall(sessionId, resp.Usage, attempt: 1, planId: null);
            AuditAgent("agent-run", sessionId, new { status = "invalid-plan", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.Failed, null, null, ex.Message,
                new TokenUsage(promptTokens, completionTokens));
        }
        AuditLlmCall(sessionId, resp.Usage, attempt: 1, planId: plan.PlanId);

        // 5. 人工确认
        if (!confirmPlan(plan))
        {
            AuditAgent("plan-rejected", sessionId, new { planId = plan.PlanId });
            return new OrchestrationResult(OrchestrationStatus.RejectedByHuman, null, plan, "人工拒绝计划",
                new TokenUsage(promptTokens, completionTokens));
        }

        // 6. 执行
        var tx = _engine.Run(plan, session, sessionId);
        if (tx.Ok)
        {
            AuditAgent("agent-run", sessionId, new { status = "executed", planId = plan.PlanId, txId = tx.TxId });
            return new OrchestrationResult(OrchestrationStatus.Executed, tx.TxId, plan, null,
                new TokenUsage(promptTokens, completionTokens));
        }

        // 7. 有界重规划（一次）：错误反馈→新计划→再确认→再执行
        try { _meter.EnsureBudget(); }
        catch (LlmBudgetExceededException ex)
        {
            AuditAgent("agent-run", sessionId, new { status = "budget-exceeded", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.BudgetExceeded, null, plan, ex.Message,
                new TokenUsage(promptTokens, completionTokens));
        }
        ChatResponse resp2;
        try
        {
            resp2 = await _client.ChatAsync(new ChatRequest(new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
                new ChatMessage("assistant", resp.Content),
                new ChatMessage("user", $"上次计划执行失败（已完成 {tx.StepsDone} 步）：{tx.Error}。请修正计划，仍只输出 JSON。")
            }, _profile.Limits.MaxTokensPerCall), ct);
        }
        catch (LlmException ex)
        {
            AuditAgent("agent-run", sessionId, new { status = "escalated", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.Escalated, null, plan, ex.Message,
                new TokenUsage(promptTokens, completionTokens));
        }
        promptTokens += resp2.Usage.PromptTokens;
        completionTokens += resp2.Usage.CompletionTokens;
        _meter.Record(resp2.Usage);

        StepPlan plan2;
        try { plan2 = AgentPlanParser.ExtractPlan(resp2.Content, allowedKinds); }
        catch (InvalidPlanException ex)
        {
            AuditLlmCall(sessionId, resp2.Usage, attempt: 2, planId: null);
            AuditAgent("agent-run", sessionId, new { status = "escalated", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.Escalated, null, plan, ex.Message,
                new TokenUsage(promptTokens, completionTokens));
        }
        AuditLlmCall(sessionId, resp2.Usage, attempt: 2, planId: plan2.PlanId);
        if (!confirmPlan(plan2))
        {
            AuditAgent("plan-rejected", sessionId, new { planId = plan2.PlanId });
            return new OrchestrationResult(OrchestrationStatus.RejectedByHuman, null, plan2, "人工拒绝重规划",
                new TokenUsage(promptTokens, completionTokens));
        }
        var tx2 = _engine.Run(plan2, session, sessionId);
        if (tx2.Ok)
        {
            AuditAgent("agent-run", sessionId, new { status = "executed", planId = plan2.PlanId, txId = tx2.TxId, replanned = true });
            return new OrchestrationResult(OrchestrationStatus.Executed, tx2.TxId, plan2, null,
                new TokenUsage(promptTokens, completionTokens));
        }
        AuditAgent("agent-run", sessionId, new { status = "escalated", planId = plan2.PlanId, error = tx2.Error });
        return new OrchestrationResult(OrchestrationStatus.Escalated, null, plan2, tx2.Error,
            new TokenUsage(promptTokens, completionTokens));
    }

    private void AuditLlmCall(string sessionId, TokenUsage usage, int attempt, string? planId)
    {
        var op = _identity.Current;
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), sessionId,
            "llm", "llm-call", _model,
            JsonSerializer.Serialize(new { model = _model, usage.PromptTokens, usage.CompletionTokens, attempt, planId }),
            "", "", "", ""));
    }

    private void AuditAgent(string kind, string sessionId, object payload)
    {
        var op = _identity.Current;
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), sessionId,
            "agent", kind, _profile.Name, JsonSerializer.Serialize(payload), "", "", "", ""));
    }
}