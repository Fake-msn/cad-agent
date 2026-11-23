using System.ComponentModel;
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Rules;
using CadAgent.View.Services;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 问题条目（计划4 T5）：纯 C#，无 Avalonia 依赖。逐字段映射 Core Issue
/// （出处引用 + 生效强制力 + 严重度 + 目标句柄 + 问题/建议 + 检测依据）。
/// <see cref="ForceBrush"/> 为效力徽章画刷字符串 "#RRGGBB"（Mandatory 红 /
/// Recommended 黄 / Informational 灰），UI 层用 Color.Parse 转 SolidColorBrush
/// （沿用 LayerEntry.ColorBrush 先例，VM 保持 Avalonia-free）。
/// §4.1④ 勾选→意图：新增 <see cref="IsChecked"/> INPC 属性（核心字段 get-only
/// 保持、构造签名不变）——UI 行复选框 TwoWay 绑定；忽略=未勾选正常决策（spec §6.2）。
/// </summary>
public sealed class IssueEntry : INotifyPropertyChanged
{
    private bool _isChecked;

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

    /// <summary>§4.1④ 勾选态（INPC）：勾选→填充意图优先取勾选项；默认 false。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return; // 同值幂等，不重复通知
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>勾选态变更通知（UI TwoWay 绑定 + VM 勾选 key 集同步）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 问题清单视图模型（计划4 T5）：纯 C#，可单测。in-process 规范检查，
/// 与 CLI check 同一语义路径：BuiltinRules.Load + SkillLoader.LoadDirectory +
/// SkillSet.Compose（规则 Id 冲突 → <see cref="ErrorMessage"/>）+
/// AdjudicationSet.Load(.cadagent/adjudications.json) +
/// SemanticOverlay.Load(.cadagent/semantic.json，存在时) → RuleEngine.Evaluate
/// （checkers 全注册，镜像 CLI StandardsEngine，含重复 CheckType 装配守卫）。
/// 只读不改图纸；加载/评估异常一律降级为 ErrorMessage（不崩 UI）。
/// 视图闭环 T6：注入 audit+identity 时成功评估留 check-run 审计（parity CLI check，
/// Source="view-check"，前后哈希一致）；审计写入失败=检查失败（spec §9 无审计旁路）。
/// 合并前 ISTQB I2：评估失败（裁定/overlay 损坏等）也留 check-run 审计（Error 字段，
/// 镜像 EditViewModel.RunRegressionCheck 失败路径口径）——观察性检查故障也入审计（spec §9）。
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
    private readonly IAuditStore? _audit;
    private readonly IIdentityProvider? _identity;

    /// <summary>in-flight 硬闸（视图闭环 T6）：并发刷新第二次立即返回——结构性守卫，不依赖按钮 IsEnabled UI 态。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>§4.1④ 勾选 key 集（会话级，不持久化）：key=RuleId+"|"+Targets 逗号连接+"|"+Message。
    /// Refresh 重建 IssueEntry 时按 key 恢复 IsChecked（跨刷新保持）；勾选变更经
    /// PropertyChanged 订阅同步增删（取消勾选即移除——忽略是正常决策，不留痕）。</summary>
    private readonly HashSet<string> _checkedKeys = new();

    public IssuesViewModel(DrawingSession session, string projectDir,
        IReadOnlyList<string> skillDirs, CanvasViewModel? canvas = null, SemaphoreSlim? sessionGate = null,
        IAuditStore? audit = null, IIdentityProvider? identity = null)
    {
        _session = session;
        _projectDir = projectDir;
        _skillDirs = skillDirs;
        _canvas = canvas;
        _sessionGate = sessionGate;
        _audit = audit;
        _identity = identity;
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
    /// in-flight 硬闸（视图闭环 T6）：并发刷新第二次 WaitAsync(0) 失败立即返回。
    /// 注入 audit+identity 时成功评估后留 check-run 审计（parity CLI check）；
    /// 审计写入失败=检查失败（spec §9 无审计旁路，镜像 CLI check exit 1 语义）。
    /// </summary>
    public async Task RefreshAsync()
    {
        // in-flight 硬闸（结构性，不依赖按钮 IsEnabled UI 态）：并发刷新第二次立即返回。
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            try
            {
                EvalResult eval = await Task.Run(Evaluate);
                CheckSummary summary = eval.Summary;
                // §4.1④：重建条目按 key 恢复勾选态（跨刷新保持），再订阅 PropertyChanged
                // 同步 key 集（恢复赋值先于订阅——恢复不触发冗余同步）。
                Issues = summary.Issues.Select(i =>
                {
                    var entry = new IssueEntry(i);
                    entry.IsChecked = _checkedKeys.Contains(CheckKey(entry));
                    entry.PropertyChanged += OnEntryCheckedChanged;
                    return entry;
                }).ToList();
                MandatoryCount = Issues.Count(i => i.Force == RuleForce.Mandatory);
                RulesSkipped = summary.RulesSkipped;
                SkippedReasons = summary.StructuredSkips ?? Array.Empty<SkipReason>();
                ErrorMessage = null;

                if (_audit is not null && _identity is not null)
                {
                    try
                    {
                        // 只读检查前后哈希一致（镜像 CLI check）；哈希取自 eval.Hash——
                        // 已在 Evaluate 的 sessionGate 临界区内计算（修复轮1），此处不再释闸后重读 session。
                        string hash = eval.Hash;
                        var op = _identity.Current;
                        // sessionId 用 "view" 常量——IssuesVM 无 CLI session 注入（口径注记）。
                        // paramsJson 字段逐字镜像 CLI check（skippedReasons 字段名同
                        // CommandRouter.StructuredSkipAuditArray：[{category, ruleId, message}]）。
                        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                            "view", "view-check", "check-run", _session.SourcePath,
                            JsonSerializer.Serialize(new
                            {
                                rulesRun = eval.Summary.RulesRun,
                                rulesSkipped = eval.Summary.RulesSkipped,
                                issueCount = eval.Summary.Issues.Count,
                                mandatoryViolations = MandatoryCount,
                                skippedReasons = eval.Summary.StructuredSkips?.Select(s => new { category = s.Category, ruleId = s.RuleId, message = s.Message }).ToArray() ?? Array.Empty<object>(),
                                overlayPresent = eval.OverlayPresent,
                                adjudicationHash = eval.AdjudicationHash,
                                rulePackVersion = eval.RulePackVersion
                            }),
                            hash, hash, "", ""));
                    }
                    catch (Exception ex)
                    {
                        // 审计先行（spec §9）：检查审计写入失败=检查失败（镜像 CLI check exit 1 语义），
                        // 清空列表+ErrorMessage；不崩 UI。
                        Issues = Array.Empty<IssueEntry>();
                        MandatoryCount = 0;
                        ErrorMessage = $"检查审计写入失败: {ex.Message}";
                    }
                }
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
        finally { _refreshGate.Release(); }
    }

    /// <summary>点击问题 → 画布高亮 Targets 句柄并居中（保持缩放）。未接 canvas → 无操作。</summary>
    public void LocateIssue(IssueEntry issue)
    {
        if (_canvas is null) return;
        _canvas.SelectHandles(issue.Targets);
        _canvas.CenterOn(issue.Targets);
    }

    /// <summary>§4.1④ 勾选 key：RuleId+"|"+Targets 逗号连接+"|"+Message（同规则不同目标=不同问题）。
    /// I1（合并前 ISTQB fixwave2）：Message 为区分因子——标高规则 mode 省略时同一文本
    /// 产两条 issue（小数位 vs 符号），同 RuleId 同 Targets 异 Message，旧 key 碰撞导致
    /// 勾其一刷新后两条全勾（意图混入未采纳 + adopted 重复/count 失真）/取消其一两条全丢。
    /// 同 rule+targets+message 的真重复视为同一条（去重语义，可接受——内容完全相同的问题
    /// 勾选态本就不应分裂）。</summary>
    private static string CheckKey(IssueEntry entry) =>
        entry.RuleId + "|" + string.Join(",", entry.Targets) + "|" + entry.Message;

    /// <summary>勾选变更 → key 集同步（勾选加入/取消移除；跨刷新保持的权威存储）。</summary>
    private void OnEntryCheckedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IssueEntry.IsChecked) || sender is not IssueEntry entry) return;
        string key = CheckKey(entry);
        if (entry.IsChecked) _checkedKeys.Add(key);
        else _checkedKeys.Remove(key);
    }

    /// <summary>评估结果 + 复现证据（视图闭环 T6）：审计 paramsJson 所需的
    /// rulePackVersion/adjudicationHash/overlayPresent 随汇总一并带回。
    /// Hash（修复轮1）：在 Evaluate 的 sessionGate 临界区内取哈希带回——审计哈希
    /// 必须等于被评估的图纸状态，不得在释闸后重读（刷新与编辑可并发）。</summary>
    private sealed record EvalResult(CheckSummary Summary, string RulePackVersion, string AdjudicationHash, bool OverlayPresent, string Hash);

    private EvalResult Evaluate()
    {
        // 合并前 ISTQB I2（spec §9 观察性检查故障也入审计）：失败路径镜像
        // EditViewModel.RunRegressionCheck——check-run + params{Error,overlayPresent}
        // 字段逐字一致，前后哈希空串（故障时口径）；嵌套守卫：审计再失败不重抛。
        // 留痕后 rethrow → RefreshAsync 外层 catch 降级 ErrorMessage（行为保持）。
        bool overlayPresent = false;
        try
        {
            // 与 CLI LoadRuleSet 同一路径：包加载 + builtin 合成（Id 冲突抛 InvalidSkillException）。
            var packages = _skillDirs.Select(SkillLoader.LoadDirectory).ToList();
            SkillSet ruleSet = SkillSet.Compose(BuiltinRules.Load(), packages);

            string agentDir = Path.Combine(_projectDir, ".cadagent");
            var adjudications = AdjudicationSet.Load(Path.Combine(agentDir, "adjudications.json"));
            string overlayPath = Path.Combine(agentDir, "semantic.json");
            var overlay = File.Exists(overlayPath) ? SemanticOverlay.Load(overlayPath) : new SemanticOverlay();
            overlayPresent = overlay.Annotations.Count > 0;

            // 会话闸（T9，T5/T6 承接）：评估读 session 与 EditViewModel 编辑/撤销互斥。
            _sessionGate?.Wait();
            try
            {
                CheckSummary summary = ViewRuleEngine.Create().Evaluate(ruleSet.AllRules, _session, overlay, adjudications);
                // 哈希与 Evaluate 同闸段（修复轮1）：_refreshGate 只挡刷新自身，刷新与编辑可并发——
                // 若在释闸后取哈希，审计哈希会反映评估后被并发编辑改动的状态（复现证据失真），
                // 并发枚举还可能抛集合修改异常。临界区内取哈希保证 hash == 被评估的图纸状态。
                string hash = _session.ComputeHash();
                return new EvalResult(summary, ruleSet.RulePackVersion, adjudications.Fingerprint(), overlayPresent, hash);
            }
            finally { _sessionGate?.Release(); }
        }
        catch (Exception ex)
        {
            if (_audit is not null && _identity is not null)
            {
                try
                {
                    var op = _identity.Current;
                    // sessionId "view" 常量口径同成功路径；Source/Kind/target 与成功路径一致。
                    _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                        "view", "view-check", "check-run", _session.SourcePath,
                        JsonSerializer.Serialize(new { Error = ex.Message, overlayPresent }),
                        "", "", "", ""));
                }
                catch { /* 嵌套守卫：审计再失败不重抛（镜像 RunRegressionCheck/CLI 最终审查裁定） */ }
            }
            throw; // ErrorMessage 降级仍由 RefreshAsync 外层 catch 统一处理（不崩 UI）
        }
    }
}
