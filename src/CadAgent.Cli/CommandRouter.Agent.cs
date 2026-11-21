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
    private const string KindDocs = """
        add-line: 画直线 {x1,y1,x2,y2: number, layer?: string}
        add-text: 写单行文字 {x,y: number, text: string, height: number(mm), layer?: string}
        delete-entity: 删除实体 {handle: number|string}
        move-entity: 移动实体 {handle: number|string, dx,dy: number}（仅 Line/Text）
        change-layer: 改图层 {handle: number|string, layer: string}（不存在则创建）
        insert-block: 插入图纸内已有块 {block: string, x,y: number, scale?: number, rotation?: number(弧度)}
        layout-lighting: 房间网格布灯 {x,y,width,depth,spacing: number(mm), block: string, layer?: string}（需已确认 Room 语义；点位=区域内 spacing 网格）
        label-luminaires: 灯具编号/设备索引符号 {prefix: string(品种代号,归院标), block?: string, start?: number, symbolSize?: number(8~12mm,默认10), layer?: string}（需已确认 Lighting 语义；正六边形符号 JGJ/T 244-2011 3.6.6-4）
        insert-symbol: 跨文档导入 skill 符号并插入 {symbol: string(符号名,须 --skill 包内), x,y: number, scale?: number, rotation?: number(弧度), layer?: string}（建块/复用块；Line/Circle/Arc/Text 白名单）
        """;

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
        var drawing = LoadOrReject(() => DrawingSession.Load(drawingPath), drawingPath, session, audit, op, stderr, source: "agent");
        if (drawing == null) return 1;
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

        var result = orch.RunAsync(intent, summary, drawing, session, allowedKinds, KindDocs,
            confirmPlan, confirmEgress,
            postSummaryProvider: () => DrawingSummarizer.Summarize(drawing, overlay)).GetAwaiter().GetResult();
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            status = result.Status.ToString(),
            txId = result.TxId,
            error = result.Error,
            verificationRounds = result.VerificationRounds,
            usage = result.TotalUsage == null ? null : new { result.TotalUsage.PromptTokens, result.TotalUsage.CompletionTokens }
        }, SemanticJsonOpts));
        return result.Status == OrchestrationStatus.Executed ? 0 : 1;
    }
}