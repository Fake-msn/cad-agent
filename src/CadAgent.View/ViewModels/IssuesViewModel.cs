using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Checks;
using CadAgent.Data.Rules;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 问题条目（计划4 T5）：纯 C#，无 Avalonia 依赖。逐字段映射 Core Issue
/// （出处引用 + 生效强制力 + 严重度 + 目标句柄 + 问题/建议 + 检测依据）。
/// <see cref="ForceBrush"/> 为效力徽章画刷字符串 "#RRGGBB"（Mandatory 红 /
/// Recommended 黄 / Informational 灰），UI 层用 Color.Parse 转 SolidColorBrush
/// （沿用 LayerEntry.ColorBrush 先例，VM 保持 Avalonia-free）。
/// </summary>
public sealed class IssueEntry
{
    internal IssueEntry(Issue issue)
    {
        RuleId = issue.RuleId;
        Standard = issue.Standard;
        Clause = issue.Clause;
        Excerpt = issue.Excerpt;
        Force = issue.Force;
        Severity = issue.Severity;
        Message = issue.Message;
        Targets = issue.Targets;
        Suggestion = issue.Suggestion;
        DetectionBasis = issue.DetectionBasis;
    }

    public string RuleId { get; }
    public string Standard { get; }
    public string Clause { get; }
    public string Excerpt { get; }

    /// <summary>生效强制力——引擎按裁定覆写后的值（展示用）。</summary>
    public RuleForce Force { get; }

    public RuleSeverity Severity { get; }
    public string Message { get; }

    /// <summary>目标图元句柄列表（图层命名规则的目标为图层句柄）。</summary>
    public IReadOnlyList<string> Targets { get; }

    public string Suggestion { get; }
    public string DetectionBasis { get; }

    /// <summary>
    /// 效力徽章色（T10 WCAG 修正）：Mandatory 深红 #B71C1C / Recommended 黄 #F9A825 /
    /// Informational 深灰 #616161——深红/深灰底白字、黄底黑字对比度均 ≥ 4.5:1（AA）。
    /// </summary>
    public string ForceBrush => Force switch
    {
        RuleForce.Mandatory => "#B71C1C",
        RuleForce.Recommended => "#F9A825",
        _ => "#616161",
    };
}

/// <summary>
/// 问题清单视图模型（计划4 T5）：纯 C#，可单测。in-process 规范检查，
/// 与 CLI check 同一语义路径：BuiltinRules.Load + SkillLoader.LoadDirectory +
/// SkillSet.Compose（规则 Id 冲突 → <see cref="ErrorMessage"/>）+
/// AdjudicationSet.Load(.cadagent/adjudications.json) +
/// SemanticOverlay.Load(.cadagent/semantic.json，存在时) → RuleEngine.Evaluate
/// （checkers 全注册，镜像 CLI StandardsEngine，含重复 CheckType 装配守卫）。
/// 只读不改图纸；加载/评估异常一律降级为 ErrorMessage（不崩 UI）。
/// RefreshAsync 后台评估、回到调用方上下文发布结果并触发 <see cref="IssuesChanged"/>。
/// 定位：LocateIssue → canvas SelectHandles(Targets) 高亮 + CenterOn(Targets) 居中；
/// 未接 canvas 或句柄无命中图元（如图层句柄）→ 视图不动。
/// </summary>
public sealed class IssuesViewModel
{
    private readonly DrawingSession _session;
    private readonly string _projectDir;
    private readonly IReadOnlyList<string> _skillDirs;
    private readonly CanvasViewModel? _canvas;
    private readonly SemaphoreSlim? _sessionGate;

    public IssuesViewModel(DrawingSession session, string projectDir,
        IReadOnlyList<string> skillDirs, CanvasViewModel? canvas = null, SemaphoreSlim? sessionGate = null)
    {
        _session = session;
        _projectDir = projectDir;
        _skillDirs = skillDirs;
        _canvas = canvas;
        _sessionGate = sessionGate;
    }

    /// <summary>最近一次评估的问题列表（初始为空；失败时清空）。</summary>
    public IReadOnlyList<IssueEntry> Issues { get; private set; } = Array.Empty<IssueEntry>();

    /// <summary>生效强制力为 Mandatory 的问题数（裁定覆写后口径，同 CLI mandatoryViolations）。</summary>
    public int MandatoryCount { get; private set; }

    /// <summary>跳过规则数（豁免/待原件核验/无检查器/检查器异常/无语义标注）。</summary>
    public int RulesSkipped { get; private set; }

    /// <summary>结构化跳过原因（类别+规则 Id+消息）——跳过不静默（spec §6.2/§9）。</summary>
    public IReadOnlyList<SkipReason> SkippedReasons { get; private set; } = Array.Empty<SkipReason>();

    /// <summary>最近一次刷新失败原因（skill 冲突/文件损坏等）；null=成功。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>刷新完成（成功或失败）→ UI 重建列表/计数。在调用方上下文触发。</summary>
    public event Action? IssuesChanged;

    /// <summary>
    /// 重新评估：后台执行加载+评估（不阻塞 UI 线程），结果回到调用方上下文发布。
    /// 任何异常（包非法/Id 冲突/裁定或 overlay 损坏/图纸访问失败）→ ErrorMessage，
    /// 列表清空；下次成功刷新清除错误。
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            CheckSummary summary = await Task.Run(Evaluate);
            Issues = summary.Issues.Select(i => new IssueEntry(i)).ToList();
            MandatoryCount = Issues.Count(i => i.Force == RuleForce.Mandatory);
            RulesSkipped = summary.RulesSkipped;
            SkippedReasons = summary.StructuredSkips ?? Array.Empty<SkipReason>();
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            Issues = Array.Empty<IssueEntry>();
            MandatoryCount = 0;
            RulesSkipped = 0;
            SkippedReasons = Array.Empty<SkipReason>();
            ErrorMessage = ex.Message;
        }
        IssuesChanged?.Invoke();
    }

    /// <summary>点击问题 → 画布高亮 Targets 句柄并居中（保持缩放）。未接 canvas → 无操作。</summary>
    public void LocateIssue(IssueEntry issue)
    {
        if (_canvas is null) return;
        _canvas.SelectHandles(issue.Targets);
        _canvas.CenterOn(issue.Targets);
    }

    private CheckSummary Evaluate()
    {
        // 与 CLI LoadRuleSet 同一路径：包加载 + builtin 合成（Id 冲突抛 InvalidSkillException）。
        var packages = _skillDirs.Select(SkillLoader.LoadDirectory).ToList();
        SkillSet ruleSet = SkillSet.Compose(BuiltinRules.Load(), packages);

        string agentDir = Path.Combine(_projectDir, ".cadagent");
        var adjudications = AdjudicationSet.Load(Path.Combine(agentDir, "adjudications.json"));
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        var overlay = File.Exists(overlayPath) ? SemanticOverlay.Load(overlayPath) : new SemanticOverlay();

        // 会话闸（T9，T5/T6 承接）：评估读 session 与 EditViewModel 编辑/撤销互斥。
        _sessionGate?.Wait();
        try { return CreateEngine().Evaluate(ruleSet.AllRules, _session, overlay, adjudications); }
        finally { _sessionGate?.Release(); }
    }

    /// <summary>
    /// 规范引擎装配——逐字镜像 CLI CommandRouter.StandardsEngine 的注册列表
    /// （builtin 5 类 + 电气/模型 5 类）与重复 CheckType 装配守卫，
    /// 防止 RuleEngine 字典 last-wins 静默丢失检查器。
    /// </summary>
    private static RuleEngine CreateEngine()
    {
        IRuleChecker[] checkers =
        {
            new TextHeightChecker(), new LineweightChecker(), new AxisLetterChecker(),
            new ElevationChecker(), new LayerNameChecker(),
            new TextHeightMinChecker(), new CableFormatChecker(), new SlotCodeChecker(),
            new LuminaireFormatChecker(), new FileNamingChecker()
        };
        var duplicate = checkers.GroupBy(c => c.CheckType, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"重复注册检查器 CheckType: {duplicate.Key}");
        return new RuleEngine(checkers);
    }
}
