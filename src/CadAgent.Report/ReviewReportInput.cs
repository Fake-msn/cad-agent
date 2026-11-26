using CadAgent.Core;

namespace CadAgent.Report;

/// <summary>报告落款身份（操作者账号 + 角色名）。</summary>
public sealed record ReportIdentity(string Operator, string Role);

/// <summary>签认记录的报告投影（Role 为枚举名，避免 Report 项目反依赖 View）。</summary>
public sealed record ReportSignoff(
    string TxId, string Operator, string Role, bool Approved, string Comment, DateTimeOffset Ts);

/// <summary>审计链摘要（生成报告时点的快照；不含条目正文，避免把整条链塞进 PDF）。</summary>
public sealed record ReportAuditSummary(
    int RecordCount, bool ChainOk, string? ChainError, long HeadSeq, string? HeadHash);

/// <summary>
/// 一次审图报告的数据输入：问题清单（裁定后生效强制力）+ 跳过 + 裁定 + 签认 + 审计验链
/// + 可选缩略图。所有集合空许可（报告如实标注"无记录"），Audit=null 表示审计库不可用。
/// </summary>
public sealed record ReviewReportInput
{
    public required string DrawingPath { get; init; }
    public required string ProjectDir { get; init; }
    public required ReportIdentity Identity { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>被审查图纸内容哈希（SHA256 hex；空串=不可得，报告显示"—"）。</summary>
    public string DrawingHash { get; init; } = "";
    public string? RulePackVersion { get; init; }
    public string? AdjudicationHash { get; init; }
    public bool OverlayPresent { get; init; }

    public required IReadOnlyList<Issue> Issues { get; init; }
    public int RulesRun { get; init; }
    public int RulesSkipped { get; init; }

    public IReadOnlyList<SkipReason> Skipped { get; init; } = Array.Empty<SkipReason>();
    public IReadOnlyList<Adjudication> Adjudications { get; init; } = Array.Empty<Adjudication>();
    public IReadOnlyList<ReportSignoff> Signoffs { get; init; } = Array.Empty<ReportSignoff>();
    public ReportAuditSummary? Audit { get; init; }

    /// <summary>缩略图 PNG（调用方渲染）；null=不嵌入。</summary>
    public byte[]? ThumbnailPng { get; init; }

    /// <summary>规则 Id → 出处核验方式（source-text / external-corroborated / pending-original）。</summary>
    public IReadOnlyDictionary<string, string> RuleVerifications { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>单个问题的报告投影（含可选的规则出处核验方式）。</summary>
public sealed record ReviewReportIssue(
    string RuleId,
    string Standard,
    string Clause,
    string Excerpt,
    RuleForce Force,
    RuleSeverity Severity,
    IReadOnlyList<string> Targets,
    string Message,
    string Suggestion,
    string DetectionBasis,
    string? Verification);

/// <summary>按生效强制力分组的问题段（空组不出现）。</summary>
public sealed record ReportIssueGroup(RuleForce Force, IReadOnlyList<ReviewReportIssue> Issues);

/// <summary>
/// 报告内容组装（纯数据，可单测）：按 Mandatory → Recommended → Informational 分组，
/// 空组省略；计数口径与 CLI mandatoryViolations 一致（裁定后 Force）。
/// </summary>
public sealed class ReportContent
{
    private static readonly RuleForce[] Order =
        { RuleForce.Mandatory, RuleForce.Recommended, RuleForce.Informational };

    private ReportContent(IReadOnlyList<ReportIssueGroup> groups) => Groups = groups;

    public IReadOnlyList<ReportIssueGroup> Groups { get; }
    public int Total => Groups.Sum(g => g.Issues.Count);
    public int Mandatory => CountOf(RuleForce.Mandatory);
    public int Recommended => CountOf(RuleForce.Recommended);
    public int Informational => CountOf(RuleForce.Informational);

    public static ReportContent Build(ReviewReportInput input)
    {
        var groups = new List<ReportIssueGroup>();
        foreach (RuleForce force in Order)
        {
            var items = input.Issues
                .Where(i => i.Force == force)
                .Select(i => new ReviewReportIssue(
                    i.RuleId, i.Standard, i.Clause, i.Excerpt, i.Force, i.Severity, i.Targets,
                    i.Message, i.Suggestion, i.DetectionBasis,
                    input.RuleVerifications.TryGetValue(i.RuleId, out string? v) ? v : null))
                .ToList();
            if (items.Count > 0) groups.Add(new ReportIssueGroup(force, items));
        }
        return new ReportContent(groups);
    }

    private int CountOf(RuleForce force) =>
        Groups.Where(g => g.Force == force).Sum(g => g.Issues.Count);
}
