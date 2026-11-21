using System.Text.Json;
using CadAgent.Core;
using CadAgent.View.Services;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 审查签认视图模型（计划4 T7）：纯 C#，无 Avalonia 依赖，可单测。
/// 门禁：RoleChecker.Ensure(op, Review)——Draw 角色抛 RoleViolationException；
/// **自审禁止**：tx 责任人（T6 TxGroup.Operator，即 tx-start 审计记录的操作者）
/// 与当前账号相同（大小写不敏感）→ 抛 RoleViolationException("绘制者不能自审")。
/// 成功路径：先审计 review-signed（Source="review"，paramsJson={txId,approved,
/// comment}，operator/role 盖章，Target=txId，PlanRef="" 不并入事务分组）再
/// Upsert+Save 签认——审计留痕先于签认落盘，审计失败绝不产生无痕迹的签认。
/// 签认门禁加性收紧（最终审查修复②）：仅已提交事务可签认（TxGroup.HasCommit，
/// failed/open → 拒绝"只能签认已提交事务"，零副作用）。
/// 驳回回滚（spec §4.1⑥/§5.4：驳回走快照回滚，回滚也留痕）：approved=false 且
/// tx-start 携带非空 SnapshotRef 时，签认落盘后 await 可选 RollbackTx 回调
/// （MainWindow 接 EditViewModel.UndoAsync → StepEngine.Rollback，无旁路）；
/// 回调失败 → ErrorMessage 提示，但签认记录与审计已先行落盘、不回撤（审计先行）。
/// 签认 IO 体量极小（单 json + 单条审计），同步完成；驳回回滚为异步等待。
/// </summary>
public sealed class SignoffViewModel
{
    /// <summary>审计 kind 常量（签认留痕）。</summary>
    public const string AuditKind = "review-signed";

    private readonly IAuditStore _audit;
    private readonly ReviewSignoffStore _store;
    private readonly IIdentityProvider _identity;
    private readonly RoleChecker _roles;
    private readonly IReadOnlyList<TxGroup> _txGroups;
    private readonly string _sessionId;

    public SignoffViewModel(IAuditStore auditStore, ReviewSignoffStore signoffStore,
        IIdentityProvider identity, RoleChecker roleChecker, IReadOnlyList<TxGroup> txGroups,
        string? sessionId = null)
    {
        _audit = auditStore;
        _store = signoffStore;
        _identity = identity;
        _roles = roleChecker;
        _txGroups = txGroups;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// 驳回回滚回调（可选；MainWindow 装配 → EditViewModel.UndoAsync）。
    /// 仅 approved=false 且事务有快照时在签认落盘后调用；null=驳回仅记录。
    /// </summary>
    public Func<string, Task>? RollbackTx { get; set; }

    /// <summary>最近一次驳回回滚失败原因；null=无失败（签认记录不受影响）。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>当前身份是否可签认（Review 及以上）。</summary>
    public bool CanSign => _identity.Current.Role >= Role.Review;

    /// <summary>全部签认（含历史覆盖前的最新态；同 TxId+Operator 仅一条）。</summary>
    public IReadOnlyList<ReviewSignoff> Signoffs => _store.Items;

    /// <summary>尚无任何签认的事务分组。</summary>
    public IReadOnlyList<TxGroup> UnsignedTx =>
        _txGroups.Where(g => !_store.Items.Any(s => s.TxId == g.TxId)).ToList();

    /// <summary>
    /// 该事务时间最新的一条签认（任意签认人，Ts 最大者；Ts 相同取列表靠后一条——
    /// 升序稳定排序保持同 Ts 的原始顺序，LastOrDefault 取末条）；null=未签认。
    /// 按时间而非列表位置——Upsert 原位替换不改变位置，多签认人重签后仍返回
    /// 时间最新的裁决。
    /// </summary>
    public ReviewSignoff? SignoffFor(string txId) =>
        _store.Items.Where(s => s.TxId == txId)
            .OrderBy(s => s.Ts).LastOrDefault();

    /// <summary>签认完成 → UI 刷新。在调用方上下文触发。</summary>
    public event Action? SignoffsChanged;

    /// <summary>
    /// 签认（通过/驳回 + 意见）。角色不足 / 未知事务 / 非已提交事务 / 自审 → 抛出
    /// （UI 捕获展示），不留签认、不留审计。成功 → 审计 review-signed + 持久化 +
    /// SignoffsChanged；驳回且有快照且装配了 RollbackTx → 随后 await 回滚
    /// （失败仅记 ErrorMessage，签认记录不回撤——审计先行）。
    /// </summary>
    public async Task SignAsync(string txId, bool approved, string comment)
    {
        OperatorIdentity op = _identity.Current;
        _roles.Ensure(op, Role.Review);
        TxGroup group = _txGroups.FirstOrDefault(g => g.TxId == txId)
            ?? throw new InvalidOperationException($"未知事务: {txId}");
        // 仅已提交事务可签认（最终审查修复②）：failed/open 事务无完整执行留痕，拒绝。
        if (!group.HasCommit)
            throw new InvalidOperationException("只能签认已提交事务");
        if (string.Equals(group.Operator, op.Account, StringComparison.OrdinalIgnoreCase))
            throw new RoleViolationException("绘制者不能自审");

        var signoff = new ReviewSignoff(txId, op.Account, op.Role, approved, comment, DateTimeOffset.UtcNow);
        // 审计先行：存储故障（如视图只读占位存储）时签认不得落盘。
        _audit.Append(new AuditEntry(signoff.Ts, op.Account, op.Role.ToString(), _sessionId,
            "review", AuditKind, txId,
            JsonSerializer.Serialize(new { txId, approved, comment }),
            "", "", "", ""));
        _store.Upsert(signoff);
        _store.Save();
        SignoffsChanged?.Invoke();

        // 驳回 → 快照回滚（spec §4.1⑥/§5.4）。前提：tx-start 有快照 + 回调已装配。
        // 回滚失败不影响已落盘的签认与审计（审计先行），仅以 ErrorMessage 提示。
        if (!approved && group.HasSnapshot && RollbackTx is Func<string, Task> rollback)
        {
            try
            {
                await rollback(txId);
                ErrorMessage = null;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"驳回已记录，但快照回滚失败: {ex.Message}";
            }
        }
        else
        {
            ErrorMessage = null;
        }
    }
}
