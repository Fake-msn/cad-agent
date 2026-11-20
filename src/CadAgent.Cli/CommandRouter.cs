using System.Text.Json;
using System.Text.Json.Serialization;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Checks;
using CadAgent.Data.Handlers;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;

namespace CadAgent.Cli;

public static class CommandRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // semantic 族 stdout 契约（Task 8 E2E 依赖）：camelCase 属性名 + 枚举字符串原样
    // （HumanConfirmed/Wall 等 PascalCase 枚举值不加命名策略）。
    // 放宽转义：中文图层名按字面输出（默认编码器会转成 \uXXXX，破坏 Contains 断言）；
    // stdout 仅供终端/管道消费，无 HTML 注入面。
    private static readonly JsonSerializerOptions SemanticJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

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
                case "semantic": return Semantic(args, positional, agentDir, session, stdout, stderr);
                case "check": return Check(RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "rule": return Rule(args, positional, agentDir, session, stdout, stderr);
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

    private static StepEngine Engine(string agentDir, IAuditStore audit, ISemanticGate? semanticGate = null) =>
        new(audit, new SnapshotStore(Path.Combine(agentDir, "snapshots")),
            Identity(agentDir), new RoleChecker(), HandlerRegistry.All(), semanticGate);

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
        // 语义闸口接线：overlay 存在→加载注入；不存在→null（几何级流程不受影响，诚实降级）。
        // 存在但损坏→审计拒绝、不执行（最终审查裁定：不得静默降级为 null 闸口）。
        string overlayPath = SemanticOverlayPath(agentDir);
        ISemanticGate? semanticGate = null;
        if (File.Exists(overlayPath))
        {
            var overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
            if (overlay is null) return 1;
            semanticGate = new SemanticGate(overlay);
        }
        var result = Engine(agentDir, audit, semanticGate).Run(plan, drawing, session);
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

    private static string SemanticOverlayPath(string agentDir) =>
        Path.Combine(agentDir, "semantic.json");

    /// <summary>
    /// semantic 族加载守卫（spec §9 错误处理全部入审计；最终审查裁定）：
    /// overlay/drawing Load 失败复用 intake-rejected 模式留痕（Source 默认 semantic，
    /// check 族传 "check"；target=失败文件，哈希不可得→空串），stderr + 调用方 exit 1。
    /// 返回 null 表示已审计拒绝。
    /// </summary>
    private static T? LoadOrReject<T>(Func<T> load, string file, string session,
        SqliteAuditStore audit, OperatorIdentity op, TextWriter stderr, string source = "semantic") where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, source, "intake-rejected", file,
                JsonSerializer.Serialize(new { Error = ex.Message, File = file }), "", "", "", ""));
            stderr.WriteLine(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// semantic 命令族分发。子命令校验仅对本族落实（计划 1 遗留项的既定范围）：
    /// 缺失/未知子命令一律 stderr + exit 1，不落入 Usage 歧义。
    /// </summary>
    private static int Semantic(string[] args, string[] positional, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2)
        {
            stderr.WriteLine("semantic 需要子命令: scan|show|map");
            return 1;
        }
        return positional[1] switch
        {
            "scan" => SemanticScan(RequireOption(args, "--drawing"), agentDir, session, stdout, stderr),
            "show" => SemanticShow(agentDir, stdout),
            "map" => SemanticMap(args, agentDir, session, stdout, stderr),
            _ => UnknownSemanticSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownSemanticSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 semantic 子命令: {sub}（可用: scan|show|map）");
        return 1;
    }

    /// <summary>
    /// semantic scan：Load→Scan(既有 overlay)→Save→审计 semantic-scan→stdout
    /// {version, annotations, ambiguous}。Load 失败复用计划 1 intake-rejected
    /// 模式（错误入审计、哈希不可得→空串）+ exit 1。
    /// </summary>
    private static int SemanticScan(string drawingPath, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        string overlayPath = SemanticOverlayPath(agentDir);
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        DrawingSession drawing;
        try
        {
            drawing = DrawingSession.Load(drawingPath);
        }
        catch (Exception ex)
        {
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "semantic", "intake-rejected", drawingPath,
                JsonSerializer.Serialize(new { Error = ex.Message }), "", "", "", ""));
            stderr.WriteLine(ex.Message);
            return 1;
        }
        var existing = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
        if (existing is null) return 1;
        var result = SemanticScanner.Scan(drawing, existing);
        result.Overlay.Save(overlayPath);
        // 扫描不改图纸：前后哈希一致（沿用 intake 的诚实留痕）。
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "semantic", "semantic-scan", drawingPath,
            JsonSerializer.Serialize(new
            {
                annotationCount = result.Overlay.Annotations.Count,
                ambiguousCount = result.Ambiguous.Count,
                version = result.Overlay.Version
            }),
            hash, hash, "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            version = result.Overlay.Version,
            annotations = result.Overlay.Annotations,
            ambiguous = result.Ambiguous
        }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>
    /// semantic show：只读摘要（version/annotations/mappings）。无 overlay 时
    /// Load 返回空叠加层→空摘要 exit 0；只读命令不入审计（沿用 roles show）。
    /// </summary>
    private static int SemanticShow(string agentDir, TextWriter stdout)
    {
        var overlay = SemanticOverlay.Load(SemanticOverlayPath(agentDir));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            version = overlay.Version,
            annotations = overlay.Annotations,
            mappings = overlay.Mappings
        }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>
    /// semantic map：人工确认图层映射（控制器裁定：必须 --drawing 以取该层实体
    /// 句柄升级标注）。无 overlay→提示先 scan + exit 1；非法类名→stderr + exit 1。
    /// </summary>
    private static int SemanticMap(string[] args, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        string layer = RequireOption(args, "--layer");
        string className = RequireOption(args, "--class");
        string drawingPath = RequireOption(args, "--drawing");
        if (!Enum.TryParse<SemanticClass>(className, out var cls))
        {
            stderr.WriteLine($"非法语义类名: {className}（可用: {string.Join("|", Enum.GetNames<SemanticClass>())}）");
            return 1;
        }
        string overlayPath = SemanticOverlayPath(agentDir);
        if (!File.Exists(overlayPath))
        {
            stderr.WriteLine("未找到语义叠加层，请先执行 semantic scan");
            return 1;
        }
        // 审计先行（最终审查裁定）：overlay/drawing Load 失败也必须留痕。
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
        if (overlay is null) return 1;
        var drawing = LoadOrReject(() => DrawingSession.Load(drawingPath), drawingPath, session, audit, op, stderr);
        if (drawing is null) return 1;

        var handlesOnLayer = drawing.Doc.Entities
            .Where(e => string.Equals(e.Layer?.Name ?? "0", layer, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Handle.ToString())
            .ToList();
        overlay.ConfirmMapping(new LayerMapping(layer, cls, op.Account, DateTimeOffset.UtcNow));
        overlay.ApplyMappingToAnnotations(layer, cls, handlesOnLayer);
        overlay.Save(overlayPath);

        // map 只改叠加层不改图纸：前后哈希一致。
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "semantic", "semantic-map", layer,
            JsonSerializer.Serialize(new { layer, @class = cls.ToString(), @operator = op.Account }),
            hash, hash, "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            layer,
            @class = cls.ToString(),
            confirmedBy = op.Account,
            version = overlay.Version
        }, SemanticJsonOpts));
        return 0;
    }

    private static string AdjudicationsPath(string agentDir) =>
        Path.Combine(agentDir, "adjudications.json");

    /// <summary>
    /// 规范引擎装配（T3 评审裁定带入 T7 的装配守卫）：重复 CheckType 注册直接抛异常，
    /// 防止 RuleEngine 字典 last-wins 静默丢失检查器。异常由 Run 兜底→stderr + exit 1。
    /// </summary>
    private static RuleEngine StandardsEngine()
    {
        IRuleChecker[] checkers =
        {
            new TextHeightChecker(), new LineweightChecker(), new AxisLetterChecker(),
            new ElevationChecker(), new LayerNameChecker()
        };
        var duplicate = checkers.GroupBy(c => c.CheckType, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"重复注册检查器 CheckType: {duplicate.Key}");
        return new RuleEngine(checkers);
    }

    /// <summary>
    /// check：LoadOrReject 图纸→overlay 存在则 Load（损坏→audited refusal）→裁定 Load→
    /// RuleEngine.Evaluate(内置规则)→审计 check-run→stdout {issues, summary}（camelCase）。
    /// 退出码：0=无 mandatory 违规；2=有 mandatory 违规（recommended/informational 不影响
    /// 退出码——决策权归人）。check 只读不改图纸：前后哈希一致。
    /// stdout summary 仅含计数（rulesRun/rulesSkipped/issueCount/mandatoryViolations）：
    /// SkippedReasons 含豁免规则 Id，若输出会破坏豁免规则的展示移除契约（T3 遗留的报告层
    /// 结构化留 T8）。
    /// </summary>
    private static int Check(string drawingPath, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var drawing = LoadOrReject(() => DrawingSession.Load(drawingPath), drawingPath,
            session, audit, op, stderr, source: "check");
        if (drawing is null) return 1;
        string overlayPath = SemanticOverlayPath(agentDir);
        var overlay = File.Exists(overlayPath)
            ? LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath,
                session, audit, op, stderr, source: "check")
            : new SemanticOverlay();
        if (overlay is null) return 1;
        var adjudications = AdjudicationSet.Load(AdjudicationsPath(agentDir));
        var summary = StandardsEngine().Evaluate(BuiltinRules.Load(), drawing, overlay, adjudications);
        int mandatoryViolations = summary.Issues.Count(i => i.Force == RuleForce.Mandatory);
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "check", "check-run", drawingPath,
            JsonSerializer.Serialize(new
            {
                rulesRun = summary.RulesRun,
                rulesSkipped = summary.RulesSkipped,
                issueCount = summary.Issues.Count,
                mandatoryViolations
            }),
            hash, hash, "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            issues = summary.Issues,
            summary = new
            {
                rulesRun = summary.RulesRun,
                rulesSkipped = summary.RulesSkipped,
                issueCount = summary.Issues.Count,
                mandatoryViolations
            }
        }, SemanticJsonOpts));
        return mandatoryViolations > 0 ? 2 : 0;
    }

    /// <summary>
    /// rule 命令族分发。子命令校验沿用 semantic 族模式：缺失/未知子命令一律
    /// stderr + exit 1，不落入 Usage 歧义。
    /// </summary>
    private static int Rule(string[] args, string[] positional, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2)
        {
            stderr.WriteLine("rule 需要子命令: adjudicate|list");
            return 1;
        }
        return positional[1] switch
        {
            "adjudicate" => RuleAdjudicate(args, agentDir, session, stdout, stderr),
            "list" => RuleList(agentDir, stdout),
            _ => UnknownRuleSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownRuleSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 rule 子命令: {sub}（可用: adjudicate|list）");
        return 1;
    }

    /// <summary>
    /// rule adjudicate：人工裁定（强制力覆盖／豁免，二选一互斥；reason 必填）。
    /// Upsert（同 RuleId 最新裁定生效）+原子 Save+审计 rule-adjudicated→exit 0。
    /// 裁定只改 adjudications.json 不改图纸：哈希不可得→空串。
    /// </summary>
    private static int RuleAdjudicate(string[] args, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        string ruleId = RequireOption(args, "--rule");
        string? forceText = GetOption(args, "--force");
        bool exempt = args.Contains("--exempt");
        if (forceText is not null && exempt)
        {
            stderr.WriteLine("--force 与 --exempt 互斥，不能同时给定");
            return 1;
        }
        if (forceText is null && !exempt)
        {
            stderr.WriteLine("rule adjudicate 需要 --force <mandatory|recommended|informational> 或 --exempt");
            return 1;
        }
        string? reason = GetOption(args, "--reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            stderr.WriteLine("rule adjudicate 需要 --reason（裁定理由，审计留痕）");
            return 1;
        }
        RuleForce? forceOverride = null;
        if (forceText is not null)
        {
            if (!Enum.TryParse<RuleForce>(forceText, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                stderr.WriteLine($"非法强制力值: {forceText}（可用: mandatory|recommended|informational）");
                return 1;
            }
            forceOverride = parsed;
        }
        string interpretation = GetOption(args, "--interpretation") ?? "";
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        string path = AdjudicationsPath(agentDir);
        var set = AdjudicationSet.Load(path);
        set.Upsert(new Adjudication(ruleId, op.Account, DateTimeOffset.UtcNow, reason,
            forceOverride, exempt, interpretation));
        set.Save(path);
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "rule", AdjudicationSet.AuditKind, ruleId,
            JsonSerializer.Serialize(new { ruleId, forceOverride = forceOverride?.ToString(), exempt, reason }),
            "", "", "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            ruleId,
            forceOverride = forceOverride?.ToString(),
            exempt,
            reason,
            interpretation
        }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>
    /// rule list：内置规则+当前裁定 JSON（camelCase）→exit 0。
    /// 只读命令不入审计（沿用 roles show / semantic show 先例）。
    /// </summary>
    private static int RuleList(string agentDir, TextWriter stdout)
    {
        var adjudications = AdjudicationSet.Load(AdjudicationsPath(agentDir));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            rules = BuiltinRules.Load(),
            adjudications = adjudications.Items
        }, SemanticJsonOpts));
        return 0;
    }

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("用法: cadagent <intake|run|rollback|audit verify|roles show|semantic scan|semantic show|semantic map|check|rule adjudicate|rule list> [参数] [--project <dir>]");
        return 1;
    }
}