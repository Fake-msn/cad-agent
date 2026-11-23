using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Llm;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Semantic;
using CadAgent.Llm;

namespace CadAgent.Cli;

/// <summary>
/// REPL 交互会话（3b-ii，§7）：持久 DrawingSession + ModelUndoStack + StepEngine。
/// 意图行→编排（stdin y/n 确认，--yes 自动）；:undo 步级撤回；:rollback 事务回滚；
/// :status 状态；:quit/EOF 退出。repl-session 起止审计。
/// </summary>
public static partial class CommandRouter
{
    internal static int ReplLoop(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string project = GetOption(args, "--project") ?? Directory.GetCurrentDirectory();
        string agentDir = Path.Combine(project, ".cadagent");
        Directory.CreateDirectory(agentDir);
        string session = GetOption(args, "--session") ?? Guid.NewGuid().ToString("N");
        string drawingPath = RequireOption(args, "--drawing");
        bool autoYes = args.Contains("--yes");

        var store = LlmConfigStore.Load(Path.Combine(agentDir, "llm-profiles.json"));
        string? profileName = GetOption(args, "--name") ?? store.ActiveProfileName;
        if (profileName == null) { stderr.WriteLine("无活动 profile，请先 llm use"); return 1; }
        var profile = store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile == null) { stderr.WriteLine($"profile 不存在: {profileName}"); return 1; }
        var protector = new DpapiKeyProtector(Path.Combine(agentDir, "keys"));
        string? key = DpapiKeyProtector.ResolveKey(protector, profile.Name);
        if (string.IsNullOrEmpty(key)) { stderr.WriteLine("无可用密钥"); return 1; }

        using var audit = OpenAudit(agentDir);
        var op = (IdentityOverride ?? Identity(agentDir)).Current;
        var drawing = LoadOrReject(() => DrawingSession.Load(drawingPath), drawingPath, session, audit, op, stderr, source: "repl");
        if (drawing == null) return 1;
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        var overlay = File.Exists(overlayPath)
            ? LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr, source: "repl")
            : null;
        if (File.Exists(overlayPath) && overlay == null) return 1;

        var undoStack = new ModelUndoStack();
        ISemanticGate? gate = overlay != null ? new SemanticGate(overlay) : null;
        var importHandler = SymbolImportHandler(args);
        var engine = Engine(agentDir, audit, gate, undoStack, importHandler);
        var meter = new UsageMeter(profile.Limits);
        var egress = new EgressPolicy(agentDir);
        var allowedKinds = HandlerRegistry.All().Select(h => h.Kind)
            .Concat(importHandler != null ? new[] { importHandler.Kind } : Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        ILlmClient client = LlmClientFactory?.Invoke(profile, key) ?? new OpenAiClient(profile, key);

        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
            "repl", "repl-session", drawingPath, JsonSerializer.Serialize(new { phase = "start", profile = profile.Name }),
            "", "", "", ""));
        stdout.WriteLine($"REPL 会话就绪（{Path.GetFileName(drawingPath)}，profile={profile.Name}）。输入意图或 :undo/:rollback/:status/:quit");

        Func<StepPlan, bool> confirmPlan = plan =>
        {
            if (autoYes) return true;
            stdout.WriteLine(JsonSerializer.Serialize(plan, SemanticJsonOpts));
            stderr.Write("确认执行? [y/N] ");
            string? answer = stdin.ReadLine();
            return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        };
        Func<bool> confirmEgress = () =>
        {
            if (autoYes) return true;
            stderr.Write($"首次向云端 {profile.Endpoint} 发送图纸数据，确认? [y/N] ");
            string? answer = stdin.ReadLine();
            return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        };

        while (true)
        {
            stderr.Write("> ");
            string? line = stdin.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line == ":quit") break;
            if (line == ":undo")
            {
                bool undone = engine.UndoLastStep(drawing, session);
                stdout.WriteLine(undone ? "已撤回上一步" : "无可撤回步骤");
                continue;
            }
            if (line.StartsWith(":rollback ", StringComparison.Ordinal))
            {
                string txId = line[":rollback ".Length..].Trim();
                try
                {
                    engine.Rollback(txId, drawing, session);
                    drawing.Reload();
                    stdout.WriteLine($"已回滚事务 {txId}");
                }
                catch (Exception ex) { stderr.WriteLine(ex.Message); }
                continue;
            }
            if (line == ":status")
            {
                stdout.WriteLine($"entities={drawing.Summarize().Entities.Count} undo={undoStack.Count} profile={profile.Name}");
                continue;
            }
            // 意图行→编排
            string summary = DrawingSummarizer.Summarize(drawing, overlay);
            var orch = new AgentOrchestrator(client, meter, egress, engine, audit,
                IdentityOverride ?? Identity(agentDir), profile.Model, profile);
            var result = orch.RunAsync(line, summary, drawing, session, allowedKinds, HandlerKindDocs.Default,
                confirmPlan, confirmEgress,
                postSummaryProvider: () => DrawingSummarizer.Summarize(drawing, overlay)).GetAwaiter().GetResult();
            string? liveOut = null;
            if (result.Status == OrchestrationStatus.Executed)
            {
                var live = LiveReplay(args, agentDir, session, audit, op, drawing, result.Plan!, LivePackages(args), stderr);
                liveOut = live.OutPath;
            }
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                status = result.Status.ToString(),
                txId = result.TxId,
                error = result.Error,
                verificationRounds = result.VerificationRounds,
                liveOut
            }, SemanticJsonOpts));
        }

        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
            "repl", "repl-session", drawingPath, JsonSerializer.Serialize(new { phase = "end" }),
            "", "", "", ""));
        return 0;
    }
}
