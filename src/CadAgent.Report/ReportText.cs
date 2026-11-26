using CadAgent.Core;

namespace CadAgent.Report;

/// <summary>
/// 报告 PDF 的纯文本投影（不依赖 QuestPDF，可单测）：摘要/审计/段标题/空标记等叙述句
/// 集中于此，文档只负责版式——文本口径改动即可被测试钉住（防跑字与计数口径漂移）。
/// </summary>
internal static class ReportText
{
    public static string ProblemLine(ReportContent content) =>
        $"问题 {content.Total} 项（必须 {content.Mandatory}｜应当 {content.Recommended}｜可 {content.Informational}）";

    public static string RulesLine(ReviewReportInput input) =>
        $"规则执行 {input.RulesRun} 条，跳过 {input.RulesSkipped} 条";

    /// <summary>审计摘要行；Audit=null 表示审计库不可用（如实标注，不当成断链）。</summary>
    public static string AuditLine(ReportAuditSummary? audit)
    {
        if (audit is null) return "审计库：不可用（未提供审计数据）";
        string chain = audit.ChainOk ? "验链通过" : $"验链失败（{audit.ChainError ?? "未知原因"}）";
        return $"审计库：{audit.RecordCount} 条记录，{chain}，头 seq={audit.HeadSeq} hash={ShortHash(audit.HeadHash)}";
    }

    /// <summary>段标题，如「跳过检查 — 1 条」。</summary>
    public static string SectionHeader(string title, int count) => $"{title} — {count} 条";

    /// <summary>问题分组标题，如「必须（Mandatory） — 1 项」。</summary>
    public static string GroupHeader(RuleForce force, int count) => $"{ForceLabel(force)} — {count} 项";

    public static string EmptyMarker() => "无";

    public static string ThumbnailMissing() => "（缩略图未生成）";

    public static string NoIssues() => "未发现问题。";

    public static string ForceLabel(RuleForce force) => force switch
    {
        RuleForce.Mandatory => "必须（Mandatory）",
        RuleForce.Recommended => "应当（Recommended）",
        _ => "可（Informational）"
    };

    private static string ShortHash(string? hash) =>
        string.IsNullOrEmpty(hash) ? "—" : hash.Length > 16 ? hash[..16] + "…" : hash;
}
