using System.Text.Json;
using System.Text.RegularExpressions;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CadAgent.Cli;

// skill 命令族 partial（2b-ii-b Task 1 纯移动提取；Task 8 增 skill run）：skill list/validate/run、
// semantic promote、--skill 规则集装配与结构化跳过 helper。共享 helper（OpenAudit/Identity/LoadOrReject/
// JsonOpts/SemanticJsonOpts 等）留主文件 CommandRouter.cs，partial 同类可见。
public static partial class CommandRouter
{
    /// <summary>
    /// skill 命令族分发（2b-ii-a T7；2b-ii-b T8 增 run）。子命令校验沿用 semantic/rule
    /// 族模式：缺失/未知子命令一律 stderr + exit 1，不落入 Usage 歧义。
    /// </summary>
    private static int Skill(string[] args, string[] positional, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2)
        {
            stderr.WriteLine("skill 需要子命令: list|validate|run");
            return 1;
        }
        return positional[1] switch
        {
            "list" => SkillList(agentDir, stdout, stderr),
            "validate" => SkillValidate(positional, stdout),
            "run" => SkillRun(args, positional, agentDir, session, stdout, stderr),
            _ => UnknownSkillSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownSkillSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 skill 子命令: {sub}（可用: list|validate|run）");
        return 1;
    }

    /// <summary>
    /// skill run（2b-ii-b T8）：模板实例化→StepEngine 执行（审计/快照/闸口全走既有引擎，
    /// 无执行旁路）。流程：
    /// 1) 包加载 LoadOrReject（Source="skill"，非法包→audited refusal exit 1）；
    /// 2) 按名找模板（Ordinal；缺失→用法错误 stderr + exit 1，不审计——map"未找到叠加层"先例）；
    /// 3) --args 解析为 JSON 对象（非法/非对象→用法错误 stderr + exit 1；值以 JsonElement
    ///    克隆透传，数值保持数值——引擎处理器 GetDouble 不接受字符串）；Instantiate
    ///    缺参/未知占位符→InvalidSkillException→LoadOrReject audited refusal exit 1；
    /// 4) 请求审计先行（执行前）：Source="skill"、Kind="skill-run"、
    ///    paramsJson={skill,template,args,planId}，PlanRef=planId，哈希不可得→空串；
    /// 5) 读图+三级闸口（镜像 RunPlan：LoadWithCensus sidecar 接线；Load/Inspect 失败→
    ///    LoadOrReject audited refusal；Red/Yellow→run-rejected 留痕拒执行——T3 只读审查，
    ///    Yellow 不再警示放行）；overlay 存在→SemanticGate 注入
    ///    （损坏→LoadOrReject 审计拒绝，不静默降级——与 run 一致）；
    /// 6) StepEngine.Run→stdout TxResult JSON（与 run 同形 {txId,ok,stepsDone,error}）；
    ///    失败事务→drawing.Reload()（镜像 RunPlan）；exit 0/1。
    /// 必需选项缺失（--template/--drawing/--args）→RequireOption 异常由 Run 兜底
    /// stderr + exit 1（用法错误，先于 OpenAudit）。
    /// </summary>
    private static int SkillRun(string[] args, string[] positional, string agentDir,
        string session, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 3)
        {
            stderr.WriteLine("skill run 需要目录参数: skill run <skillDir> --template <name> --drawing <file> --args <json>");
            return 1;
        }
        string skillDir = positional[2];
        string templateName = RequireOption(args, "--template");
        string drawingPath = RequireOption(args, "--drawing");
        string argsJson = RequireOption(args, "--args");
        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var package = LoadOrReject(() => SkillLoader.LoadDirectory(skillDir), skillDir,
            session, audit, op, stderr, source: "skill");
        if (package is null) return 1;
        var template = package.Steps.FirstOrDefault(t => string.Equals(t.Name, templateName, StringComparison.Ordinal));
        if (template is null)
        {
            stderr.WriteLine($"未找到模板: {templateName}（skill '{package.Manifest.Name}' 的 steps/ 不含该模板）");
            return 1;
        }
        Dictionary<string, object> templateArgs;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("--args 必须是 JSON 对象（参数名→值）");
            templateArgs = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"--args JSON 非法: {ex.Message}");
            return 1;
        }
        var plan = LoadOrReject(() => TemplateInstantiator.Instantiate(template, templateArgs), skillDir,
            session, audit, op, stderr, source: "skill");
        if (plan is null) return 1;
        // 请求审计先行（brief 步骤4）：先于读图与执行——执行失败/读图失败也不丢请求留痕。
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            session, "skill", "skill-run", drawingPath,
            JsonSerializer.Serialize(new { skill = skillDir, template = templateName, args = templateArgs, planId = plan.PlanId }),
            "", "", "", plan.PlanId));
        // 读图+三级闸口（镜像 RunPlan）：Load/Inspect 失败一并 LoadOrReject 留痕。
        var loaded = LoadOrReject(() =>
        {
            var d = LoadWithCensus(drawingPath, agentDir);
            return Tuple.Create(d, GateDetector.Inspect(d.Summarize(), new EmptySupportMatrix()));
        }, drawingPath, session, audit, op, stderr, source: "skill");
        if (loaded is null) return 1;
        var (drawing, report) = loaded;
        if (GateRefusesRun(report))
        {
            // spec 硬要求6（T3 收紧）：Red/Yellow 图纸禁止修改——不执行任何步骤（与 run 一致；
            // Yellow=只读审查，未解析对象无句柄/几何，禁触区无法定位，全图级拦截）。
            string hash = drawing.ComputeHash();
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "skill", "run-rejected", drawingPath,
                JsonSerializer.Serialize(new
                {
                    level = report.Level.ToString(),
                    unsupportedCount = report.Unsupported.Count,
                    unparsedCount = drawing.Unparsed.TotalCount,
                    guidance = report.Guidance
                }),
                hash, hash, "", plan.PlanId));
            stderr.WriteLine(report.Guidance);
            return 1;
        }
        // 语义闸口接线（镜像 RunPlan）：overlay 存在→加载注入；不存在→null（几何级流程
        // 不受影响）；存在但损坏→审计拒绝、不执行（不得静默降级为 null 闸口）。
        string overlayPath = SemanticOverlayPath(agentDir);
        ISemanticGate? semanticGate = null;
        if (File.Exists(overlayPath))
        {
            var overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
            if (overlay is null) return 1;
            semanticGate = new SemanticGate(overlay);
        }
        var result = Engine(agentDir, audit, semanticGate, null, SkillRunImportHandler(skillDir, args)).Run(plan, drawing, session);
        string? liveOut = null;
        if (!result.Ok) drawing.Reload();
        else
        {
            var live = LiveReplay(args, agentDir, session, audit, op, drawing, plan, LivePackages(args, skillDir), stderr);
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

    /// <summary>
    /// 可重复选项的全部值（按出现序）——--skill 可重复（2b-ii-a T6）。
    /// 尾随无值选项（2b-ii-b Task 2）：stderr 非 null 时警告"忽略无值选项 {name}"
    /// 并宽容继续（不 exit 1）；stderr 为 null（默认）保持旧静默行为。
    /// </summary>
    /// <summary>
    /// 3h：skill run 的符号导入处理器——包目录为位置参数（非 --skill），故以 skillDir 为主
    /// 合并 --skill 附加包（去重）；模板 insert-symbol 步骤自此可解析本包符号。
    /// </summary>
    private static IStepHandler SkillRunImportHandler(string skillDir, string[] args)
    {
        var dirs = new List<string> { skillDir };
        dirs.AddRange(GetOptionValues(args, "--skill"));
        var packages = dirs.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(SkillLoader.LoadDirectory).ToList();
        return new ImportSymbolHandler(packages);
    }

    private static List<string> GetOptionValues(string[] args, string name, TextWriter? stderr = null)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 < args.Length) values.Add(args[i + 1]);
            else stderr?.WriteLine($"忽略无值选项 {name}");
        }
        return values;
    }

    /// <summary>
    /// 规则集装配（2b-ii-a T6）：builtin + --skill 包（可重复，显式目录路径，按出现序）
    /// →SkillSet.Compose。adjudicate 孤儿校验消费 ContainsRule；T7 check/scan --skill
    /// 接线复用同一加载路径（check 消费 AllRules/RulePackVersion，scan 消费 MergedAliases）。
    /// skill 目录非法/规则 Id 冲突→异常由 Run 兜底 stderr + exit 1
    /// （用法错误不审计——与 adjudicate 互斥校验先例一致，校验先于任何副作用）。
    /// </summary>
    private static SkillSet LoadRuleSet(string[] args, TextWriter? stderr = null)
    {
        var packages = GetOptionValues(args, "--skill", stderr).Select(SkillLoader.LoadDirectory).ToList();
        return SkillSet.Compose(BuiltinRules.Load(), packages);
    }

    /// <summary>
    /// BF-T3：--skill 包列表（可重复，按出现序）→符号库索引供块级指纹匹配消费。
    /// 与 LoadRuleSet 同一加载路径（SkillLoader.LoadDirectory）；包非法→异常由 Run 兜底
    /// stderr + exit 1（用法错误先于任何副作用，LoadRuleSet 先例）。无 --skill→空列表。
    /// </summary>
    internal static IReadOnlyList<SkillPackage> LoadSkillPackages(string[] args) =>
        GetOptionValues(args, "--skill").Select(SkillLoader.LoadDirectory).ToList();

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
}
