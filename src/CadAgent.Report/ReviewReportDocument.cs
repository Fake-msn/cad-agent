using System.Globalization;
using CadAgent.Core;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CadAgent.Report;

/// <summary>
/// 审图报告 PDF 版式（QuestPDF）：封面信息 + 统计 + 缩略图 + 按强制力分组的问题明细
/// + 跳过检查 + 裁定记录 + 签认记录 + 页脚页码。缺失数据一律如实标注，不静默隐藏。
/// </summary>
internal sealed class ReviewReportDocument : IDocument
{
    private readonly ReviewReportInput _input;
    private readonly ReportContent _content;
    private readonly string _family;

    public ReviewReportDocument(ReviewReportInput input, ReportContent content, string family)
    {
        _input = input;
        _content = content;
        _family = family;
    }

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"施工图审查报告 — {Path.GetFileName(_input.DrawingPath)}",
        Author = _input.Identity.Operator,
        Creator = "CadAgent",
        Producer = "QuestPDF"
    };

    public DocumentSettings GetSettings() => new();

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(x => x.FontFamily(_family).FontSize(9));
            page.Header().Element(ComposeHeader);
            page.Content().Element(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeHeader(IContainer container) => container.PaddingBottom(6).Row(row =>
    {
        row.RelativeItem().Text("施工图审查报告").FontSize(15).Bold();
        row.ConstantItem(230).AlignRight()
            .Text($"图纸：{Path.GetFileName(_input.DrawingPath)}")
            .FontSize(8).FontColor(Colors.Grey.Darken2);
    });

    private void ComposeContent(IContainer container) => container.Column(col =>
    {
        col.Spacing(8);
        col.Item().Element(ComposeMeta);
        col.Item().Element(ComposeSummary);
        if (_input.ThumbnailPng is { Length: > 0 })
            col.Item().AlignCenter().MaxWidth(420).Image(_input.ThumbnailPng).FitWidth();
        else
            col.Item().Text("（缩略图未生成）").FontColor(Colors.Grey.Medium);
        foreach (ReportIssueGroup group in _content.Groups)
            col.Item().Element(c => ComposeGroup(c, group));
        if (_content.Groups.Count == 0)
            col.Item().Text("未发现问题。").Bold();
        col.Item().Element(ComposeSkips);
        col.Item().Element(ComposeAdjudications);
        col.Item().Element(ComposeSignoffs);
        col.Item().PaddingTop(6)
            .Text("本报告由 CadAgent 自动生成，供人工审查参考；规则解释权归审查人。")
            .FontSize(7.5f).FontColor(Colors.Grey.Darken1);
    });

    private void ComposeMeta(IContainer container) => container.Column(col =>
    {
        col.Spacing(1.5f);
        MetaLine(col, "图纸路径", _input.DrawingPath);
        MetaLine(col, "项目目录", _input.ProjectDir);
        MetaLine(col, "审查人", $"{_input.Identity.Operator}（{_input.Identity.Role}）");
        MetaLine(col, "生成时间",
            _input.IssuedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        MetaLine(col, "图纸 SHA256", OrDash(_input.DrawingHash));
        MetaLine(col, "语义标注", _input.OverlayPresent ? "有" : "无（规则评估可能受限）");
        MetaLine(col, "规则包版本", OrDash(_input.RulePackVersion));
        MetaLine(col, "裁定集指纹", OrDash(_input.AdjudicationHash));
    });

    private static void MetaLine(ColumnDescriptor col, string label, string value) =>
        col.Item().Row(row =>
        {
            row.ConstantItem(72).Text(label).FontColor(Colors.Grey.Darken2);
            row.RelativeItem().Text(value);
        });

    private void ComposeSummary(IContainer container) =>
        container.Background(Colors.Grey.Lighten4).Padding(6).Column(col =>
        {
            col.Spacing(2);
            col.Item().Text(
                    $"问题 {_content.Total} 项（必须 {_content.Mandatory}｜应当 {_content.Recommended}｜可 {_content.Informational}）")
                .Bold();
            col.Item().Text($"规则执行 {_input.RulesRun} 条，跳过 {_input.RulesSkipped} 条");
            col.Item().Text(AuditLine());
        });

    private string AuditLine()
    {
        if (_input.Audit is null) return "审计库：不可用（未提供审计数据）";
        ReportAuditSummary a = _input.Audit;
        string chain = a.ChainOk ? "验链通过" : $"验链失败（{a.ChainError ?? "未知原因"}）";
        return $"审计库：{a.RecordCount} 条记录，{chain}，头 seq={a.HeadSeq} hash={ShortHash(a.HeadHash)}";
    }

    private static string ForceLabel(RuleForce force) => force switch
    {
        RuleForce.Mandatory => "必须（Mandatory）",
        RuleForce.Recommended => "应当（Recommended）",
        _ => "可（Informational）"
    };

    private static string ShortHash(string? hash) =>
        string.IsNullOrEmpty(hash) ? "—" : hash.Length > 16 ? hash[..16] + "…" : hash;

    private static string OrDash(string? value) => string.IsNullOrEmpty(value) ? "—" : value;

    private void ComposeGroup(IContainer container, ReportIssueGroup group) => container.Column(col =>
    {
        col.Spacing(4);
        col.Item().PaddingTop(4)
            .Text($"{ForceLabel(group.Force)} — {group.Issues.Count} 项")
            .FontSize(11).Bold()
            .FontColor(group.Force switch
            {
                RuleForce.Mandatory => Colors.Red.Darken2,
                RuleForce.Recommended => Colors.Orange.Darken2,
                _ => Colors.Grey.Darken2
            });
        foreach (ReviewReportIssue issue in group.Issues)
            col.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(6)
                .Element(c => ComposeIssue(c, issue));
    });

    private static void ComposeIssue(IContainer container, ReviewReportIssue issue) =>
        container.Column(col =>
        {
            col.Spacing(1);
            col.Item().Row(row =>
            {
                row.RelativeItem().Text($"{issue.RuleId} · {issue.Standard} {issue.Clause}").Bold();
                row.ConstantItem(84).AlignRight()
                    .Text($"严重度 {issue.Severity}").FontSize(8).FontColor(Colors.Grey.Darken2);
            });
            col.Item().Text($"问题：{issue.Message}");
            if (issue.Targets.Count > 0)
                col.Item().Text($"目标：{string.Join(", ", issue.Targets)}").FontSize(8);
            if (!string.IsNullOrWhiteSpace(issue.Excerpt))
                col.Item().Text($"原文摘录：{issue.Excerpt}");
            if (!string.IsNullOrWhiteSpace(issue.Suggestion))
                col.Item().Text($"建议：{issue.Suggestion}");
            col.Item().Text($"检测依据：{issue.DetectionBasis}");
            if (!string.IsNullOrWhiteSpace(issue.Verification))
                col.Item().Text($"核对方式：{issue.Verification}").FontSize(8);
        });

    private void ComposeSkips(IContainer container) => container.Column(col =>
    {
        col.Spacing(2);
        col.Item().Text($"跳过检查 — {_input.Skipped.Count} 条").Bold();
        if (_input.Skipped.Count == 0)
        {
            col.Item().Text("无").FontColor(Colors.Grey.Medium);
            return;
        }
        foreach (SkipReason s in _input.Skipped)
            col.Item().Text($"[{s.Category}] {s.RuleId}: {s.Message}").FontSize(8);
    });

    private void ComposeAdjudications(IContainer container) => container.Column(col =>
    {
        col.Spacing(2);
        col.Item().Text($"裁定记录 — {_input.Adjudications.Count} 条").Bold();
        if (_input.Adjudications.Count == 0)
        {
            col.Item().Text("无").FontColor(Colors.Grey.Medium);
            return;
        }
        foreach (Adjudication a in _input.Adjudications)
        {
            string effect = a.Exempt
                ? "豁免"
                : a.ForceOverride is RuleForce f ? $"覆盖为 {f}" : "仅解读";
            col.Item().Text(
                $"{a.RuleId} · {a.Operator} · {a.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
                + $" · {effect} · 理由：{a.Reason}"
                + (string.IsNullOrWhiteSpace(a.Interpretation) ? "" : $" · 解读：{a.Interpretation}"))
                .FontSize(8);
        }
    });

    private void ComposeSignoffs(IContainer container) => container.Column(col =>
    {
        col.Spacing(2);
        col.Item().Text($"签认记录 — {_input.Signoffs.Count} 条").Bold();
        if (_input.Signoffs.Count == 0)
        {
            col.Item().Text("无").FontColor(Colors.Grey.Medium);
            return;
        }
        foreach (ReportSignoff s in _input.Signoffs)
        {
            string tx = s.TxId.Length > 8 ? s.TxId[..8] : s.TxId;
            string verdict = s.Approved ? "通过" : "驳回";
            col.Item().Text(
                $"Tx {tx} · {s.Operator}（{s.Role}） · {verdict}"
                + $" · {s.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
                + (string.IsNullOrWhiteSpace(s.Comment) ? "" : $" · 意见：{s.Comment}"))
                .FontSize(8);
        }
    });

    private void ComposeFooter(IContainer container) => container.Row(row =>
    {
        row.RelativeItem()
            .Text($"CadAgent 审图报告 · {Path.GetFileName(_input.DrawingPath)}")
            .FontSize(7).FontColor(Colors.Grey.Darken1);
        row.ConstantItem(100).AlignRight().DefaultTextStyle(x => x.FontSize(7)).Text(text =>
        {
            text.CurrentPageNumber();
            text.Span(" / ");
            text.TotalPages();
        });
    });
}
