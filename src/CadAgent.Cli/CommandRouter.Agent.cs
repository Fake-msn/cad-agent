using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Llm;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Semantic;
using CadAgent.Llm;

namespace CadAgent.Cli;

/// <summary>
/// agent 命令（3b-i）：完整编排闭环 CLI 入口。
/// active/指定 profile + DPAPI key → 图纸摘要 → Orchestrator（出境闸/确认/执行/重规划）。
/// --yes 自动确认（审计注记）；交互确认经 stdin（手动冒烟路径）。
/// </summary>
public static partial class CommandRouter
{
    private static int Agent(string[] args, string agentDir, string session, TextWriter stdout, TextWriter stderr)
    {
        string drawingPath = RequireOption(args, "--drawing");
        string intent = RequireOption(args, "--intent");
        bool autoYes = args.Contains("--yes");

        var store = LlmConfigStore.Load(Path.Combine(agentDir, "llm-profiles.json"));
        string? profileName = GetOption(args, "--name") ?? store.ActiveProfileName;
        if (profileName == null) { stderr.WriteLine("无活动 profile，请先 llm use 或 --name 指定"); return 1; }
        var profile = store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile == null) { stderr.WriteLine($"profile 不存在: {profileName}"); return 1; }
        var protector = new DpapiKeyProtector(Path.Combine(agentDir, "keys"));
        string? key = DpapiKeyProtector.ResolveKey(protector, profile.Name);
        if (string.IsNullOrEmpty(key)) { stderr.WriteLine("无可用密钥（llm add --key 或 CADAGENT_LLM_API_KEY）"); return 1; }

        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var drawing = LoadOrReject(() => LoadWithCensus(drawingPath, agentDir), drawingPath, session, audit, op, stderr, source: "agent");
        if (drawing == null) return 1;
        // 准入闸（防假 Green T3 缺口闭合）：agent 路径此前无 GateDetector 检查——
        // Yellow/Red 一律先于 LLM 调用拒绝（run-rejected 留痕，spec ⑥/§9；只读审查全图级拦截）。
        var gateReport = GateDetector.Inspect(drawing.Summarize(), new EmptySupportMatrix());
        if (GateRefusesRun(gateReport))
        {
            string gateHash = drawing.ComputeHash();
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "agent", "run-rejected", drawingPath,
                JsonSerializer.Serialize(new
                {
                    level = gateReport.Level.ToString(),
                    unsupportedCount = gateReport.Unsupported.Count,
                    unparsedCount = drawing.Unparsed.TotalCount,
                    guidance = gateReport.Guidance
                }),
                gateHash, gateHash, "", ""));
            stderr.WriteLine(gateReport.Guidance);
            return 1;
        }
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        var overlay = File.Exists(overlayPath)
            ? LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr, source: "agent")
            : null;
        if (File.Exists(overlayPath) && overlay == null) return 1;

        string summary = DrawingSummarizer.Summarize(drawing, overlay);
        var importHandler = SymbolImportHandler(args);
        var allowedKinds = HandlerRegistry.All().Select(h => h.Kind)
            .Concat(importHandler != null ? new[] { importHandler.Kind } : Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        ISemanticGate? gate = overlay != null ? new SemanticGate(overlay) : null;

        ILlmClient client = LlmClientFactory?.Invoke(profile, key) ?? new OpenAiClient(profile, key);
        var orch = new AgentOrchestrator(client, new UsageMeter(profile.Limits), new EgressPolicy(agentDir),
            Engine(agentDir, audit, gate, null, importHandler), audit, Identity(agentDir), profile.Model, profile);

        Func<StepPlan, bool> confirmPlan = plan =>
        {
            if (autoYes) return true;
            stdout.WriteLine(JsonSerializer.Serialize(plan, SemanticJsonOpts));
            stderr.Write("确认执行此计划? [y/N] ");
            string? answer = Console.ReadLine();
            return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        };
        Func<bool> confirmEgress = () =>
        {
            if (autoYes) return true;
            stderr.Write($"首次向云端端点 {profile.Endpoint} 发送图纸数据，确认? [y/N] ");
            string? answer = Console.ReadLine();
            return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        };

        var result = orch.RunAsync(intent, summary, drawing, session, allowedKinds, HandlerKindDocs.Default,
            confirmPlan, confirmEgress,
            postSummaryProvider: () => DrawingSummarizer.Summarize(drawing, overlay)).GetAwaiter().GetResult();
        string? liveOut = null;
        bool liveOk = true;
        if (result.Status == OrchestrationStatus.Executed)
        {
            var live = LiveReplay(args, agentDir, session, audit, op, drawing, result.Plan!, LivePackages(args), stderr);
            liveOk = live.Ok;
            liveOut = live.OutPath;
        }
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            status = result.Status.ToString(),
            txId = result.TxId,
            error = result.Error,
            verificationRounds = result.VerificationRounds,
            liveOut,
            usage = result.TotalUsage == null ? null : new { result.TotalUsage.PromptTokens, result.TotalUsage.CompletionTokens }
        }, SemanticJsonOpts));
        return result.Status == OrchestrationStatus.Executed && liveOk ? 0 : 1;
    }
}
