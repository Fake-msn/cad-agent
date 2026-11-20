using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;

namespace CadAgent.Cli;

public static class CommandRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0) return Usage(stderr);
            string project = GetOption(args, "--project") ?? Directory.GetCurrentDirectory();
            string agentDir = Path.Combine(project, ".cadagent");
            Directory.CreateDirectory(agentDir);
            string session = GetOption(args, "--session") ?? Guid.NewGuid().ToString("N");
            // 去掉选项名及其值（--project/--session/--drawing 后跟的值）
            var positional = StripOptionValues(args);

            switch (positional[0])
            {
                case "intake": return Intake(positional[1], agentDir, session, stdout, stderr);
                case "run": return RunPlan(positional[1], RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "rollback": return Rollback(positional[1], RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "audit": return AuditVerify(agentDir, stdout, stderr);
                case "roles": return RolesShow(agentDir, stdout);
                default: return Usage(stderr);
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Gate-at-run policy (spec hard-req 6): Red drawings are information-incomplete
    /// and must not be operated on. Yellow proceeds with a warning (semantic-level
    /// interception is Plan 2 policy).
    /// </summary>
    internal static bool GateRefusesRun(GateReport report) => report.Level == GateLevel.Red;

    private static string[] StripOptionValues(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) { i++; continue; }
            list.Add(args[i]);
        }
        return list.ToArray();
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string RequireOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException($"缺少必需选项 {name}");

    private static SqliteAuditStore OpenAudit(string agentDir) =>
        new(Path.Combine(agentDir, "audit.db"));

    private static IIdentityProvider Identity(string agentDir) =>
        new WindowsIdentityProvider(RoleConfig.Load(Path.Combine(agentDir, "roles.yaml")));

    private static int Intake(string file, string agentDir, string session, TextWriter stdout, TextWriter stderr)
    {
        // 审计先行（spec §9 错误处理全部入审计）：Load/Inspect 失败也必须留痕。
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        DrawingSession drawing;
        DrawingSummary summary;
        GateReport report;
        try
        {
            drawing = DrawingSession.Load(file);
            summary = drawing.Summarize();
            report = GateDetector.Inspect(summary, new EmptySupportMatrix());
        }
        catch (Exception ex)
        {
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "intake", "intake-rejected", file,
                JsonSerializer.Serialize(new { Error = ex.Message }), "", "", "", ""));
            stderr.WriteLine(ex.Message);
            return 1;
        }
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "intake", "intake", file,
            JsonSerializer.Serialize(new { summary.Version, Entities = summary.Entities.Count, Unknowns = summary.Unknowns.Count }),
            drawing.ComputeHash(), drawing.ComputeHash(), "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            level = report.Level.ToString(),
            unsupported = report.Unsupported,
            guidance = report.Guidance,
            summary
        }, JsonOpts));
        return report.Level switch { GateLevel.Green => 0, GateLevel.Yellow => 2, _ => 3 };
    }

    private static StepEngine Engine(string agentDir, IAuditStore audit) =>
        new(audit, new SnapshotStore(Path.Combine(agentDir, "snapshots")),
            Identity(agentDir), new RoleChecker(), HandlerRegistry.All());

    private static int RunPlan(string planPath, string drawingPath, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        var plan = StepPlanParser.Parse(File.ReadAllText(planPath));
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        DrawingSession drawing;
        GateReport report;
        try
        {
            drawing = DrawingSession.Load(drawingPath);
            report = GateDetector.Inspect(drawing.Summarize(), new EmptySupportMatrix());
        }
        catch (Exception ex)
        {
            // 读图失败同样拒绝执行并留痕（哈希不可得→空串）。
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "run", "run-rejected", drawingPath,
                JsonSerializer.Serialize(new { level = "LoadFailed", unsupportedCount = 0, error = ex.Message }),
                "", "", "", ""));
            stderr.WriteLine(ex.Message);
            return 1;
        }
        if (GateRefusesRun(report))
        {
            // spec 硬要求6：信息不完整（Red）图纸禁止操作——不执行任何步骤。
            string hash = drawing.ComputeHash();
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "run", "run-rejected", drawingPath,
                JsonSerializer.Serialize(new { level = report.Level.ToString(), unsupportedCount = report.Unsupported.Count }),
                hash, hash, "", ""));
            stderr.WriteLine(report.Guidance);
            return 1;
        }
        if (report.Level == GateLevel.Yellow)
        {
            // Yellow 放行+警示（语义级拦截属计划2的既定策略）。
            stderr.WriteLine($"警告: 图纸含 {report.Unsupported.Count} 个未支持对象（Yellow 放行）。{report.Guidance}");
        }
        var result = Engine(agentDir, audit).Run(plan, drawing, session);
        if (!result.Ok) drawing.Reload();
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            txId = result.TxId, ok = result.Ok, stepsDone = result.StepsDone, error = result.Error
        }, JsonOpts));
        return result.Ok ? 0 : 1;
    }

    private static int Rollback(string txId, string drawingPath, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        var drawing = DrawingSession.Load(drawingPath);
        // 快照必须属于当前图纸：防止把别的图纸的快照覆盖到本图纸上
        string snapshotFile = new SnapshotStore(Path.Combine(agentDir, "snapshots")).Find(txId);
        if (!string.Equals(Path.GetFileName(snapshotFile), Path.GetFileName(drawing.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            // 拒绝也必须入审计（spec §9）：谁、撤哪个事务、期望/实际图纸各是什么。
            using var rejectedAudit = OpenAudit(agentDir);
            var op = Identity(agentDir).Current;
            rejectedAudit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "rollback", "rollback-rejected", txId,
                JsonSerializer.Serialize(new
                {
                    Expected = Path.GetFileName(drawing.SourcePath),
                    Actual = Path.GetFileName(snapshotFile)
                }),
                "", "", txId, ""));
            stderr.WriteLine($"事务 {txId} 的快照不属于图纸 {drawing.SourcePath}，已拒绝撤回");
            return 1;
        }
        using var audit = OpenAudit(agentDir);
        Engine(agentDir, audit).Rollback(txId, drawing, session);
        stdout.WriteLine($"已撤回事务 {txId}");
        return 0;
    }

    private static int AuditVerify(string agentDir, TextWriter stdout, TextWriter stderr)
    {
        using var audit = OpenAudit(agentDir);
        if (audit.VerifyChain(out string error))
        {
            stdout.WriteLine($"审计链完整，共 {audit.ReadAll().Count} 条记录");
            return 0;
        }
        stderr.WriteLine(error);
        return 1;
    }

    private static int RolesShow(string agentDir, TextWriter stdout)
    {
        var op = Identity(agentDir).Current;
        stdout.WriteLine($"{op.Account}\t{op.Role}");
        return 0;
    }

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("用法: cadagent <intake|run|rollback|audit verify|roles show> [参数] [--project <dir>]");
        return 1;
    }
}