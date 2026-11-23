using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Checks;
using CadAgent.Data.Handlers;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;

namespace CadAgent.Cli;

public static partial class CommandRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // intake stdout census 加性（T3）：unparsed 字段仅非空 census 输出——WhenWritingNull
    // 省略 null（无 census 图纸 stdout 逐字不变，T1 Fix Round 1 契约保持；其余字段恒非 null）。
    private static readonly JsonSerializerOptions IntakeJsonOpts = new(JsonOpts)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
                case "intake":
                    // fix wave（intake 用法守卫）：图纸路径是 positional（intake <path>），
                    // 误用选项形态（intake --drawing x）经 StripOptionValues 剥选项后
                    // positional=["intake"]——此前 positional[1] 裸抛 IndexOutOfRange
                    // （Run 兜底打印原生消息，无用法指引）。守卫先于 Intake/OpenAudit：
                    // 友好用法提示 + exit 1，不建 audit.db（用法错误不留审计——
                    // RequireOption/semantic 子命令守卫先例）。仅修 intake 分支，
                    // 其他命令同型问题留后续切片（不扩 scope）。
                    if (positional.Length < 2)
                    {
                        stderr.WriteLine("用法: intake <图纸路径> [--project <目录>]");
                        return 1;
                    }
                    return Intake(positional[1], agentDir, session, stdout, stderr);
                case "run": return RunPlan(args, positional[1], RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "rollback": return Rollback(positional[1], RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "audit": return positional.Length > 1 && positional[1] == "package"
                    ? AuditPackage(args, agentDir, stdout, stderr)
                    : AuditVerify(args, agentDir, stdout, stderr);
                case "roles": return RolesShow(agentDir, stdout);
                case "semantic": return Semantic(args, positional, agentDir, session, stdout, stderr);
                case "check": return Check(args, RequireOption(args, "--drawing"), agentDir, session, stdout, stderr);
                case "rule": return Rule(args, positional, agentDir, session, stdout, stderr);
                case "skill": return Skill(args, positional, agentDir, session, stdout, stderr);
                case "view": return ViewLaunch(args, project, stderr);
                case "llm": return Llm(args, positional, agentDir, session, stdout, stderr);
                case "agent": return Agent(args, agentDir, session, stdout, stderr);
                case "review": return Review(args, positional, agentDir, session, stdout, stderr);
                case "repl": return ReplLoop(args, Console.In, stdout, stderr);
                case "export": return Export(args, agentDir, session, stdout, stderr);
                case "vendor": return Vendor(positional, stdout, stderr);
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
    /// Gate-at-run policy (spec hard-req 6; 防假 Green T3 收紧): Yellow/Red 一律拒绝修改——
    /// 未解析对象无句柄/几何，禁触区无法定位，全图级拦截（只读审查）。仅 Green 放行。
    /// </summary>
    internal static bool GateRefusesRun(GateReport report) => report.Level != GateLevel.Green;

    /// <summary>
    /// 统一读图 + census sidecar 接线（防假 Green T3）：intake/run/skill run/agent/repl/export
    /// 修改消费点 + rollback/check/review 只读/撤回消费点共用（T5 顺手修②等价一致性）。
    /// persisted=Store.Load → DrawingSession.Load(path, persisted) → census 非空回写（原子写）。
    /// R9：persisted Empty/缺 + 源 .dwg 在 → Load 从源通知重建 census（此处回写 sidecar）；
    /// 源缺失 → Load 抛 InvalidOperationException（各消费点既有 catch/LoadOrReject 留痕拒绝）。
    /// agentDir 约定 = project/.cadagent（Run 唯一装配点），sidecar 以 projectDir 键控。
    /// 已知限制（T1 单文件多源互斥）：第二源 Save 覆盖第一源，Load 靠 SourcePath 匹配诚实
    /// Empty——多源场景 census 仅最近源有效（见 UnparsedCensusStore 注释）。
    /// </summary>
    private static DrawingSession LoadWithCensus(string drawingPath, string agentDir)
    {
        string projectDir = Path.GetDirectoryName(Path.GetFullPath(agentDir))!;
        var persisted = UnparsedCensusStore.Load(projectDir, drawingPath);
        var session = DrawingSession.Load(drawingPath, persisted);
        if (session.Unparsed.TotalCount > 0)
            UnparsedCensusStore.Save(projectDir, drawingPath, session.Unparsed);
        return session;
    }

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
        new(Path.Combine(agentDir, "audit.db"), new CadAgent.Core.Audit.AnchorService(agentDir));

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
            drawing = LoadWithCensus(file, agentDir);
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
            summary,
            // census 数组（类×计数，T3 加性）：通知侧未解析对象普查—— Yellow/Red 判定的证据面；
            // 空 census → null → WhenWritingNull 省略（legacy stdout 逐字不变）。
            unparsed = drawing.Unparsed.TotalCount > 0 ? drawing.Unparsed.Classes : null
        }, IntakeJsonOpts));
        return report.Level switch { GateLevel.Green => 0, GateLevel.Yellow => 2, _ => 3 };
    }

    private static StepEngine Engine(string agentDir, IAuditStore audit, ISemanticGate? semanticGate = null,
        ModelUndoStack? undoStack = null, IStepHandler? extraHandler = null) =>
        new(audit, new SnapshotStore(Path.Combine(agentDir, "snapshots")),
            IdentityOverride ?? Identity(agentDir), new RoleChecker(),
            extraHandler == null ? HandlerRegistry.All() : HandlerRegistry.All().Concat(new[] { extraHandler }).ToList(),
            semanticGate, undoStack);

    /// <summary>
    /// 3h：--skill 包符号导入处理器（insert-symbol）。无 --skill→null（kind 不注册，
    /// 计划含 insert-symbol 时响亮失败"未注册的步骤类型"）；包非法→加载异常经 Run 兜底。
    /// </summary>
    private static IStepHandler? SymbolImportHandler(string[] args)
    {
        var dirs = GetOptionValues(args, "--skill");
        if (dirs.Count == 0) return null;
        var packages = dirs.Select(SkillLoader.LoadDirectory).ToList();
        return new ImportSymbolHandler(packages);
    }

    /// <summary>
    /// run --skill 接线（2b-ii-b T8，加性）：入口先经 LoadRuleSet 校验包加载
    /// （builtin + --skill 包，Id 冲突/包非法→InvalidSkillException→Run 兜底
    /// stderr + exit 1，用法错误先于 OpenAudit/Load——check --skill 冲突先例）。
    /// 第一 slice 仅校验包加载 + tx 审计留痕：闸口 overlay 逻辑不变（别名影响 scan
    /// 不影响 run 闸口存在性检查）；步骤模板执行走 skill run，run 不做模板实例化。
    /// 带 --skill 时 tx-start/tx-commit 审计 paramsJson 由裸 planId 升级为
    /// {planId, rulePackVersion}（RulePackAuditDecorator，SkillSet.RulePackVersion
    /// 复现证据，与 check/scan 同口径）；不带 --skill 行为逐字不变。
    /// 回归检查仍 builtin-only（第一 slice 裁定：run --skill 不评估包规则）。
    /// </summary>
    private static int RunPlan(string[] args, string planPath, string drawingPath, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        var ruleSet = LoadRuleSet(args, stderr);
        bool skillLoaded = GetOptionValues(args, "--skill").Count > 0;
        var plan = StepPlanParser.Parse(File.ReadAllText(planPath));
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        DrawingSession drawing;
        GateReport report;
        try
        {
            drawing = LoadWithCensus(drawingPath, agentDir);
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
            // spec 硬要求6（T3 收紧）：信息不完整（Red）或部分未解析（Yellow）图纸禁止修改——
            // 不执行任何步骤。Yellow=只读审查（未解析对象无句柄/几何，禁触区无法定位，全图级拦截）。
            string hash = drawing.ComputeHash();
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "run", "run-rejected", drawingPath,
                JsonSerializer.Serialize(new
                {
                    level = report.Level.ToString(),
                    unsupportedCount = report.Unsupported.Count,
                    unparsedCount = drawing.Unparsed.TotalCount,
                    guidance = report.Guidance
                }),
                hash, hash, "", ""));
            stderr.WriteLine(report.Guidance);
            return 1;
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
        // 带 --skill：tx 审计 paramsJson 加性携带 rulePackVersion（装饰器只改写
        // tx-start/tx-commit，其余记录原样透传）；不带 --skill：直传，行为不变。
        IAuditStore engineAudit = skillLoaded
            ? new RulePackAuditDecorator(audit, ruleSet.RulePackVersion)
            : audit;
        var result = Engine(agentDir, engineAudit, semanticGate, null, SymbolImportHandler(args)).Run(plan, drawing, session);
        string? liveOut = null;
        if (!result.Ok) drawing.Reload();
        else
        {
            RunRegressionCheck(drawing, overlay, agentDir, session, audit, op, stderr);
            var live = LiveReplay(args, agentDir, session, audit, op, drawing, plan, LivePackages(args), stderr);
            if (!live.Ok)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    txId = result.TxId, ok = false, stepsDone = result.StepsDone, error = live.Error
                }, JsonOpts));
                return 1;
            }
            liveOut = live.OutPath;
        }
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            txId = result.TxId, ok = result.Ok, stepsDone = result.StepsDone, error = result.Error, liveOut
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
        // R9 补留痕：此前 Load 在 try/catch 外——异常经 Run 兜底 exit 1 但无审计（spec §9 缺口）。
        // LoadOrReject 复用 intake-rejected 模式（Source="rollback"，与 semantic/check 同一守卫）。
        // R10 Minor#2：裸 Load → LoadWithCensus——sidecar 有效（路径+源哈希匹配）时用
        // persisted census，避免全量重读源；Yellow 上 rollback 保持 R7 撤回豁免（不加闸）。
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var drawing = LoadOrReject(() => LoadWithCensus(drawingPath, agentDir), drawingPath,
            session, audit, op, stderr, source: "rollback");
        if (drawing == null) return 1;
        // 快照必须属于当前图纸：防止把别的图纸的快照覆盖到本图纸上
        string snapshotFile = new SnapshotStore(Path.Combine(agentDir, "snapshots")).Find(txId);
        if (!string.Equals(Path.GetFileName(snapshotFile), Path.GetFileName(drawing.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            // 拒绝也必须入审计（spec §9）：谁、撤哪个事务、期望/实际图纸各是什么。
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
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
        Engine(agentDir, audit).Rollback(txId, drawing, session);
        stdout.WriteLine($"已撤回事务 {txId}");
        return 0;
    }

    private static int AuditVerify(string[] args, string agentDir, TextWriter stdout, TextWriter stderr)
    {
        using var audit = OpenAudit(agentDir);
        if (!audit.VerifyChain(out string error))
        {
            stderr.WriteLine(error);
            return 1;
        }
        var all = audit.ReadAll();
        stdout.WriteLine($"审计链完整，共 {all.Count} 条记录");
        if (args.Contains("--anchor"))
        {
            var anchor = new CadAgent.Core.Audit.AnchorService(agentDir);
            long headSeq = all.Count > 0 ? all[^1].Seq : 0;
            string headHash = all.Count > 0 ? all[^1].Hash : "";
            var status = anchor.Verify(headSeq, headHash);
            switch (status)
            {
                case CadAgent.Core.Audit.AnchorStatus.Ok:
                    stdout.WriteLine("外部锚校验通过（无截断/伪造）");
                    return 0;
                case CadAgent.Core.Audit.AnchorStatus.Missing:
                    stderr.WriteLine("警示: 外部锚未启用（anchor.json 缺失）");
                    return 0;
                case CadAgent.Core.Audit.AnchorStatus.Stale:
                    stderr.WriteLine("警示: 外部锚滞后（db 超前锚，崩溃窗口尾记录无锚保护）；审计包 manifest 如实记 Stale");
                    return 0;
                default:
                    stderr.WriteLine($"外部锚校验失败: {status}（尾截断或伪造）");
                    return 1;
            }
        }
        return 0;
    }

    private static int RolesShow(string agentDir, TextWriter stdout)
    {
        var op = Identity(agentDir).Current;
        stdout.WriteLine($"{op.Account}\t{op.Role}");
        return 0;
    }

    /// <summary>
    /// view（计划4 T1）：进程拉起 Avalonia 视图——dotnet CadAgent.View.dll（同目录，
    /// Cli 对 View 的项目引用仅复制输出；Cli 代码不 using Avalonia）。
    /// 图纸缺失/View 程序集缺失→stderr + exit 1；启动成功即 exit 0（不等待 UI 进程）。
    /// T10：--skill 可重复直通 View 进程（BuildViewArgs 装配）；包校验先于进程拉起
    /// （check --skill 先例：用法错误先于任何副作用，LoadRuleSet 异常由 Run 兜底）。
    /// </summary>
    private static int ViewLaunch(string[] args, string project, TextWriter stderr)
    {
        string drawing = RequireOption(args, "--drawing");
        if (!File.Exists(drawing))
        {
            stderr.WriteLine($"图纸文件不存在: {drawing}");
            return 1;
        }
        var skillDirs = GetOptionValues(args, "--skill");
        LoadRuleSet(args, stderr); // 包非法/Id 冲突→抛异常（Run 兜底 stderr + exit 1，不拉起进程）
        string viewDll = Path.Combine(AppContext.BaseDirectory, "CadAgent.View.dll");
        if (!File.Exists(viewDll))
        {
            stderr.WriteLine($"未找到视图程序集: {viewDll}");
            return 1;
        }
        Process.Start("dotnet", BuildViewArgs(viewDll, drawing, project, skillDirs));
        return 0;
    }

    /// <summary>
    /// T10：View 进程参数装配（arg-builder 单测覆盖）——--skill 可重复、按出现序
    /// 追加；不带 --skill 时与 T1 字符串逐字一致。
    /// </summary>
    internal static string BuildViewArgs(string viewDll, string drawing, string project,
        IReadOnlyList<string> skillDirs)
    {
        var sb = new System.Text.StringBuilder($"\"{viewDll}\" --drawing \"{drawing}\" --project \"{project}\"");
        foreach (string dir in skillDirs) sb.Append($" --skill \"{dir}\"");
        return sb.ToString();
    }

    /// <summary>
    /// 视觉接线 T3：semantic scan --vision relay 进程拉起 seam（仅测试注入：捕获 args/返回码；
    /// 生产 null → Process dotnet ViewDll 真启动）。与 LlmClientFactory 同口径。
    /// </summary>
    internal static Func<string[], int>? VisionScanSpawn;

    /// <summary>
    /// 视觉接线 T3：semantic scan --vision → 独立 View 进程 --vision-scan headless 入口
    /// （R4 parity：ViewLaunch 进程模式镜像——Cli 不 using Avalonia；区别：relay 须等进程
    /// 结束并转发 stdout/stderr/退出码，故 RedirectStandardOutput/Error + WaitForExit）。
    /// 转发 --drawing/--project/--confirm-egress/--name/--max-tiles/--session；闸口/出境/审计全在 View 进程内。
    /// View 程序集缺失→stderr + exit 1（ViewLaunch 同口径）。
    /// </summary>
    private static int VisionScanRelay(string[] args, string drawingPath, string agentDir,
        TextWriter stdout, TextWriter stderr)
    {
        string project = Path.GetDirectoryName(Path.GetFullPath(agentDir))!;
        var relayArgs = new List<string> { "--vision-scan", "--drawing", drawingPath, "--project", project };
        if (args.Contains("--confirm-egress")) relayArgs.Add("--confirm-egress");
        string? name = GetOption(args, "--name");
        if (name is not null) { relayArgs.Add("--name"); relayArgs.Add(name); }
        // T4 成本闸：--max-tiles 瓦片预算直通 View 进程（值非法由 View 用法拒绝，先于其副作用）。
        string? maxTiles = GetOption(args, "--max-tiles");
        if (maxTiles is not null) { relayArgs.Add("--max-tiles"); relayArgs.Add(maxTiles); }
        // R11⑤：--session 直通 View 进程（View 入口读 --session；不转发则 relay 链审计会话断档）。
        // 未给定不转发（View 侧自生成 GUID，既有契约逐字保持）。
        string? sessionOpt = GetOption(args, "--session");
        if (sessionOpt is not null) { relayArgs.Add("--session"); relayArgs.Add(sessionOpt); }
        if (VisionScanSpawn is { } spawn)
            return spawn(relayArgs.ToArray());
        string viewDll = Path.Combine(AppContext.BaseDirectory, "CadAgent.View.dll");
        if (!File.Exists(viewDll))
        {
            stderr.WriteLine($"未找到视图程序集: {viewDll}");
            return 1;
        }
        var psi = new ProcessStartInfo("dotnet", BuildVisionScanArgs(viewDll, relayArgs))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // View 进程 --vision-scan 分支置 Console.OutputEncoding=UTF8；relay 同口径读取
            // （Windows GBK 默认码页会损坏中文 JSON/指引）。
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("View 进程启动失败");
        // 双异步读避免经典死锁（子进程 stderr 缓冲先满时同步 ReadToEnd 互等）；输出为小 JSON/指引。
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        stdout.Write(outTask.GetAwaiter().GetResult());
        stderr.Write(errTask.GetAwaiter().GetResult());
        return proc.ExitCode;
    }

    /// <summary>
    /// 视觉接线 T3：relay 进程参数装配（arg-builder 单测覆盖，BuildViewArgs 同口径）——
    /// dll/选项值加引号，flag（-- 前缀）裸追加。
    /// </summary>
    internal static string BuildVisionScanArgs(string viewDll, IReadOnlyList<string> relayArgs)
    {
        var sb = new System.Text.StringBuilder($"\"{viewDll}\"");
        foreach (string a in relayArgs)
            sb.Append(a.StartsWith("--") ? $" {a}" : $" \"{a}\"");
        return sb.ToString();
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
            stderr.WriteLine("semantic 需要子命令: scan|show|map|map-block|promote");
            return 1;
        }
        return positional[1] switch
        {
            "scan" => SemanticScan(args, RequireOption(args, "--drawing"), agentDir, session, stdout, stderr),
            "show" => SemanticShow(agentDir, stdout),
            "map" => SemanticMap(args, agentDir, session, stdout, stderr),
            // BF-T4（块级纠偏切片，spec §13.3）：块→符号人工确认（复核制，overlay-only）。
            "map-block" => SemanticMapBlock(args, agentDir, session, stdout, stderr),
            "promote" => SemanticPromote(args, agentDir, session, stdout, stderr),
            _ => UnknownSemanticSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownSemanticSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 semantic 子命令: {sub}（可用: scan|show|map|map-block|promote）");
        return 1;
    }

    /// <summary>
    /// semantic scan：Load→Scan(既有 overlay, skill 别名)→Save→审计 semantic-scan→stdout
    /// {version, annotations, ambiguous, sheets}（sheets=T7 加性幅面摘要 {sheetId,name,number}）。
    /// Load 失败复用计划 1 intake-rejected
    /// 模式（错误入审计、哈希不可得→空串）+ exit 1。
    /// --skill 接线（2b-ii-a T7）：与 check/adjudicate 同一 LoadRuleSet 加载路径，
    /// MergedAliases→Scan overload（别名命中层不再不明确）；包非法/Id 冲突→用法错误
    /// 先于任何副作用（不审计，Run 兜底 stderr + exit 1）。无 --skill→空别名表，行为不变。
    /// 审计加性（最终审查裁定）：paramsJson 增 rulePackVersion=SkillSet.RulePackVersion
    /// （规则包溯源，与 check 同口径；扫描行为本身不变）。
    /// 视觉接线 T3：--vision → relay 独立 View 进程 headless 入口（Cli 不 using Avalonia，
    /// ViewLaunch 先例）；无 --vision 的 legacy 在进程扫描逐字不变。
    /// R9（T4 顺带修）：--vision 与 --skill 并存 → 用法拒绝 exit 1——relay 不转发 --skill
    /// （View headless 入口无别名通道），静默丢弃=诚实缺口；拒绝先于一切副作用（check --skill 先例）。
    /// BF-T3（块级纠偏切片，spec §13.3）：--skill 包符号库→Scan symbolPackages 通道；
    /// stdout 加性四键恒在 blockAutoSeeded/blockSuggestions/blockAmbiguous(+guidance)/symbolsSkipped
    /// （无包→零/空；旧键 version/annotations/ambiguous/sheets 逐字不变）；审计 params 加性
    /// blockAutoSeeded/blockSuggestionCount/blockAmbiguousCount/symbolsSkipped。
    /// --min-block-instances 正整数旗标（镜像 --max-tiles 先例）：非法值→stderr + exit 1，
    /// 先于一切副作用（含 --vision relay——新旗标无 legacy 行为可破）。
    /// </summary>
    private static int SemanticScan(string[] args, string drawingPath, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        // BF-T3：--min-block-instances 用法解析先于一切副作用（不建 .cadagent/审计——
        // --max-tiles 正整数先例；置于 --vision relay 之前，非法值任何路径都拒绝）。
        int minBlockInstances = 3;
        string? minRaw = GetOption(args, "--min-block-instances");
        if (minRaw is not null)
        {
            if (!int.TryParse(minRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                stderr.WriteLine($"用法: --min-block-instances 须为正整数（收到: {minRaw}）");
                return 1;
            }
            minBlockInstances = n;
        }
        // --vision relay 先于一切在进程副作用（闸口/出境/审计全在 View 进程内编排）。
        if (args.Contains("--vision"))
        {
            // R11④：args.Contains 判定替代 GetOptionValues 计数——裸 --skill（末位无值）
            // 取不到值但 flag 存在本身即冲突，同样拒绝（静默丢弃=诚实缺口，R9 同口径）。
            if (args.Contains("--skill"))
            {
                stderr.WriteLine("用法: semantic scan --vision 与 --skill 不能并存（视觉读图不支持 skill 别名；请去掉 --skill 或改用无 --vision 的语义扫描）");
                return 1;
            }
            return VisionScanRelay(args, drawingPath, agentDir, stdout, stderr);
        }
        var ruleSet = LoadRuleSet(args, stderr);
        // BF-T3：符号库包列表（与 LoadRuleSet 同一 --skill 加载路径；包非法先于副作用拒绝）。
        var packages = LoadSkillPackages(args);
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
        var result = SemanticScanner.Scan(drawing, existing, ruleSet.MergedAliases,
            symbolPackages: packages, minBlockInstances: minBlockInstances);
        result.Overlay.Save(overlayPath);
        // 扫描不改图纸：前后哈希一致（沿用 intake 的诚实留痕）。
        string hash = drawing.ComputeHash();
        // BF-T3 加性披露口径（审计与 stdout 共用）：无包→BlockMatch null→零/空恒在。
        int blockAutoSeeded = result.BlockMatch?.AutoSeeds.Sum(s => s.Handles.Count) ?? 0;
        int symbolsSkipped = result.BlockMatch?.SymbolsSkipped ?? 0;
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
                rulePackVersion = ruleSet.RulePackVersion,
                // BF-T3 审计加性（spec §13.3）：块级信号计数——自动种子标注数/建议数/歧义数/跳过符号数。
                blockAutoSeeded,
                blockSuggestionCount = result.BlockMatch?.Suggestions.Count ?? 0,
                blockAmbiguousCount = result.BlockMatch?.Ambiguous.Count ?? 0,
                symbolsSkipped
            }),
            hash, hash, "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            version = result.Overlay.Version,
            annotations = result.Overlay.Annotations,
            ambiguous = result.Ambiguous,
            // T7 加性：幅面摘要数组（sheetId=图框 Insert 句柄/布局 "L"+视口句柄；
            // 无图框 legacy 图纸=单默认幅面 S0）。agent 计划 params.sheetId 的取值依据；
            // 既有 version/annotations/ambiguous 契约不变。
            sheets = drawing.Sheets.Select(s => new { sheetId = s.SheetId, name = s.Name, number = s.Number }),
            // BF-T3 stdout 加性四键恒在（spec §13.3）：块级自动种子标注数/近似建议/高频歧义/跳过符号数。
            // guidance 仅"代理图形不可读"项给 T3 转换指引（披露侧拼接，不入 record）；其余 null。
            blockAutoSeeded,
            blockSuggestions = (result.BlockMatch?.Suggestions ?? Array.Empty<BlockSuggestion>())
                .Select(s => new { block = s.BlockName, instances = s.Instances,
                    candidates = s.Candidates.Select(c => new { symbol = c.SymbolName, distance = c.Distance, confidence = c.Confidence }) }),
            blockAmbiguous = (result.BlockMatch?.Ambiguous ?? Array.Empty<BlockAmbiguous>())
                .Select(a => new { block = a.BlockName, instances = a.Instances, reason = a.Reason,
                    sampleHandles = a.SampleHandles,
                    guidance = a.Reason == "代理图形不可读" ? "建议天正 T3 转换后重扫" : null }),
            symbolsSkipped
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
    /// semantic map-block（BF-T4 块级纠偏切片，spec §13.3）：人工确认块→符号映射。
    /// 复核制：确认时重提取块/符号指纹——哈希精确（exact）或 Distance(块fp,符号fp)≤
    /// NearThreshold（固定参数序，T1 非对称口径），否则拒绝留痕 reason=指纹不符。
    /// 成功落该块全部 walked Insert 实例（含嵌套后缀句柄）HumanConfirmed 1.0——
    /// 每句柄先 RemoveAll 全 provenance 再 Upsert（镜像 ApplyMappingToAnnotations）。
    /// overlay-only 不改图纸（before==after 哈希）；**不调 GateDetector**——
    /// Yellow 豁免沿用 SemanticMap 先例（只改 overlay 不改图纸）。
    /// 拒绝面（OpenAudit 后任何拒绝都留痕 semantic-map-block-rejected）：
    /// 块不存在/符号不存在/块无几何/符号无几何/指纹不符/class 非法。
    /// class 默认符号 meta.Class；--class 覆盖须 Enum.TryParse 成功（SemanticMap 同口径）。
    /// 符号首包命中 OrdinalIgnoreCase（镜像 ImportSymbolHandler）；drawing Load 失败
    /// 镜像 scan intake-rejected 留痕。--skill 用法校验先于 OpenAudit（最终审查
    /// Important#1：非法包→exit 1 零副作用，audit.db 不创建）。
    /// </summary>
    private static int SemanticMapBlock(string[] args, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        string blockName = RequireOption(args, "--block");
        string symbolName = RequireOption(args, "--symbol");
        string drawingPath = RequireOption(args, "--drawing");
        // 用法校验先于任何副作用（LoadRuleSet/scan 先例，最终审查 Important#1）：
        // 非法 skill 包→异常经 Run 兜底 exit 1，此时 audit.db 未建、working 未物化。
        var packages = LoadSkillPackages(args);
        // 审计先行（最终审查裁定，仅覆盖 drawing Load 失败留痕，不要求用法校验在审计后）：
        // Load 失败也必须留痕（scan intake-rejected 镜像）。
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
        // 符号首包命中（OrdinalIgnoreCase，镜像 ImportSymbolHandler）
        SymbolMeta? meta = null;
        SkillPackage? owner = null;
        foreach (var p in packages)
        {
            meta = p.Symbols.FirstOrDefault(s => s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
            if (meta != null) { owner = p; break; }
        }
        if (meta is null)
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "符号不存在", stderr);
        // 块名查找 OrdinalIgnoreCase（控制器裁定）
        var block = drawing.Doc.BlockRecords.FirstOrDefault(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
        if (block is null)
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "块不存在", stderr);
        var fpBlock = BlockFingerprint.Extract(block.Entities);
        if (fpBlock is null)
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "块无几何", stderr);
        // 符号 DXF 路径=包内 symbols/<file>（ImportSymbolHandler 同口径）；读失败=符号无几何
        ACadSharp.CadDocument symDoc;
        try
        {
            using var fs = File.OpenRead(Path.Combine(owner!.SourceDir, "symbols", meta.File));
            symDoc = new ACadSharp.IO.DxfReader(fs).Read();
        }
        catch
        {
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "符号无几何", stderr);
        }
        var fpSym = BlockFingerprint.Extract(symDoc.Entities);
        if (fpSym is null)
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "符号无几何", stderr);
        // 复核：exact=哈希相等；否则固定参数序 Distance(块fp,符号fp)，超阈拒绝
        bool exact = fpBlock.Hash == fpSym.Hash;
        double d = exact ? 0 : BlockFingerprint.Distance(fpBlock, fpSym);
        if (!exact && d > BlockFingerprint.NearThreshold)
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "指纹不符", stderr);
        // class 默认符号 meta.Class；--class 覆盖须 Enum.TryParse 成功（SemanticMap 同口径，
        // 大小写敏感按现状）；非法→审计拒绝（OpenAudit 后任何拒绝都留痕）。
        SemanticClass cls = meta.Class;
        string? classOpt = GetOption(args, "--class");
        if (classOpt is not null && !Enum.TryParse(classOpt, out cls))
            return RejectMapBlock(audit, op, session, drawing, blockName, symbolName, "class 非法", stderr);
        // 实例=walked Insert 且 Block.Name OrdinalIgnoreCase 匹配（含嵌套后缀句柄）
        var walked = BlockWalker.Walk(drawing.Doc, drawing.Sheets).ToList();
        var instances = walked.Where(w => w.Entity is ACadSharp.Entities.Insert ins
            && string.Equals(ins.Block?.Name, blockName, StringComparison.OrdinalIgnoreCase)).ToList();
        string overlayPath = SemanticOverlayPath(agentDir);
        var overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
        if (overlay is null) return 1;
        foreach (var w in instances)
        {
            // 每句柄先清全 provenance 再落人工确认（镜像 ApplyMappingToAnnotations）
            overlay.Annotations.RemoveAll(a => a.TargetHandle == w.HandlePath);
            overlay.UpsertAnnotation(new SemanticAnnotation(w.HandlePath, cls, Provenance.HumanConfirmed, 1.0,
                $"人工确认块映射: {blockName} → {symbolName}", w.SheetId));
        }
        overlay.Save(overlayPath);
        // map-block 只改叠加层不改图纸：前后哈希一致（SemanticMap 先例）。
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "semantic", "semantic-map-block", blockName,
            JsonSerializer.Serialize(new { block = blockName, symbol = symbolName, @class = cls.ToString(), instances = instances.Count, distance = d, exact }),
            hash, hash, "", ""));
        stdout.WriteLine(JsonSerializer.Serialize(new { block = blockName, symbol = symbolName, @class = cls.ToString(), instances = instances.Count, distance = d, exact }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>
    /// map-block 拒绝留痕（spec §13.3）：审计 semantic-map-block-rejected
    /// （Source="semantic"，Target=blockName，params {block,symbol,reason}，
    /// overlay-only 口径 before==after）+ stderr + exit 1。拒绝先于任何 overlay 写入。
    /// </summary>
    private static int RejectMapBlock(SqliteAuditStore audit, OperatorIdentity op, string session,
        DrawingSession drawing, string blockName, string symbolName, string reason, TextWriter stderr)
    {
        string hash = drawing.ComputeHash();
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "semantic", "semantic-map-block-rejected", blockName,
            JsonSerializer.Serialize(new { block = blockName, symbol = symbolName, reason }),
            hash, hash, "", ""));
        stderr.WriteLine($"块映射确认拒绝: {reason}（block={blockName}, symbol={symbolName}）");
        return 1;
    }

    private static string AdjudicationsPath(string agentDir) =>
        Path.Combine(agentDir, "adjudications.json");

    /// <summary>
    /// 规范引擎装配（T3 评审裁定带入 T7 的装配守卫）：重复 CheckType 注册直接抛异常，
    /// 防止 RuleEngine 字典 last-wins 静默丢失检查器。异常由 Run 兜底→stderr + exit 1。
    /// 2b-ii-b T6：注册电气/模型 5 新检查器（T3-T5 交付）——builtin 仅 50001 类型，
    /// builtin-only 行为不变；--skill 电气/模型包规则自此可评估（此前 NoChecker 跳过）。
    /// </summary>
    private static RuleEngine StandardsEngine()
    {
        IRuleChecker[] checkers =
        {
            new TextHeightChecker(), new LineweightChecker(), new AxisLetterChecker(),
            new ElevationChecker(), new LayerNameChecker(),
            new TextHeightMinChecker(), new CableFormatChecker(), new SlotCodeChecker(),
            new LuminaireFormatChecker(), new FileNamingChecker(),
            new RoomNameChecker()
        };
        var duplicate = checkers.GroupBy(c => c.CheckType, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"重复注册检查器 CheckType: {duplicate.Key}");
        return new RuleEngine(checkers);
    }

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
        var ruleSet = LoadRuleSet(args, stderr);
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        // T5 顺手修②：裸 Load → LoadWithCensus——只读审查路径享受 persisted census
        // 直载（sidecar 有效时不重读源，与 run/rollback 等价一致性）；check 只读不改图，
        // Yellow 不拦只读审查（built-in 无 mandatory → exit 0 行为保持）。
        var drawing = LoadOrReject(() => LoadWithCensus(drawingPath, agentDir), drawingPath,
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
        var ruleSet = LoadRuleSet(args, stderr);
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

    private static int Usage(TextWriter stderr)
    {
        stderr.WriteLine("用法: cadagent <intake|run|rollback|audit verify|roles show|semantic scan|semantic show|semantic map|semantic promote|check|rule adjudicate|rule list|skill list|skill validate|skill run|view> [参数] [--project <dir>]");
        return 1;
    }

    /// <summary>
    /// run --skill tx 审计装饰器（2b-ii-b T8）：仅把 Source="plan" 的 tx-start/tx-commit
    /// 记录 paramsJson 由裸 planId 改写为 {planId, rulePackVersion}（planId 原值保留，
    /// 规则包版本复现证据——与 check/scan 的 rulePackVersion 同口径）。其余记录原样
    /// 透传；ReadAll/VerifyChain 直通内层存储。只在带 --skill 时装配（加性，无 --skill
    /// 的 run 审计逐字不变）。装饰发生在 Append 入链之前——哈希链对改写后的内容计算，
    /// 链完整性不受影响。
    /// </summary>
    private sealed class RulePackAuditDecorator : IAuditStore
    {
        private readonly IAuditStore _inner;
        private readonly string _rulePackVersion;

        public RulePackAuditDecorator(IAuditStore inner, string rulePackVersion)
        {
            _inner = inner;
            _rulePackVersion = rulePackVersion;
        }

        public AuditRecord Append(AuditEntry e)
        {
            if (e.Source == "plan" && e.Kind is "tx-start" or "tx-commit")
                e = e with
                {
                    ParamsJson = JsonSerializer.Serialize(new { planId = e.ParamsJson, rulePackVersion = _rulePackVersion })
                };
            return _inner.Append(e);
        }

        public IReadOnlyList<AuditRecord> ReadAll() => _inner.ReadAll();

        public bool VerifyChain(out string error) => _inner.VerifyChain(out error);
    }
}
