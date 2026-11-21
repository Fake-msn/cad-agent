using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data.Rules;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 规则裁定视图模型（计划4 T8）：纯 C#，无 Avalonia 依赖，可单测。
/// 与 CLI rule adjudicate 同一校验/审计/持久化路径：
/// 校验顺序契约（逐字镜像 CLI）：互斥（force+exempt）→ force/exempt 必填 →
/// reason 必填 → force 合法性 → 孤儿 ruleId（新鲜 SkillSet.Compose，builtin+skill）
/// ——全部先于审计/Load/Upsert/Save，拒绝抛 InvalidOperationException，不留审计、不落盘。
/// 成功：AdjudicationSet.Load（损坏→intake-rejected 留痕，Source="rule"）→
/// Upsert（同 RuleId 最新裁定生效）→ 审计 rule-adjudicated（Source="rule"，
/// params{ruleId,forceOverride,exempt,reason}，Target=ruleId，哈希不可得→空串）→
/// 原子 Save——审计先行（CLI 评审裁定：Save 失败不回滚审计，记录保留裁定意图）。
/// 裁定 IO 体量极小（单 json + 单条审计），同步完成返回 Task.CompletedTask
/// （沿用 SignoffViewModel 先例，headless 点击后状态即时一致）。
/// AdjudicationsChanged → UI 刷新 + MainWindow 联动 IssuesViewModel.RefreshAsync。
/// </summary>
public sealed class AdjudicationViewModel
{
    private readonly string _projectDir;
    private readonly IReadOnlyList<string> _skillDirs;
    private readonly IAuditStore _audit;
    private readonly IIdentityProvider _identity;
    private readonly string _sessionId;

    public AdjudicationViewModel(string projectDir, IReadOnlyList<string> skillDirs,
        IAuditStore auditStore, IIdentityProvider identity, string? sessionId = null)
    {
        _projectDir = projectDir;
        _skillDirs = skillDirs;
        _audit = auditStore;
        _identity = identity;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        LoadState();
    }

    /// <summary>规则列表（builtin + skill 包，SkillSet.Compose；加载失败时为空）。</summary>
    public IReadOnlyList<Rule> Rules { get; private set; } = Array.Empty<Rule>();

    /// <summary>当前裁定集（同 RuleId 最新裁定生效；加载失败时为空）。</summary>
    public IReadOnlyList<Adjudication> Adjudications { get; private set; } = Array.Empty<Adjudication>();

    /// <summary>加载失败原因（skill 冲突/裁定文件损坏等）；null=成功。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>裁定成功 → UI 刷新 + 问题清单联动刷新。在调用方上下文触发。</summary>
    public event Action? AdjudicationsChanged;

    private string AdjudicationsPath => Path.Combine(_projectDir, ".cadagent", "adjudications.json");

    /// <summary>
    /// 人工裁定（强制力覆盖／豁免，二选一互斥；reason 必填）。校验拒绝 → 抛出
    /// （UI 捕获行内展示），不留审计、不落盘；成功 → 审计 + 持久化 + 事件联动。
    /// </summary>
    public Task AdjudicateAsync(string ruleId, RuleForce? force, bool exempt, string reason,
        string interpretation = "")
    {
        // 校验顺序契约（镜像 CLI RuleAdjudicate）：互斥→force/exempt 必填→reason 必填→
        // force 合法性→孤儿——先于审计/Load/Upsert/Save，未知 Id 不留审计、不落盘。
        if (force is not null && exempt)
            throw new InvalidOperationException("--force 与 --exempt 互斥，不能同时给定");
        if (force is null && !exempt)
            throw new InvalidOperationException("rule adjudicate 需要 --force <mandatory|recommended|informational> 或 --exempt");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("rule adjudicate 需要 --reason（裁定理由，审计留痕）");
        if (force is RuleForce f && !Enum.IsDefined(f))
            throw new InvalidOperationException($"非法强制力值: {force}（可用: mandatory|recommended|informational）");
        // 孤儿裁定校验：每次新鲜合成规则集（与 CLI 每次调用 LoadRuleSet 同口径）。
        SkillSet ruleSet = ComposeRuleSet();
        if (!ruleSet.ContainsRule(ruleId))
            throw new InvalidOperationException($"未知规则 Id: {ruleId}（不在 builtin+skill 规则集中，拒绝孤儿裁定）");

        OperatorIdentity op = _identity.Current;
        AdjudicationSet set = LoadOrReject(() => AdjudicationSet.Load(AdjudicationsPath), AdjudicationsPath);
        set.Upsert(new Adjudication(ruleId, op.Account, DateTimeOffset.UtcNow, reason,
            force, exempt, interpretation));
        // 审计先行（CLI 评审裁定）：Append 先于 Save——Save 失败不回滚审计，记录保留意图。
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            _sessionId, "rule", AdjudicationSet.AuditKind, ruleId,
            JsonSerializer.Serialize(new { ruleId, forceOverride = force?.ToString(), exempt, reason }),
            "", "", "", ""));
        set.Save(AdjudicationsPath);
        Rules = ruleSet.AllRules;
        Adjudications = set.Items;
        ErrorMessage = null;
        AdjudicationsChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>构造时加载规则集 + 当前裁定；失败降级 ErrorMessage（不崩 UI）。</summary>
    private void LoadState()
    {
        try
        {
            Rules = ComposeRuleSet().AllRules;
            Adjudications = AdjudicationSet.Load(AdjudicationsPath).Items;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            Rules = Array.Empty<Rule>();
            Adjudications = Array.Empty<Adjudication>();
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>与 CLI LoadRuleSet / IssuesViewModel.Evaluate 同一合成路径。</summary>
    private SkillSet ComposeRuleSet() =>
        SkillSet.Compose(BuiltinRules.Load(), _skillDirs.Select(SkillLoader.LoadDirectory).ToList());

    /// <summary>
    /// 加载守卫——镜像 CLI LoadOrReject（source="rule"）：裁定文件 Load 失败 →
    /// intake-rejected 留痕（target=失败文件，params{Error,File}，哈希不可得→空串）
    /// 后重新抛出（UI 捕获行内展示）。
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
                _sessionId, "rule", "intake-rejected", file,
                JsonSerializer.Serialize(new { Error = ex.Message, File = file }), "", "", "", ""));
            throw;
        }
    }
}
