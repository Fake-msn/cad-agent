using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Checks;
using CadAgent.Data.Handlers;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
                case "check": return Check(args, RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "rule": return Rule(args, positional, agentDir, session, stdout, stderr);
                case "skill": return Skill(positional, agentDir, stdout, stderr);
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

    /// <summary>可重复选项的全部值（按出现序）——--skill 可重复（2b-ii-a T6）。</summary>
    private static List<string> GetOptionValues(string[] args, string name)
    {
        var values = new List<string>();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name) values.Add(args[i + 1]);
        return values;
    }

    /// <summary>
    /// 规则集装配（2b-ii-a T6）：builtin + --skill 包（可重复，显式目录路径，按出现序）
    /// →SkillSet.Compose。adjudicate 孤儿校验消费 ContainsRule；T7 check/scan --skill
    /// 接线复用同一加载路径（check 消费 AllRules/RulePackVersion，scan 消费 MergedAliases）。
    /// skill 目录非法/规则 Id 冲突→异常由 Run 兜底 stderr + exit 1
    /// （用法错误不审计——与 adjudicate 互斥校验先例一致，校验先于任何副作用）。
    /// </summary>
    private static SkillSet LoadRuleSet(string[] args)
    {
        var packages = GetOptionValues(args, "--skill").Select(SkillLoader.LoadDirectory).ToList();
        return SkillSet.Compose(BuiltinRules.Load(), packages);
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
        SemanticOverlay? overlay = null;
        if (File.Exists(overlayPath))
        {
            overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
            if (overlay is null) return 1;
            semanticGate = new SemanticGate(overlay);
        }
        var result = Engine(agentDir, audit, semanticGate).Run(plan, drawing, session);
        if (!result.Ok) drawing.Reload();
        else RunRegressionCheck(drawing, overlay, agentDir, session, audit, op, stderr);
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            txId = result.TxId, ok = result.Ok, stepsDone = result.StepsDone, error = result.Error
        }, JsonOpts));
        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// run 回归检查（2b-i T8 加性）：tx 成功后对运行后图纸评估内置规则——存在生效
    /// mandatory 违规（含运行未修复的既有违规）→stderr 警告，**不自动回滚，决策权归人**；
    /// 无论有无违规均审计 check-run（Source="run-regression"，只读检查前后哈希一致）。
    /// 观察性检查不得改变 run 结果：裁定文件损坏→LoadOrReject 审计拒绝后跳过回归；
    /// 其余异常→审计留痕（spec §9）+ stderr 提示，退出码仍由事务结果决定。
    /// 跳过原因沿用 check 的结构化 stderr 通道（跳过不静默）。
    /// 无 overlay 观察性：overlay 为 null/空→引擎记 NotEvaluable 结构化跳过（2b-ii-a T5）；
    /// 2b-i interim 的 stderr"未评估"提示与 paramsJson overlayPresent 字段保留（既有测试兼容）。
    /// 复现证据（2b-ii-a T6 加性）：paramsJson 增 adjudicationHash + rulePackVersion
    /// （run 不带 --skill→builtin 常量；与 check 同口径）。
    /// </summary>
    private static void RunRegressionCheck(DrawingSession drawing, SemanticOverlay? overlay,
        string agentDir, string session, SqliteAuditStore audit, OperatorIdentity op, TextWriter stderr)
    {
        try
        {
            var effectiveOverlay = overlay ?? new SemanticOverlay();
            bool overlayPresent = effectiveOverlay.Annotations.Count > 0;
            if (!overlayPresent)
                stderr.WriteLine("提示: 无语义标注，轴号/标高规则未评估（请先 semantic scan/map）");
            string adjPath = AdjudicationsPath(agentDir);
            var adjudications = LoadOrReject(() => AdjudicationSet.Load(adjPath), adjPath,
                session, audit, op, stderr, source: "run-regression");
            if (adjudications is null) return; // 已审计拒绝；已提交事务不受影响
            // 规则包版本（2b-ii-a T6 审计加性）：run 不带 --skill——builtin-only 合成，
            // RulePackVersion 即内置常量；与 check 同一复现证据字段口径。
            var ruleSet = SkillSet.Compose(BuiltinRules.Load(), Array.Empty<SkillPackage>());
            var summary = StandardsEngine().Evaluate(ruleSet.AllRules, drawing, effectiveOverlay, adjudications);
            int mandatoryViolations = summary.Issues.Count(i => i.Force == RuleForce.Mandatory);
            WriteStructuredSkips(summary, stderr);
            string hash = drawing.ComputeHash();
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "run-regression", "check-run", drawing.SourcePath,
                JsonSerializer.Serialize(new
                {
                    rulesRun = summary.RulesRun,
                    rulesSkipped = summary.RulesSkipped,
                    issueCount = summary.Issues.Count,
                    mandatoryViolations,
                    skippedReasons = StructuredSkipAuditArray(summary),
                    overlayPresent,
                    adjudicationHash = adjudications.Fingerprint(),
                    rulePackVersion = ruleSet.RulePackVersion
                }),
                hash, hash, "", ""));
            if (mandatoryViolations > 0)
            {
                string ruleIds = string.Join(", ", summary.Issues
                    .Where(i => i.Force == RuleForce.Mandatory).Select(i => i.RuleId).Distinct());
                stderr.WriteLine($"警告: 运行后图纸存在 {mandatoryViolations} 项强制(mandatory)规范违规（规则: {ruleIds}）。事务已提交，不自动回滚——决策权归人，请执行 check 复核并人工裁定或修正。");
            }
        }
        catch (Exception ex)
        {
            // 回归检查自身故障也入审计（spec §9），但不影响已提交事务。
            // 嵌套守卫（最终审查裁定）：catch 路径的 audit.Append 若再抛异常，不得让异常逃出——
            // 否则 run 将以 exit 1 无 stdout 收场，尽管事务已提交。写入失败降级为仅 stderr 留痕，
            // 绝不重抛。（无注入缝隙可模拟审计写入失败——以代码审查+既有损坏裁定测试覆盖。）
            try
            {
                audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    session, "run-regression", "check-run", drawing.SourcePath,
                    JsonSerializer.Serialize(new { Error = ex.Message, overlayPresent = overlay is { Annotations.Count: > 0 } }),
                    "", "", "", ""));
            }
            catch (Exception auditEx)
            {
                stderr.WriteLine($"回归审计写入失败: {auditEx.Message}");
            }
            stderr.WriteLine($"回归检查未完成: {ex.Message}（事务已提交，不受影响）");
        }
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
            stderr.WriteLine("semantic 需要子命令: scan|show|map|promote");
            return 1;
        }
        return positional[1] switch
        {
            "scan" => SemanticScan(args, RequireOption(args, "--drawing"), agentDir, session, stdout, stderr),
            "show" => SemanticShow(agentDir, stdout),
            "map" => SemanticMap(args, agentDir, session, stdout, stderr),
            "promote" => SemanticPromote(args, agentDir, session, stdout, stderr),
            _ => UnknownSemanticSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownSemanticSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 semantic 子命令: {sub}（可用: scan|show|map|promote）");
        return 1;
    }

    /// <summary>
    /// semantic scan：Load→Scan(既有 overlay, skill 别名)→Save→审计 semantic-scan→stdout
    /// {version, annotations, ambiguous}。Load 失败复用计划 1 intake-rejected
    /// 模式（错误入审计、哈希不可得→空串）+ exit 1。
    /// --skill 接线（2b-ii-a T7）：与 check/adjudicate 同一 LoadRuleSet 加载路径，
    /// MergedAliases→Scan overload（别名命中层不再不明确）；包非法/Id 冲突→用法错误
    /// 先于任何副作用（不审计，Run 兜底 stderr + exit 1）。无 --skill→空别名表，行为不变。
    /// 审计加性（最终审查裁定）：paramsJson 增 rulePackVersion=SkillSet.RulePackVersion
    /// （规则包溯源，与 check 同口径；扫描行为本身不变）。
    /// </summary>
    private static int SemanticScan(string[] args, string drawingPath, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        var ruleSet = LoadRuleSet(args);
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
        var result = SemanticScanner.Scan(drawing, existing, ruleSet.MergedAliases);
        result.Overlay.Save(overlayPath);
        // 扫描不改图纸：前后哈希一致（沿用 intake 的诚实留痕）。
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "semantic", "semantic-scan", drawingPath,
            JsonSerializer.Serialize(new
            {
                annotationCount = result.Overlay.Annotations.Count,
                ambiguousCount = result.Ambiguous.Count,
                version = result.Overlay.Version,
                // 规则包溯源（最终审查裁定 Important #2，加性）：别名命中改变 overlay，
                // 审计员须能分辨哪个 skill 版本产生——与 check/run-regression 同口径
                // （无 --skill→builtin 常量 "gb50001-2017.r1"）。
                rulePackVersion = ruleSet.RulePackVersion
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

    /// <summary>
    /// semantic promote（2b-ii-a T7）：项目人工确认映射提升为 skill 包别名。
    /// 项目 overlay.Mappings 须含该层（OrdinalIgnoreCase，与 ConfirmMapping 同口径），
    /// 无映射→stderr + exit 1（用法错误不审计——map"未找到叠加层"先例）；
    /// overlay 损坏→LoadOrReject 审计拒绝；skill 目录非法→LoadOrReject 审计拒绝。
    /// 校验先于任何写入（validation-before-side-effects）：先读 manifest 文本、匹配
    /// 列 0 version 行并计算 newVersion；无 version 行（如 flow-style manifest，loader
    /// 可加载）→InvalidSkillException→Run 兜底 stderr + exit 1，零变更（aliases/manifest
    /// 不动、不审计）。随后写入顺序（brief）：aliases.yaml upsert（YamlDotNet 序列化，
    /// 保持 {aliases:[{layerName,class}]} 结构；同层名替换 class，否则追加）→
    /// manifest version patch+1（正则仅命中列 0 version 行末位数字组，保留引号）→
    /// 审计 semantic-promoted（Source="semantic"，paramsJson={layer,class,skill,newVersion}；
    /// promote 不改图纸，哈希不可得→空串，adjudicate 先例）→exit 0。
    /// 两阶段提交（最终审查裁定）：先完整暂存 aliases.yaml.tmp + skill.yaml.tmp，两者都
    /// 成功后才顺序 Move——任何 Move 之前的序列化/写入失败→零变更；中途 Move 失败→
    /// semantic-promote-failed 审计（paramsJson={layer,skill,error,movedFiles}，嵌套守卫
    /// 尽力留痕）+ stderr + exit 1。成功路径写入后审计，SemanticMap 先例。
    /// </summary>
    private static int SemanticPromote(string[] args, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        string layer = RequireOption(args, "--layer");
        string skillDir = RequireOption(args, "--skill");
        string overlayPath = SemanticOverlayPath(agentDir);
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
        if (overlay is null) return 1;
        var mapping = overlay.Mappings.FirstOrDefault(m =>
            string.Equals(m.LayerName, layer, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            stderr.WriteLine($"图层 {layer} 无项目人工确认映射（overlay.Mappings），请先 semantic scan/map");
            return 1;
        }
        var package = LoadOrReject(() => SkillLoader.LoadDirectory(skillDir), skillDir, session, audit, op, stderr);
        if (package is null) return 1;

        // 校验先于任何暂存/Move：先读 manifest、匹配列 0 version 行、算出
        // newVersion/bumped 文本。flow-style 等无 version 行的 manifest（loader 可加载）
        // 在此抛 InvalidSkillException→stderr + exit 1，aliases/manifest/审计零变更。
        string manifestPath = Path.Combine(skillDir, "skill.yaml");
        string manifestText = File.ReadAllText(manifestPath);
        var match = ManifestVersionLine.Match(manifestText);
        if (!match.Success)
            throw new InvalidSkillException("skill.yaml: version line not found, cannot patch bump");
        string newVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{int.Parse(match.Groups["patch"].Value, System.Globalization.CultureInfo.InvariantCulture) + 1}");
        // 正则仅替换列 0 version 行末位数字组（MatchEvaluator 避免替换串 $ 歧义），
        // 其余字节原样保留；loader 已校验 digits.digits.digits。
        string bumped = ManifestVersionLine.Replace(manifestText,
            m => $"{m.Groups["prefix"].Value}{newVersion}{m.Groups["suffix"].Value}", 1);

        // aliases upsert：同层名（OrdinalIgnoreCase）替换 class，否则追加（保持原有序）。
        // 注意：upsert 经 YamlDotNet 重新序列化整个文件——原文件的注释与额外未知键会被
        // 丢弃（计划规定的 YamlDotNet 行为，非增量文本编辑）。
        var aliases = package.Aliases.ToList();
        int i = aliases.FindIndex(a => string.Equals(a.LayerName, layer, StringComparison.OrdinalIgnoreCase));
        var upserted = new SkillAlias(layer, mapping.Class);
        if (i >= 0) aliases[i] = upserted; else aliases.Add(upserted);
        string aliasesYaml = YamlSerializer.Serialize(new
        {
            aliases = aliases.Select(a => new { layerName = a.LayerName, @class = a.Class.ToString() }).ToList()
        });

        // 两阶段提交（最终审查裁定 Important #1）：先把两个 .tmp 完整暂存（序列化/写入
        // 错误发生在任何 Move 之前→零变更），全部成功后再顺序 Move。中途 Move 失败
        // （Windows 文件锁/磁盘满等）→尽力追加 semantic-promote-failed 审计
        // （movedFiles 如实记录已 Move 的文件——已变更未升版的状态可追溯）+ stderr + exit 1。
        string aliasesPath = Path.Combine(skillDir, "aliases.yaml");
        var movedFiles = new List<string>();
        try
        {
            // 阶段 1：完整暂存两个 .tmp（保证末尾换行），此阶段失败不得有任何 Move。
            File.WriteAllText(aliasesPath + ".tmp", EnsureTrailingNewline(aliasesYaml));
            File.WriteAllText(manifestPath + ".tmp", EnsureTrailingNewline(bumped));
            // 阶段 2：顺序 Move（tmp+Move 覆盖，SemanticOverlay.Save 先例）。
            File.Move(aliasesPath + ".tmp", aliasesPath, overwrite: true);
            movedFiles.Add("aliases.yaml");
            File.Move(manifestPath + ".tmp", manifestPath, overwrite: true);
            movedFiles.Add("skill.yaml");
        }
        catch (Exception ex)
        {
            // 嵌套守卫（run-regression 先例）：失败审计写入自身再抛异常时降级为仅 stderr
            // 留痕，绝不重抛——promote 失败路径必须以 stderr + exit 1 收场。
            try
            {
                audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    session, "semantic", "semantic-promote-failed", layer,
                    JsonSerializer.Serialize(new { layer, skill = skillDir, error = ex.Message, movedFiles }),
                    "", "", "", ""));
            }
            catch (Exception auditEx)
            {
                stderr.WriteLine($"promote 失败审计写入失败: {auditEx.Message}");
            }
            stderr.WriteLine(ex.Message);
            return 1;
        }

        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "semantic", "semantic-promoted", layer,
            JsonSerializer.Serialize(new { layer, @class = mapping.Class.ToString(), skill = skillDir, newVersion }),
            "", "", "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            layer,
            @class = mapping.Class.ToString(),
            skill = skillDir,
            newVersion
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
    /// 结构化跳过 stderr 通道（2b-ii-a T5，check/run-regression 共用）：
    /// "跳过: [{Category}] {RuleId}: {Message}"——保留旧 "跳过: " 前缀与规则 Id
    /// （既有测试子串兼容）；Message 保留旧 reason 文案子串。
    /// </summary>
    private static void WriteStructuredSkips(CheckSummary summary, TextWriter stderr)
    {
        foreach (var skip in summary.StructuredSkips ?? Array.Empty<SkipReason>())
            stderr.WriteLine($"跳过: [{skip.Category}] {skip.RuleId}: {skip.Message}");
    }

    /// <summary>
    /// 审计 paramsJson skippedReasons 结构化数组（2b-ii-a T5）：[{category, ruleId, message}]
    /// （camelCase 匿名投影，与 paramsJson 其余字段命名一致；stdout 契约不变）。
    /// </summary>
    private static object StructuredSkipAuditArray(CheckSummary summary) =>
        (summary.StructuredSkips ?? Array.Empty<SkipReason>())
            .Select(s => new { category = s.Category, ruleId = s.RuleId, message = s.Message })
            .ToList();

    /// <summary>
    /// check：LoadOrReject 图纸→overlay 存在则 Load（损坏→audited refusal）→裁定
    /// LoadOrReject（损坏→audited refusal，与图纸/overlay 同一守卫）→
    /// RuleEngine.Evaluate(内置规则)→审计 check-run→stdout {issues, summary}（camelCase）。
    /// 退出码：0=无 mandatory 违规；2=有 mandatory 违规（recommended/informational 不影响
    /// 退出码——决策权归人）。check 只读不改图纸：前后哈希一致。
    /// stdout summary 仅含计数（rulesRun/rulesSkipped/issueCount/mandatoryViolations）：
    /// 跳过原因含豁免规则 Id，若输出会破坏豁免规则的展示移除契约。
    /// 跳过不静默（spec §6.2/§9，评审裁定；2b-ii-a T5 结构化）：跳过逐条写 stderr
    /// （"跳过: [{Category}] {RuleId}: {Message}"），并以结构化 skippedReasons 数组
    /// [{category, ruleId, message}] 加性入审计 paramsJson（stdout 契约不变）。
    /// 无 overlay 观察性：无语义标注时 overlay 驱动规则（轴号/标高）由引擎记 NotEvaluable
    /// 结构化跳过（T5）；2b-i interim 的 stderr 提示与 paramsJson overlayPresent 字段保留
    /// （既有测试兼容）。
    /// 复现证据（2b-ii-a T6 加性）：paramsJson 增 adjudicationHash（裁定集 Fingerprint）
    /// + rulePackVersion（SkillSet.RulePackVersion）。
    /// --skill 接线（2b-ii-a T7）：与 adjudicate 同一 LoadRuleSet 加载路径（--skill 可重复），
    /// 规则集=SkillSet.AllRules，rulePackVersion 反映 skill 包；包非法/Id 冲突→用法错误，
    /// 校验先于 OpenAudit/Load（不审计，Run 兜底 stderr + exit 1——adjudicate 互斥校验先例）。
    /// 别名不影响 check（check 不扫描）。无 --skill→builtin-only，行为与 T6 一致。
    /// </summary>
    private static int Check(string[] args, string drawingPath, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        var ruleSet = LoadRuleSet(args);
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
        bool overlayPresent = overlay.Annotations.Count > 0;
        if (!overlayPresent)
            stderr.WriteLine("提示: 无语义标注，轴号/标高规则未评估（请先 semantic scan/map）");
        string adjPath = AdjudicationsPath(agentDir);
        var adjudications = LoadOrReject(() => AdjudicationSet.Load(adjPath), adjPath,
            session, audit, op, stderr, source: "check");
        if (adjudications is null) return 1;
        // 规则集已在入口装配（builtin + --skill 包，T7）：AllRules 序 = builtin 序 + 包序，
        // RulePackVersion = 内置常量 + "|name@version" 按包序拼接（无 --skill→内置常量）。
        var summary = StandardsEngine().Evaluate(ruleSet.AllRules, drawing, overlay, adjudications);
        int mandatoryViolations = summary.Issues.Count(i => i.Force == RuleForce.Mandatory);
        // 跳过不静默：结构化原因走 stderr（含类别+规则 Id），不进 stdout JSON（豁免展示移除契约）。
        WriteStructuredSkips(summary, stderr);
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "check", "check-run", drawingPath,
            JsonSerializer.Serialize(new
            {
                rulesRun = summary.RulesRun,
                rulesSkipped = summary.RulesSkipped,
                issueCount = summary.Issues.Count,
                mandatoryViolations,
                skippedReasons = StructuredSkipAuditArray(summary),
                overlayPresent,
                adjudicationHash = adjudications.Fingerprint(),
                rulePackVersion = ruleSet.RulePackVersion
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
            "list" => RuleList(agentDir, session, stdout, stderr),
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
    /// 孤儿校验（2b-ii-a T6）：ruleId 必须在加载集（builtin + --skill 包，--skill 可重复，
    /// 与 check 同一加载路径）；未知 Id→stderr + exit 1，不留审计、不落盘（校验先于副作用）。
    /// 裁定文件 LoadOrReject（损坏→audited refusal exit 1）→Upsert（同 RuleId 最新裁定
    /// 生效）→审计 rule-adjudicated→原子 Save→exit 0。
    /// 审计先行（评审裁定）：Append 先于 Save——若 Save 在审计后抛异常，记录仍保留裁定
    /// 意图（可接受，不回滚审计）；异常由 Run 兜底→stderr + exit 1。
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
        // 孤儿裁定校验（2b-ii-a T6）：ruleId 必须在加载集（builtin + --skill 包）。
        // 校验顺序契约：互斥→force/exempt 必填→reason 必填→force 合法性→孤儿——
        // 先于 OpenAudit/Load/Upsert/Append/Save，未知 Id 不留审计、不落盘。
        var ruleSet = LoadRuleSet(args);
        if (!ruleSet.ContainsRule(ruleId))
        {
            stderr.WriteLine($"未知规则 Id: {ruleId}（不在 builtin+skill 规则集中，拒绝孤儿裁定）");
            return 1;
        }
        string interpretation = GetOption(args, "--interpretation") ?? "";
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        string path = AdjudicationsPath(agentDir);
        var set = LoadOrReject(() => AdjudicationSet.Load(path), path,
            session, audit, op, stderr, source: "rule");
        if (set is null) return 1;
        set.Upsert(new Adjudication(ruleId, op.Account, DateTimeOffset.UtcNow, reason,
            forceOverride, exempt, interpretation));
        // 审计先行：Append 先于 Save（见方法注释——Save 失败不回滚审计，记录保留意图）。
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "rule", AdjudicationSet.AuditKind, ruleId,
            JsonSerializer.Serialize(new { ruleId, forceOverride = forceOverride?.ToString(), exempt, reason }),
            "", "", "", ""));
        set.Save(path);
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
    /// 只读命令成功时不入审计（沿用 roles show / semantic show 先例）；裁定文件损坏
    /// 则 LoadOrReject 审计拒绝（spec §9 错误处理全部入审计）+ stderr + exit 1。
    /// </summary>
    private static int RuleList(string agentDir, string session, TextWriter stdout, TextWriter stderr)
    {
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        string path = AdjudicationsPath(agentDir);
        var adjudications = LoadOrReject(() => AdjudicationSet.Load(path), path,
            session, audit, op, stderr, source: "rule");
        if (adjudications is null) return 1;
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            rules = BuiltinRules.Load(),
            adjudications = adjudications.Items
        }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>
    /// skill 命令族分发（2b-ii-a T7）。子命令校验沿用 semantic/rule 族模式：
    /// 缺失/未知子命令一律 stderr + exit 1，不落入 Usage 歧义。
    /// </summary>
    private static int Skill(string[] positional, string agentDir, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2)
        {
            stderr.WriteLine("skill 需要子命令: list|validate");
            return 1;
        }
        return positional[1] switch
        {
            "list" => SkillList(agentDir, stdout, stderr),
            "validate" => SkillValidate(positional, stdout),
            _ => UnknownSkillSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownSkillSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 skill 子命令: {sub}（可用: list|validate）");
        return 1;
    }

    /// <summary>
    /// skill list（2b-ii-a T7）：扫描 project/.cadagent/skills/*/（每子目录一个包，
    /// Ordinal 序）→JSON [{name, version, ruleCount, aliasCount}]→exit 0。
    /// 目录缺失→空列表 exit 0；非法包→跳过 + stderr 警告（裁定：list 属信息性命令，
    /// 不因个别坏包崩溃整体列表）。只读不入审计（roles show / semantic show 先例）。
    /// </summary>
    private static int SkillList(string agentDir, TextWriter stdout, TextWriter stderr)
    {
        string skillsRoot = Path.Combine(agentDir, "skills");
        var entries = new List<object>();
        if (Directory.Exists(skillsRoot))
        {
            foreach (string dir in Directory.EnumerateDirectories(skillsRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                try
                {
                    var pkg = SkillLoader.LoadDirectory(dir);
                    entries.Add(new
                    {
                        name = pkg.Manifest.Name,
                        version = pkg.Manifest.Version,
                        ruleCount = pkg.Rules.Count,
                        aliasCount = pkg.Aliases.Count
                    });
                }
                catch (InvalidSkillException ex)
                {
                    stderr.WriteLine($"警告: 跳过非法 skill 包 '{Path.GetFileName(dir)}': {ex.Message}");
                }
            }
        }
        stdout.WriteLine(JsonSerializer.Serialize(entries, SemanticJsonOpts));
        return 0;
    }

    /// <summary>
    /// skill validate（2b-ii-a T7）：SkillLoader.LoadDirectory→stdout 摘要
    /// {name, version, ruleCount, aliasCount}→exit 0；InvalidSkillException/缺目录参数
    /// →异常由 Run 兜底 stderr + exit 1。只读不入审计。
    /// </summary>
    private static int SkillValidate(string[] positional, TextWriter stdout)
    {
        if (positional.Length < 3)
            throw new ArgumentException("skill validate 需要目录参数: skill validate <dir>");
        var pkg = SkillLoader.LoadDirectory(positional[2]);
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            name = pkg.Manifest.Name,
            version = pkg.Manifest.Version,
            ruleCount = pkg.Rules.Count,
            aliasCount = pkg.Aliases.Count
        }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>promote aliases.yaml 序列化（camelCase 属性名，保持 {aliases:[{layerName,class}]} 结构）。</summary>
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>promote manifest patch bump：仅命中列 0 顶层 version 行（块式 YAML，引号可选；
    /// 锚定列 0 使缩进的嵌套 version 文本——如块标量内容——不被误命中），末位数字组即 patch。</summary>
    private static readonly Regex ManifestVersionLine = new(
        @"(?m)^(?<prefix>version:[ \t]*[""']?)(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<suffix>[""']?)",
        RegexOptions.Compiled);

    /// <summary>保证末尾换行（promote 暂存写入约定，原 AtomicWriteAllText 的换行语义）。</summary>
    private static string EnsureTrailingNewline(string content) =>
        content.EndsWith('\n') ? content : content + "\n";

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("用法: cadagent <intake|run|rollback|audit verify|roles show|semantic scan|semantic show|semantic map|semantic promote|check|rule adjudicate|rule list|skill list|skill validate> [参数] [--project <dir>]");
        return 1;
    }
}
