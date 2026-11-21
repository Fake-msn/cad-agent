using System.Globalization;
using System.Text;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.View.Rendering;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 审计条目（计划4 T6）：纯 C#，无 Avalonia 依赖。逐字段映射 Core AuditRecord
/// 的展示字段（Seq/Ts/Operator/Role/Kind/Target/PlanRef）。
/// </summary>
public sealed class AuditEntryVm
{
    internal AuditEntryVm(AuditRecord record)
    {
        Seq = record.Seq;
        Ts = record.Entry.Ts;
        Operator = record.Entry.Operator;
        Role = record.Entry.Role;
        Kind = record.Entry.Kind;
        Target = record.Entry.Target;
        PlanRef = record.Entry.PlanRef;
    }

    public long Seq { get; }
    public DateTimeOffset Ts { get; }
    public string Operator { get; }
    public string Role { get; }
    public string Kind { get; }
    public string Target { get; }
    public string PlanRef { get; }
}

/// <summary>
/// 事务分组（计划4 T6）：以 tx-start 记录为界，按 PlanRef 归拢同事务的
/// step/tx-commit/tx-failed/step-failed/semantic-gate-refused 记录（同一 PlanRef
/// 重复执行 → 多个分组，记录按 Seq 顺序落入最近一个未终结的同 PlanRef 分组）。
/// TxId = tx-start SnapshotRef 的快照目录名（StepEngine 的 GUID 事务号，
/// SnapshotStore.Find 可直接使用）；无快照路径时退化为 PlanRef。
/// Steps 仅含成功的 "step" 记录；HasFailed 覆盖 step-failed、tx-failed 与
/// semantic-gate-refused（StepEngine 语义闸口拒绝后留痕即返回，无 tx-failed/
/// tx-commit 终结记录——该 kind 本身即失败终结，须关闭分组，避免灰色 open
/// 并吞并后续同 PlanRef 记录）。
/// </summary>
public sealed class TxGroup
{
    private readonly List<AuditEntryVm> _entries;

    internal TxGroup(string txId, AuditEntryVm start)
    {
        TxId = txId;
        PlanRef = start.PlanRef;
        StartTs = start.Ts;
        Operator = start.Operator;
        _entries = new List<AuditEntryVm> { start };
    }

    public string TxId { get; }
    public string PlanRef { get; }
    public DateTimeOffset StartTs { get; }
    public string Operator { get; }

    /// <summary>成功步骤（Kind == "step"），Target 为图元句柄。</summary>
    public IReadOnlyList<AuditEntryVm> Steps => _entries.Where(e => e.Kind == "step").ToList();

    public bool HasCommit => _entries.Any(e => e.Kind == "tx-commit");
    public bool HasFailed => _entries.Any(e => e.Kind is "step-failed" or "tx-failed" or "semantic-gate-refused");

    /// <summary>时间线状态徽标：failed ✗ / commit ✓ / open（无终结记录）。</summary>
    public string Status => HasFailed ? "failed ✗" : HasCommit ? "commit ✓" : "open";

    internal bool Closed { get; set; }

    internal void Add(AuditEntryVm entry)
    {
        _entries.Add(entry);
        if (entry.Kind is "tx-commit" or "tx-failed" or "step-failed" or "semantic-gate-refused") Closed = true;
    }
}

/// <summary>
/// 事务前后对比结果（计划4 T6）：BeforeItems = 快照 DXF 渲染图元，
/// AfterItems = 当前会话渲染图元；按 Handle 比对增删，同句柄按几何规范串
/// （Kind+点列+文本+字高+旋转，不含图层/颜色）比对变更。
/// 失败（快照缺失/损坏等）→ ErrorMessage 非 null、各列表为空（不抛出）。
/// </summary>
public sealed class DiffResult
{
    internal DiffResult(IReadOnlyList<RenderItem> beforeItems, IReadOnlyList<RenderItem> afterItems,
        IReadOnlyList<string> addedHandles, IReadOnlyList<string> removedHandles,
        IReadOnlyList<string> changedHandles)
    {
        BeforeItems = beforeItems;
        AfterItems = afterItems;
        AddedHandles = addedHandles;
        RemovedHandles = removedHandles;
        ChangedHandles = changedHandles;
    }

    internal DiffResult(string errorMessage)
    {
        ErrorMessage = errorMessage;
    }

    public IReadOnlyList<RenderItem> BeforeItems { get; } = Array.Empty<RenderItem>();
    public IReadOnlyList<RenderItem> AfterItems { get; } = Array.Empty<RenderItem>();
    public IReadOnlyList<string> AddedHandles { get; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedHandles { get; } = Array.Empty<string>();
    public IReadOnlyList<string> ChangedHandles { get; } = Array.Empty<string>();

    /// <summary>失败原因（快照缺失/DXF 损坏等）；null=成功。</summary>
    public string? ErrorMessage { get; }
}

/// <summary>
/// 审计时间线视图模型（计划4 T6）：纯 C#，可单测。构造时读取 IAuditStore.ReadAll
/// 映射 Entries 并按 tx-start 边界分组 Transactions（读取失败 → ErrorMessage，
/// 列表为空，不崩 UI）。
/// 回放只读：SelectTx 仅高亮该事务 step 的 Target 句柄（canvas 接线时
/// SelectHandles + CenterOn，沿用 T5 定位模式）；LoadDiffAsync 经
/// SnapshotStore.Find → DrawingSession.Load → RenderModelBuilder.Build 渲染
/// 快照，与当前会话比对——绝不 SaveWorking/Restore，不改图纸。
/// </summary>
public sealed class AuditViewModel
{
    private readonly SnapshotStore _snapshots;
    private readonly DrawingSession _session;
    private readonly CanvasViewModel? _canvas;

    public AuditViewModel(IAuditStore auditStore, SnapshotStore snapshotStore,
        DrawingSession session, CanvasViewModel? canvas = null)
    {
        _snapshots = snapshotStore;
        _session = session;
        _canvas = canvas;
        try
        {
            var entries = new List<AuditEntryVm>();
            var groups = new List<TxGroup>();
            TxGroup? open = null;
            foreach (AuditRecord record in auditStore.ReadAll())
            {
                var entry = new AuditEntryVm(record);
                entries.Add(entry);
                if (entry.Kind == "tx-start")
                {
                    open = new TxGroup(TxIdFromSnapshotRef(record.Entry.SnapshotRef, entry.PlanRef), entry);
                    groups.Add(open);
                }
                else if (open is { Closed: false } && entry.PlanRef.Length > 0 && entry.PlanRef == open.PlanRef)
                {
                    open.Add(entry);
                }
            }
            Entries = entries;
            Transactions = groups;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>全部审计记录（Seq 顺序）。</summary>
    public IReadOnlyList<AuditEntryVm> Entries { get; } = Array.Empty<AuditEntryVm>();

    /// <summary>事务分组（tx-start 顺序）。</summary>
    public IReadOnlyList<TxGroup> Transactions { get; } = Array.Empty<TxGroup>();

    /// <summary>审计存储读取失败原因；null=成功。</summary>
    public string? ErrorMessage { get; }

    /// <summary>最近一次 SelectTx 命中的事务号；null=未选择。</summary>
    public string? SelectedTxId { get; private set; }

    /// <summary>选中事务的状态栏摘要（短号/操作者/时间/状态/步数）。</summary>
    public string? SelectedSummary { get; private set; }

    /// <summary>LoadDiffAsync 完成（成功或失败）→ UI 刷新。在调用方上下文触发。</summary>
    public event Action? DiffChanged;

    /// <summary>
    /// 回放选择：返回该事务成功步骤的 Target 句柄（去重保序，空句柄剔除）；
    /// canvas 接线时高亮 + 居中（只读，不改图）。未知事务号 → 空列表，状态不变。
    /// </summary>
    public IReadOnlyList<string> SelectTx(string txId)
    {
        TxGroup? group = Transactions.FirstOrDefault(g => g.TxId == txId);
        if (group is null) return Array.Empty<string>();
        SelectedTxId = txId;
        string shortId = txId.Length > 8 ? txId[..8] : txId;
        SelectedSummary =
            $"{shortId} · {group.Operator} · {group.StartTs.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} · {group.Status} · {group.Steps.Count} steps";
        IReadOnlyList<string> handles = group.Steps
            .Select(s => s.Target).Where(h => h.Length > 0).Distinct().ToList();
        if (_canvas is not null)
        {
            _canvas.SelectHandles(handles);
            _canvas.CenterOn(handles);
        }
        return handles;
    }

    /// <summary>
    /// 加载事务前后对比：后台执行快照加载 + 渲染 + 比对（不阻塞 UI 线程），
    /// 结果回到调用方上下文。任何异常（快照缺失/损坏）→ DiffResult.ErrorMessage。
    /// </summary>
    public async Task<DiffResult> LoadDiffAsync(string txId)
    {
        DiffResult result;
        try
        {
            result = await Task.Run(() => BuildDiff(txId));
        }
        catch (Exception ex)
        {
            result = new DiffResult(ex.Message);
        }
        DiffChanged?.Invoke();
        return result;
    }

    private DiffResult BuildDiff(string txId)
    {
        string snapshotPath = _snapshots.Find(txId);
        // 只读加载快照副本：绝不调用 SaveWorking/Restore，当前会话不被触碰。
        DrawingSession before = DrawingSession.Load(snapshotPath);
        IReadOnlyList<RenderItem> beforeItems = RenderModelBuilder.Build(before);
        IReadOnlyList<RenderItem> afterItems = RenderModelBuilder.Build(_session);

        Dictionary<string, string> beforeKeys = GeometryKeys(beforeItems);
        Dictionary<string, string> afterKeys = GeometryKeys(afterItems);
        var added = afterKeys.Keys.Where(h => !beforeKeys.ContainsKey(h)).ToList();
        var removed = beforeKeys.Keys.Where(h => !afterKeys.ContainsKey(h)).ToList();
        var changed = afterKeys.Where(kv => beforeKeys.TryGetValue(kv.Key, out string? old) && old != kv.Value)
            .Select(kv => kv.Key).ToList();
        return new DiffResult(beforeItems, afterItems, added, removed, changed);
    }

    /// <summary>句柄 → 几何规范串（同句柄多图元时按序拼接，保证确定性）。</summary>
    private static Dictionary<string, string> GeometryKeys(IReadOnlyList<RenderItem> items)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (RenderItem item in items)
        {
            if (item.Handle.Length == 0) continue;
            string key = GeometryKey(item);
            map[item.Handle] = map.TryGetValue(item.Handle, out string? prev) ? prev + "\n" + key : key;
        }
        return map;
    }

    /// <summary>
    /// 几何规范串：Kind + 点列（R 格式不变文化）+ 文本 + 字高 + 旋转。
    /// 图层/颜色不参与——变更口径为几何/文本内容（brief）。
    /// </summary>
    private static string GeometryKey(RenderItem item)
    {
        var sb = new StringBuilder();
        sb.Append(item.Kind).Append('|');
        foreach ((double x, double y) in item.Points)
            sb.Append(x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        sb.Append('|').Append(item.Text ?? "")
          .Append('|').Append(item.TextHeight.ToString("R", CultureInfo.InvariantCulture))
          .Append('|').Append(item.RotationDeg.ToString("R", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// StepEngine 快照路径约定 {root}/{txId}/{file} → 取目录名即事务号。
    /// 分隔符归一化保证跨平台读取 Windows 写入的路径；无路径信息退化为 PlanRef。
    /// </summary>
    private static string TxIdFromSnapshotRef(string snapshotRef, string planRef)
    {
        if (snapshotRef.Length == 0) return planRef;
        string normalized = snapshotRef.Replace('\\', '/')
            .TrimEnd('/');
        string? dir = Path.GetDirectoryName(normalized);
        string name = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir);
        return name.Length > 0 ? name : planRef;
    }
}
