using System.Text.Json;

namespace CadAgent.Core.Llm;

/// <summary>编排结果状态。</summary>
public enum OrchestrationStatus { Executed, BlockedEgress, RejectedByHuman, Failed, Escalated, BudgetExceeded }

/// <summary>编排结果：状态/事务Id/计划/错误/累计用量/读回验证轮数（3i）。</summary>
public sealed record OrchestrationResult(OrchestrationStatus Status, string? TxId, StepPlan? Plan, string? Error, TokenUsage? TotalUsage, int VerificationRounds = 0);

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
    private readonly int _maxVerificationRounds;
    private int _attempt;

    public AgentOrchestrator(ILlmClient client, UsageMeter meter, EgressPolicy egress,
        StepEngine engine, IAuditStore audit, IIdentityProvider identity, string model, ProviderProfile profile,
        int maxVerificationRounds = 2)
    {
        _client = client;
        _meter = meter;
        _egress = egress;
        _engine = engine;
        _audit = audit;
        _identity = identity;
        _model = model;
        _profile = profile;
        _maxVerificationRounds = maxVerificationRounds;
    }

    public async Task<OrchestrationResult> RunAsync(string intent, string drawingSummary,
        IDrawingSession session, string sessionId, IReadOnlySet<string> allowedKinds, string kindDocs,
        Func<StepPlan, bool> confirmPlan, Func<bool>? confirmCloudEgress = null,
        Func<string>? postSummaryProvider = null, CancellationToken ct = default)
    {
        var op = _identity.Current;
        int promptTokens = 0, completionTokens = 0;
        _attempt = 0;

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
            AuditLlmCall(sessionId, resp.Usage, attempt: ++_attempt, planId: null);
            AuditAgent("agent-run", sessionId, new { status = "invalid-plan", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.Failed, null, null, ex.Message,
                new TokenUsage(promptTokens, completionTokens));
        }
        AuditLlmCall(sessionId, resp.Usage, attempt: ++_attempt, planId: plan.PlanId);

        // 5. 人工确认
        if (!confirmPlan(plan))
        {
            AuditAgent("plan-rejected", sessionId, new { planId = plan.PlanId });
            return new OrchestrationResult(OrchestrationStatus.RejectedByHuman, null, plan, "人工拒绝计划",
                new TokenUsage(promptTokens, completionTokens));
        }

        // 6. 执行
        string beforeHash = session.ComputeHash();
        var tx = _engine.Run(plan, session, sessionId);
        if (tx.Ok)
            return await VerifyLoopAsync(intent, drawingSummary, session, sessionId, allowedKinds, kindDocs,
                confirmPlan, plan, tx.TxId, tx.StepsDone, beforeHash, promptTokens, completionTokens,
                postSummaryProvider, ct);

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
            AuditLlmCall(sessionId, resp2.Usage, attempt: ++_attempt, planId: null);
            AuditAgent("agent-run", sessionId, new { status = "escalated", error = ex.Message });
            return new OrchestrationResult(OrchestrationStatus.Escalated, null, plan, ex.Message,
                new TokenUsage(promptTokens, completionTokens));
        }
        AuditLlmCall(sessionId, resp2.Usage, attempt: ++_attempt, planId: plan2.PlanId);
        if (!confirmPlan(plan2))
        {
            AuditAgent("plan-rejected", sessionId, new { planId = plan2.PlanId });
            return new OrchestrationResult(OrchestrationStatus.RejectedByHuman, null, plan2, "人工拒绝重规划",
                new TokenUsage(promptTokens, completionTokens));
        }
        string beforeHash2 = session.ComputeHash();
        var tx2 = _engine.Run(plan2, session, sessionId);
        if (tx2.Ok)
            return await VerifyLoopAsync(intent, drawingSummary, session, sessionId, allowedKinds, kindDocs,
                confirmPlan, plan2, tx2.TxId, tx2.StepsDone, beforeHash2, promptTokens, completionTokens,
                postSummaryProvider, ct);
        AuditAgent("agent-run", sessionId, new { status = "escalated", planId = plan2.PlanId, error = tx2.Error });
        return new OrchestrationResult(OrchestrationStatus.Escalated, null, plan2, tx2.Error,
            new TokenUsage(promptTokens, completionTokens));
    }

    /// <summary>
    /// 3i 读回迭代环（设计核心"读图→决策→操作→读回→迭代"的读回半环）：
    /// 执行成功后 post-summary+前后哈希 diff 喂回 LLM 验证；verdict ok→Executed；
    /// 纠正计划→白名单+人工确认+执行→再验证；有界 _maxVerificationRounds 轮。
    /// 预算耗尽→Executed+budget-skipped 审计（执行已提交，不谎报失败）；
    /// 验证不可解析/纠正执行失败/轮耗尽→Escalated（不信任完成，交人工）。
    /// postSummaryProvider 为 CLI 注入的完整摘要 seam；null→Core 最小 diff（不引用 Data）。
    /// </summary>
    private async Task<OrchestrationResult> VerifyLoopAsync(string intent, string preSummary,
        IDrawingSession session, string sessionId, IReadOnlySet<string> allowedKinds, string kindDocs,
        Func<StepPlan, bool> confirmPlan, StepPlan lastPlan, string txId, int stepsDone, string preHash,
        int promptTokens, int completionTokens, Func<string>? postSummaryProvider, CancellationToken ct)
    {
        for (int round = 1; round <= _maxVerificationRounds; round++)
        {
            try { _meter.EnsureBudget(); }
            catch (LlmBudgetExceededException ex)
            {
                AuditAgent("agent-verify", sessionId, new { verdict = "budget-skipped", round, error = ex.Message });
                AuditAgent("agent-run", sessionId, new { status = "executed", planId = lastPlan.PlanId, txId, verificationRounds = round - 1, verification = "budget-skipped" });
                return new OrchestrationResult(OrchestrationStatus.Executed, txId, lastPlan, null,
                    new TokenUsage(promptTokens, completionTokens), round - 1);
            }
            string postSummary = postSummaryProvider?.Invoke()
                ?? $"执行后哈希 {session.ComputeHash()}（执行前 {preHash}），完成 {stepsDone} 步";
            string verifySystem = "你是 CAD 制图验证智能体。判断执行结果是否满足用户意图。" +
                "满足只输出 {\"verdict\":\"ok\"}；不满足只输出纠正步骤计划 JSON，" +
                "格式 {\"steps\":[{\"kind\":\"...\",\"params\":{...}}]}。可用步骤类型：\n" + kindDocs;
            string verifyUser = $"意图：{intent}\n\n执行前摘要：\n{preSummary}\n\n执行后摘要：\n{postSummary}\n\n" +
                $"已执行步数：{stepsDone}（类型: {string.Join(", ", lastPlan.Steps.Select(s => s.Kind))}）";
            ChatResponse vr;
            try
            {
                vr = await _client.ChatAsync(new ChatRequest(new[]
                {
                    new ChatMessage("system", verifySystem),
                    new ChatMessage("user", verifyUser)
                }, _profile.Limits.MaxTokensPerCall), ct);
            }
            catch (LlmException ex)
            {
                AuditAgent("agent-verify", sessionId, new { verdict = "llm-failed", round, error = ex.Message });
                AuditAgent("agent-run", sessionId, new { status = "escalated", planId = lastPlan.PlanId, txId, error = ex.Message });
                return new OrchestrationResult(OrchestrationStatus.Escalated, txId, lastPlan, ex.Message,
                    new TokenUsage(promptTokens, completionTokens), round - 1);
            }
            promptTokens += vr.Usage.PromptTokens;
            completionTokens += vr.Usage.CompletionTokens;
            _meter.Record(vr.Usage);
            AuditLlmCall(sessionId, vr.Usage, attempt: ++_attempt, planId: null);

            bool ok;
            StepPlan? fix;
            try { (ok, fix) = AgentPlanParser.ExtractVerification(vr.Content, allowedKinds); }
            catch (InvalidPlanException ex)
            {
                AuditAgent("agent-verify", sessionId, new { verdict = "unparseable", round, error = ex.Message });
                AuditAgent("agent-run", sessionId, new { status = "escalated", planId = lastPlan.PlanId, txId, error = ex.Message });
                return new OrchestrationResult(OrchestrationStatus.Escalated, txId, lastPlan, ex.Message,
                    new TokenUsage(promptTokens, completionTokens), round - 1);
            }
            if (ok)
            {
                AuditAgent("agent-verify", sessionId, new { verdict = "ok", round });
                AuditAgent("agent-run", sessionId, new { status = "executed", planId = lastPlan.PlanId, txId, verificationRounds = round });
                return new OrchestrationResult(OrchestrationStatus.Executed, txId, lastPlan, null,
                    new TokenUsage(promptTokens, completionTokens), round);
            }
            AuditAgent("agent-verify", sessionId, new { verdict = "fix", round, fixPlanId = fix!.PlanId });
            if (!confirmPlan(fix))
            {
                AuditAgent("plan-rejected", sessionId, new { planId = fix.PlanId });
                return new OrchestrationResult(OrchestrationStatus.RejectedByHuman, txId, fix, "人工拒绝纠正计划",
                    new TokenUsage(promptTokens, completionTokens), round - 1);
            }
            string fixPreHash = session.ComputeHash();
            var ftx = _engine.Run(fix, session, sessionId);
            if (!ftx.Ok)
            {
                AuditAgent("agent-run", sessionId, new { status = "escalated", planId = fix.PlanId, txId = ftx.TxId, error = ftx.Error });
                return new OrchestrationResult(OrchestrationStatus.Escalated, ftx.TxId, fix, ftx.Error,
                    new TokenUsage(promptTokens, completionTokens), round - 1);
            }
            lastPlan = fix;
            txId = ftx.TxId;
            stepsDone = ftx.StepsDone;
            preHash = fixPreHash;
        }
        AuditAgent("agent-verify", sessionId, new { verdict = "rounds-exhausted", rounds = _maxVerificationRounds });
        AuditAgent("agent-run", sessionId, new { status = "escalated", planId = lastPlan.PlanId, txId, error = "读回验证轮耗尽" });
        return new OrchestrationResult(OrchestrationStatus.Escalated, txId, lastPlan, "读回验证轮耗尽，升级人工",
            new TokenUsage(promptTokens, completionTokens), _maxVerificationRounds);
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