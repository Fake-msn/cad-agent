using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;
using CadAgent.View.Services;

namespace CadAgent.View.ViewModels;

/// <summary>手动编辑工具（计划4 T9）。Select 为默认选择工具，不产生编辑。
/// SetText=「改文字」入口（项2 T4 只加枚举/kind，UI 接线在 T5）；追加在末尾不改既有序值。</summary>
public enum EditTool { Select, Line, Text, Move, Delete, ChangeLayer, InsertBlock, SetText }

/// <summary>编辑结果：Ok/TxId/Error（请求被拒于执行之前时 TxId 为空）；Warning 为编辑后回归警告
/// （视图闭环 T7，镜像 CLI run-regression，观察性非阻塞）。Yellow 闸口警示通道自防假 Green T3
/// 收紧后恒空（Yellow 一律拒绝，PR#20；签名兼容保留）。</summary>
public sealed record EditResult(bool Ok, string TxId, string Error, string Warning = "");

/// <summary>
/// 手动编辑视图模型（计划4 T9）：纯 C#，无 Avalonia 依赖，可单测。
/// **所有编辑经 StepEngine——无直接 Doc 修改、无审计旁路**：
/// ApplyAsync 构造单步 StepPlan（planId="view-edit-{guid:N}"，kind 对应处理器
/// 契约；项2 T4 追加 set-text/set-attribute 入口与 space/layout/attribute 限定）→ 先审计 view-edit 请求（Source="view"，Kind="view-edit"，
/// params{tool,args,planId}，Target=图纸路径，前后哈希一致——请求本身不改图，
/// PlanRef=planId 供审计员关联后续 tx）→ 准入闸口（镜像 CLI RunPlan：
/// GateDetector.Inspect + EmptySupportMatrix，Red/Yellow→run-rejected 留痕拒绝执行——
/// T3 只读审查，Yellow 不再放行）→ StepEngine.Run（语义闸口接线逐字镜像
/// CLI RunPlan：.cadagent/semantic.json 存在→加载注入，损坏→intake-rejected
/// 留痕后拒绝执行，绝不静默降级）。成功：StepEngine 已 SaveWorking，session.Doc
/// 即最新（不 Reload），RecentTxIds 压栈；失败：ErrorMessage + session.Reload
/// （镜像 CLI run 的 if (!result.Ok) drawing.Reload()——丢弃未落盘的内存改动，
/// 无部分保存）。UndoAsync 镜像 CLI rollback：快照归属校验（不符→
/// rollback-rejected 留痕拒绝）→ StepEngine.Rollback（Restore+Reload+rollback
/// 审计）→ 撤销栈移除。EditCompleted 在调用方上下文触发（成功或失败皆触发，
/// UI 据此重建渲染模型 + 图层面板）。
/// 会话闸（T6 承接）：可选共享 SemaphoreSlim(1,1)（MainWindow 装配，同时传给
/// AuditViewModel/IssuesViewModel/SemanticViewModel 的后台 session 读）——
/// 编辑/撤销整段持闸，杜绝 off-thread diff/check/scan 读到被编辑撕扯的 Doc；
/// 未提供时自建私有闸（编辑/撤销之间仍串行）。
/// [合并前 ISTQB I3 边界注记] 上述"杜绝"仅限取闸的 off-thread 消费者；
/// UI 线程 RefreshAfterEdit（含 PlanExecuted 释闸后触发的全视图刷新）不取闸枚举 Doc，
/// 与并发编辑的窄窗竞态仍在——刷新侧取闸仍 deferred（窄窗竞态跟踪，
/// 见 view-closure 计划执行记录"合并前 ISTQB 审查 tracked deferred"小节）。
/// </summary>
public sealed class EditViewModel
{
    private readonly DrawingSession _session;
    private readonly IAuditStore _audit;
    private readonly SnapshotStore _snapshots;
    private readonly IIdentityProvider _identity;
    private readonly RoleChecker _roles;
    private readonly string _projectDir;
    private readonly string _sessionId;
    private readonly SemaphoreSlim _gate;
    private readonly List<string> _recentTxIds = new();

    // set-attribute 无 EditTool 成员（无独立工具按钮）：tool/kind 成对常量化，防字面量漂移。
    private const string SetAttributeToolName = "SetAttribute";
    private const string SetAttributeKind = "set-attribute";

    public EditViewModel(DrawingSession session, IAuditStore auditStore, SnapshotStore snapshotStore,
        IIdentityProvider identity, RoleChecker roleChecker, string projectDir,
        string? sessionId = null, SemaphoreSlim? sessionGate = null)
    {
        _session = session;
        _audit = auditStore;
        _snapshots = snapshotStore;
        _identity = identity;
        _roles = roleChecker;
        _projectDir = projectDir;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _gate = sessionGate ?? new SemaphoreSlim(1, 1);
    }

    /// <summary>当前工具（UI 工具栏设置；Select 不产生编辑）。</summary>
    public EditTool ActiveTool { get; set; } = EditTool.Select;

    /// <summary>最近一次编辑/撤销失败原因；null=成功。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>撤销栈（成功事务号，压栈序；UndoAsync 成功后移除）。</summary>
    public IReadOnlyList<string> RecentTxIds => _recentTxIds;

    /// <summary>编辑/撤销完成（成功或失败）→ UI 重建渲染。在调用方上下文触发。</summary>
    public event Action? EditCompleted;

    // ---- 类型化便捷入口（UI/测试）：args 与六处理器 params 契约一一对应 ----
    // 项2 T4：space/layout/attribute 仅在有效时写入 args——审计 view-edit 请求与 step
    // params 因此不留 "space":null/"attribute":false 噪声（conditional assembly）。

    public Task<EditResult> DrawLineAsync(double x1, double y1, double x2, double y2,
        string? layer = null, string? space = null, string? layout = null)
    {
        var args = new Dictionary<string, object?> { ["x1"] = x1, ["y1"] = y1, ["x2"] = x2, ["y2"] = y2, ["layer"] = layer };
        AddSpace(args, space, layout);
        return ApplyAsync(EditTool.Line, JsonSerializer.SerializeToElement(args));
    }

    public Task<EditResult> AddTextAsync(double x, double y, string text, double height,
        string? layer = null, string? space = null, string? layout = null)
    {
        var args = new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["text"] = text, ["height"] = height, ["layer"] = layer };
        AddSpace(args, space, layout);
        return ApplyAsync(EditTool.Text, JsonSerializer.SerializeToElement(args));
    }

    /// <summary>block=块名 → 块定义实体（全实例生效；与 space/layout/attribute 互斥，由处理器校验）。
    /// affectedInstances 为视图确认时已算出的影响实例数（审计附注；引擎不依赖，仅非空时落 params）。</summary>
    public Task<EditResult> MoveAsync(string handle, double dx, double dy,
        string? space = null, string? layout = null, string? block = null,
        int? affectedInstances = null) =>
        WithHandle(handle, h =>
        {
            var args = HandleArgs(h, space, layout, block, attribute: false);
            args["dx"] = dx;
            args["dy"] = dy;
            AddAffectedInstances(args, affectedInstances);
            return ApplyAsync(EditTool.Move, JsonSerializer.SerializeToElement(args));
        });

    /// <summary>attribute=true 时仅检索 Insert 属性（实例语义；ATTRIB 句柄与实体句柄同口径）。</summary>
    public Task<EditResult> DeleteAsync(string handle, string? space = null, string? layout = null,
        bool attribute = false, string? block = null, int? affectedInstances = null) =>
        WithHandle(handle, h =>
        {
            var args = HandleArgs(h, space, layout, block, attribute);
            AddAffectedInstances(args, affectedInstances);
            return ApplyAsync(EditTool.Delete, JsonSerializer.SerializeToElement(args));
        });

    public Task<EditResult> ChangeLayerAsync(string handle, string layer,
        string? space = null, string? layout = null, string? block = null,
        int? affectedInstances = null) =>
        WithHandle(handle, h =>
        {
            var args = HandleArgs(h, space, layout, block, attribute: false);
            args["layer"] = layer;
            AddAffectedInstances(args, affectedInstances);
            return ApplyAsync(EditTool.ChangeLayer, JsonSerializer.SerializeToElement(args));
        });

    /// <summary>插块（space:"paper"+layout → 布局纸面，项2 T5；默认模型，源兼容）。</summary>
    public Task<EditResult> InsertBlockAsync(string block, double x, double y, double scale = 1,
        double rotation = 0, string? space = null, string? layout = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["block"] = block, ["x"] = x, ["y"] = y, ["scale"] = scale, ["rotation"] = rotation
        };
        AddSpace(args, space, layout);
        return ApplyAsync(EditTool.InsertBlock, JsonSerializer.SerializeToElement(args));
    }

    /// <summary>改文字（spec §3.3 set-text）：TEXT/MText/ATTRIB 改值；block=块内文字（全实例生效）。</summary>
    public Task<EditResult> SetTextAsync(string handle, string text, string? block = null,
        int? affectedInstances = null) =>
        WithHandle(handle, h =>
        {
            var args = new Dictionary<string, object?> { ["handle"] = h, ["text"] = text };
            if (block is not null) args["block"] = block; // 条件写入：不留 "block":null 噪声
            AddAffectedInstances(args, affectedInstances);
            return ApplyAsync(EditTool.SetText, JsonSerializer.SerializeToElement(args));
        });

    /// <summary>改实例属性值（spec §3.3 set-attribute 句柄形态；整实例级，不涉块定义）。</summary>
    public Task<EditResult> SetAttributeAsync(string handle, string value) =>
        WithHandle(handle, h => ApplyAsync(SetAttributeToolName, SetAttributeKind,
            JsonSerializer.SerializeToElement(new { handle = h, value })));

    private static Dictionary<string, object?> HandleArgs(ulong handle, string? space, string? layout,
        string? block, bool attribute)
    {
        var args = new Dictionary<string, object?> { ["handle"] = handle };
        AddSpace(args, space, layout);
        if (block is not null) args["block"] = block; // 仅有效时落 params（与 space 同口径）
        if (attribute) args["attribute"] = true; // 仅 true 落 params（false 无意义，徒增噪声）
        return args;
    }

    private static void AddSpace(Dictionary<string, object?> args, string? space, string? layout)
    {
        if (space is not null) args["space"] = space;
        if (layout is not null) args["layout"] = layout;
    }

    /// <summary>仅块编辑路径携带（视图确认时已算出的影响实例数）；非空才落 params，不产生 null 噪声。</summary>
    private static void AddAffectedInstances(Dictionary<string, object?> args, int? affectedInstances)
    {
        if (affectedInstances is int n) args["affectedInstances"] = n;
    }

    /// <summary>句柄为十进制 ulong 字符串（RenderItem.Handle/审计 Target 同口径）；非法→拒绝，不留审计。</summary>
    private static Task<EditResult> WithHandle(string handle, Func<ulong, Task<EditResult>> apply) =>
        ulong.TryParse(handle, out ulong h)
            ? apply(h)
            : Task.FromResult(new EditResult(false, "", $"非法句柄: {handle}"));

    public async Task<EditResult> ApplyAsync(EditTool tool, JsonElement args)
    {
        if (tool == EditTool.Select)
            return new EditResult(false, "", "Select 工具不产生编辑");
        if (args.ValueKind != JsonValueKind.Object)
            return new EditResult(false, "", "编辑参数必须是 JSON 对象");
        return await ApplyAsync(tool.ToString(), KindFor(tool), args);
    }

    /// <summary>
    /// 核心入口：toolName→kind 单步计划。校验（Select/非对象 args）先于任何副作用；
    /// 请求审计先于 tx；核心体在后台执行并整段持会话闸，结果回到调用方上下文发布。
    /// args 须为调用方保活的 JsonElement（SerializeToElement 产物或已 Clone）。
    /// 拆出 toolName 形态：set-attribute 无对应 EditTool 成员（UI 无独立工具按钮），
    /// 由 SetAttributeAsync 直接给 kind，仍走同一条审计/闸口/引擎通道。
    /// </summary>
    private async Task<EditResult> ApplyAsync(string toolName, string kind, JsonElement args)
    {
        // 与公开重载同口径：非对象 args 先于任何副作用拒绝（不留审计、不取闸）。
        if (args.ValueKind != JsonValueKind.Object)
            return new EditResult(false, "", "编辑参数必须是 JSON 对象");
        string planId = "view-edit-" + Guid.NewGuid().ToString("N");

        await _gate.WaitAsync();
        EditResult result;
        try
        {
            try
            {
                result = await Task.Run(() => ApplyCore(toolName, kind, args, planId));
            }
            catch (Exception ex)
            {
                // 角色拒绝/overlay 损坏/审计故障等：请求可能已留痕，tx 未开始或已失败。
                result = new EditResult(false, "", ex.Message);
            }
            if (result.Ok)
            {
                _recentTxIds.Add(result.TxId);
                ErrorMessage = null;
            }
            else
            {
                // 失败 → Reload（镜像 CLI run）：丢弃未落盘的内存改动，无部分保存。
                // Reload 自身失败（IO 故障）→ 追加进错误信息，不掩盖原因。
                try { _session.Reload(); }
                catch (Exception ex) { result = result with { Error = $"{result.Error}（回读失败: {ex.Message}）" }; }
                ErrorMessage = result.Error;
            }
        }
        finally { _gate.Release(); }
        EditCompleted?.Invoke();
        return result;
    }

    /// <summary>
    /// 撤销：镜像 CLI rollback 的快照归属校验 + StepEngine.Rollback。
    /// 失败降级 ErrorMessage（rollback-failed 由引擎留痕；快照缺失同 CLI
    /// 在任何副作用之前抛出，不留审计）。成功/失败皆触发 EditCompleted。
    /// </summary>
    public async Task UndoAsync(string txId)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() => UndoCore(txId));
            _recentTxIds.Remove(txId);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { _gate.Release(); }
        EditCompleted?.Invoke();
    }

    /// <summary>请求审计先于执行；闸口注入镜像 CLI RunPlan；tx 结果原样透传。</summary>
    private EditResult ApplyCore(string toolName, string kind, JsonElement args, string planId)
    {
        OperatorIdentity op = _identity.Current;
        // 请求审计（先于 tx）：请求本身不改图 → 前后哈希一致；PlanRef=planId 关联 tx。
        string hash = _session.ComputeHash();
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            _sessionId, "view", "view-edit", _session.SourcePath,
            JsonSerializer.Serialize(new { tool = toolName, args, planId }),
            hash, hash, "", planId));

        // 准入闸口（最终审查修复①，镜像 CLI RunPlan，spec §4.2）：每次 ApplyAsync 都检
        // （session 已加载，Summarize+Inspect 开销小）。T3 收紧：Red/Yellow 一律拒绝——
        // 未解析对象无句柄/几何，禁触区无法定位，全图级拦截（Yellow=只读审查）。
        // run-rejected 留痕（Source="view"，params{level,unsupportedCount,unparsedCount,guidance}
        // 镜像 CLI 字段，前后哈希一致——拒绝本身不改图）后拒绝执行，绝不开始 tx。
        GateReport gate = GateDetector.Inspect(_session.Summarize(), new EmptySupportMatrix());
        if (gate.Level != GateLevel.Green)
        {
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                _sessionId, "view", "run-rejected", _session.SourcePath,
                JsonSerializer.Serialize(new
                {
                    level = gate.Level.ToString(),
                    unsupportedCount = gate.Unsupported.Count,
                    unparsedCount = _session.Unparsed.TotalCount,
                    guidance = gate.Guidance
                }),
                hash, hash, "", planId));
            return new EditResult(false, "", gate.Guidance);
        }

        // 语义闸口接线（镜像 CLI RunPlan）：overlay 存在→加载注入；不存在→null
        // （几何级流程不受影响）；存在但损坏→intake-rejected 留痕后拒绝，不静默降级。
        string overlayPath = Path.Combine(_projectDir, ".cadagent", "semantic.json");
        ISemanticGate? semanticGate = null;
        if (File.Exists(overlayPath))
        {
            SemanticOverlay overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath);
            semanticGate = new SemanticGate(overlay);
        }
        var engine = new StepEngine(_audit, _snapshots, _identity, _roles, HandlerRegistry.All(), semanticGate);
        var plan = new StepPlan(planId, new[] { new StepSpec(kind, args) });
        TxResult tx = engine.Run(plan, _session, _sessionId);
        // 视图闭环 T7：tx 成功后回归检查（镜像 CLI run 的 RunRegressionCheck，观察性）——
        // ApplyCore 整体在 _gate 临界区内，ComputeHash 自然持闸（Task 6 哈希口径教训），
        // 无需额外加闸。PR#20 合并注记：T3 收紧后 Yellow 一律拒绝（原 gateWarning
        // 叠加通道恒空），回归警告为 Warning 唯一来源（T7 行为保持）。
        string warning = "";
        if (tx.Ok)
        {
            string regressionWarning = RunRegressionCheck();
            if (regressionWarning.Length > 0) warning = regressionWarning;
        }
        return tx.Ok ? new EditResult(true, tx.TxId, "", warning) : new EditResult(false, tx.TxId, tx.Error);
    }

    /// <summary>
    /// 编辑后回归检查（镜像 CLI RunRegressionCheck，spec §9 观察性）：builtin-only 评估，
    /// mandatory 违规→警告不自动回滚（决策权归人）；无论有无违规均审计 check-run；
    /// 自身故障也入审计（嵌套守卫：审计再失败仅降级提示，绝不重抛影响已提交 tx）。
    /// </summary>
    private string RunRegressionCheck()
    {
        bool overlayPresent = false;
        try
        {
            string agentDir = Path.Combine(_projectDir, ".cadagent");
            string overlayPath = Path.Combine(agentDir, "semantic.json");
            var overlay = File.Exists(overlayPath) ? SemanticOverlay.Load(overlayPath) : new SemanticOverlay();
            overlayPresent = overlay.Annotations.Count > 0;
            var adjudications = AdjudicationSet.Load(Path.Combine(agentDir, "adjudications.json"));
            var ruleSet = SkillSet.Compose(BuiltinRules.Load(), Array.Empty<SkillPackage>());
            CheckSummary summary = ViewRuleEngine.Create().Evaluate(ruleSet.AllRules, _session, overlay, adjudications);
            int mandatoryViolations = summary.Issues.Count(i => i.Force == RuleForce.Mandatory);
            string hash = _session.ComputeHash();
            var op = _identity.Current;
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                _sessionId, "view-regression", "check-run", _session.SourcePath,
                JsonSerializer.Serialize(new
                {
                    rulesRun = summary.RulesRun,
                    rulesSkipped = summary.RulesSkipped,
                    issueCount = summary.Issues.Count,
                    mandatoryViolations,
                    skippedReasons = summary.StructuredSkips?.Select(s => new { category = s.Category, ruleId = s.RuleId, message = s.Message }).ToArray() ?? Array.Empty<object>(),
                    overlayPresent,
                    adjudicationHash = adjudications.Fingerprint(),
                    rulePackVersion = ruleSet.RulePackVersion
                }),
                hash, hash, "", ""));
            if (mandatoryViolations > 0)
            {
                string ruleIds = string.Join(", ", summary.Issues
                    .Where(i => i.Force == RuleForce.Mandatory).Select(i => i.RuleId).Distinct());
                return $"警告: 编辑后图纸存在 {mandatoryViolations} 项强制(mandatory)规范违规（规则: {ruleIds}）。事务已提交，不自动回滚——决策权归人，请在问题清单复核并人工裁定或修正。";
            }
            return "";
        }
        catch (Exception ex)
        {
            try
            {
                var op = _identity.Current;
                _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    _sessionId, "view-regression", "check-run", _session.SourcePath,
                    JsonSerializer.Serialize(new { Error = ex.Message, overlayPresent }),
                    "", "", "", ""));
            }
            catch { /* 嵌套守卫：审计再失败不重抛（镜像 CLI 最终审查裁定） */ }
            return $"回归检查未完成: {ex.Message}（事务已提交，不受影响）";
        }
    }

    /// <summary>镜像 CLI Rollback：快照必须属于当前图纸，防止跨图纸覆盖。</summary>
    private void UndoCore(string txId)
    {
        string snapshotFile = _snapshots.Find(txId);
        if (!string.Equals(Path.GetFileName(snapshotFile), Path.GetFileName(_session.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            OperatorIdentity op = _identity.Current;
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                _sessionId, "rollback", "rollback-rejected", txId,
                JsonSerializer.Serialize(new
                {
                    Expected = Path.GetFileName(_session.SourcePath),
                    Actual = Path.GetFileName(snapshotFile)
                }),
                "", "", txId, ""));
            throw new InvalidOperationException($"事务 {txId} 的快照不属于图纸 {_session.SourcePath}，已拒绝撤回");
        }
        var engine = new StepEngine(_audit, _snapshots, _identity, _roles, HandlerRegistry.All());
        engine.Rollback(txId, _session, _sessionId);
    }

    /// <summary>
    /// 加载守卫——逐字镜像 CLI LoadOrReject（spec §9）：overlay Load 失败 →
    /// intake-rejected 留痕（Source="semantic"，与 CLI run 的 overlay 守卫同口径，
    /// target=失败文件，params{Error,File}，哈希不可得→空串）后重新抛出。
    /// </summary>
    private T LoadOrReject<T>(Func<T> load, string file) where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            OperatorIdentity op = _identity.Current;
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                _sessionId, "semantic", "intake-rejected", file,
                JsonSerializer.Serialize(new { Error = ex.Message, File = file }), "", "", "", ""));
            throw;
        }
    }

    private static string KindFor(EditTool tool) => tool switch
    {
        EditTool.Line => "add-line",
        EditTool.Text => "add-text",
        EditTool.Move => "move-entity",
        EditTool.Delete => "delete-entity",
        EditTool.ChangeLayer => "change-layer",
        EditTool.InsertBlock => "insert-block",
        EditTool.SetText => "set-text",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "未知编辑工具")
    };
}
