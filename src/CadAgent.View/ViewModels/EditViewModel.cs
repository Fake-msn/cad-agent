using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Semantic;

namespace CadAgent.View.ViewModels;

/// <summary>手动编辑工具（计划4 T9）。Select 为默认选择工具，不产生编辑。</summary>
public enum EditTool { Select, Line, Text, Move, Delete, ChangeLayer, InsertBlock }

/// <summary>编辑结果：Ok/TxId/Error（请求被拒于执行之前时 TxId 为空）。Warning 为历史字段（T3 起 Yellow 一律拒绝，恒空；签名兼容保留）。</summary>
public sealed record EditResult(bool Ok, string TxId, string Error, string Warning = "");

/// <summary>
/// 手动编辑视图模型（计划4 T9）：纯 C#，无 Avalonia 依赖，可单测。
/// **所有编辑经 StepEngine——无直接 Doc 修改、无审计旁路**：
/// ApplyAsync 构造单步 StepPlan（planId="view-edit-{guid:N}"，kind 对应六处理器
/// 契约）→ 先审计 view-edit 请求（Source="view"，Kind="view-edit"，
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

    public Task<EditResult> DrawLineAsync(double x1, double y1, double x2, double y2, string? layer = null) =>
        ApplyAsync(EditTool.Line, JsonSerializer.SerializeToElement(new { x1, y1, x2, y2, layer }));

    public Task<EditResult> AddTextAsync(double x, double y, string text, double height, string? layer = null) =>
        ApplyAsync(EditTool.Text, JsonSerializer.SerializeToElement(new { x, y, text, height, layer }));

    public Task<EditResult> MoveAsync(string handle, double dx, double dy) =>
        WithHandle(handle, h => ApplyAsync(EditTool.Move, JsonSerializer.SerializeToElement(new { handle = h, dx, dy })));

    public Task<EditResult> DeleteAsync(string handle) =>
        WithHandle(handle, h => ApplyAsync(EditTool.Delete, JsonSerializer.SerializeToElement(new { handle = h })));

    public Task<EditResult> ChangeLayerAsync(string handle, string layer) =>
        WithHandle(handle, h => ApplyAsync(EditTool.ChangeLayer, JsonSerializer.SerializeToElement(new { handle = h, layer })));

    public Task<EditResult> InsertBlockAsync(string block, double x, double y, double scale = 1, double rotation = 0) =>
        ApplyAsync(EditTool.InsertBlock, JsonSerializer.SerializeToElement(new { block, x, y, scale, rotation }));

    /// <summary>句柄为十进制 ulong 字符串（RenderItem.Handle/审计 Target 同口径）；非法→拒绝，不留审计。</summary>
    private static Task<EditResult> WithHandle(string handle, Func<ulong, Task<EditResult>> apply) =>
        ulong.TryParse(handle, out ulong h)
            ? apply(h)
            : Task.FromResult(new EditResult(false, "", $"非法句柄: {handle}"));

    /// <summary>
    /// 核心入口：tool→kind 单步计划。校验（Select/非对象 args）先于任何副作用；
    /// 请求审计先于 tx；核心体在后台执行并整段持会话闸，结果回到调用方上下文发布。
    /// args 须为调用方保活的 JsonElement（SerializeToElement 产物或已 Clone）。
    /// </summary>
    public async Task<EditResult> ApplyAsync(EditTool tool, JsonElement args)
    {
        if (tool == EditTool.Select)
            return new EditResult(false, "", "Select 工具不产生编辑");
        if (args.ValueKind != JsonValueKind.Object)
            return new EditResult(false, "", "编辑参数必须是 JSON 对象");
        string kind = KindFor(tool);
        string planId = "view-edit-" + Guid.NewGuid().ToString("N");

        await _gate.WaitAsync();
        EditResult result;
        try
        {
            try
            {
                result = await Task.Run(() => ApplyCore(tool, kind, args, planId));
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
    private EditResult ApplyCore(EditTool tool, string kind, JsonElement args, string planId)
    {
        OperatorIdentity op = _identity.Current;
        // 请求审计（先于 tx）：请求本身不改图 → 前后哈希一致；PlanRef=planId 关联 tx。
        string hash = _session.ComputeHash();
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            _sessionId, "view", "view-edit", _session.SourcePath,
            JsonSerializer.Serialize(new { tool = tool.ToString(), args, planId }),
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
        return tx.Ok ? new EditResult(true, tx.TxId, "") : new EditResult(false, tx.TxId, tx.Error);
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
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "未知编辑工具")
    };
}
