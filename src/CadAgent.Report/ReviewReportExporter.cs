using QuestPDF.Fluent;

namespace CadAgent.Report;

/// <summary>导出结果：最终路径 + 生成时点的审计验链状态 + 字节数。</summary>
public sealed record ReviewReportResult(string Path, bool AuditChainOk, long ByteCount);

/// <summary>
/// 审图报告导出器（CLI 与 View 共用唯一实现）：内容组装 → QuestPDF 生成 → 同目录临时
/// 文件 + Move 原子落盘（失败无半成品）。字体族经 <see cref="CjkFontRegistry"/> 解析。
/// 不写审计——审计留痕由调用方（CLI/View）在导出成功后统一 Append（无旁路）。
/// </summary>
public static class ReviewReportExporter
{
    public static ReviewReportResult Export(ReviewReportInput input, string outPath)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(outPath))
            throw new ArgumentException("报告输出路径不能为空", nameof(outPath));

        string fullPath = Path.GetFullPath(outPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string family = CjkFontRegistry.EnsureFamily();
        ReportContent content = ReportContent.Build(input);

        byte[] pdf;
        try
        {
            pdf = new ReviewReportDocument(input, content, family).GeneratePdf();
        }
        catch (ReviewReportException) { throw; }
        catch (Exception ex)
        {
            throw new ReviewReportException($"PDF 生成失败: {ex.Message}", ex);
        }

        string tmp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, pdf);
            File.Move(tmp, fullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不掩盖原始异常 */ }
            throw new ReviewReportException($"报告写入失败: {ex.Message}", ex);
        }

        return new ReviewReportResult(fullPath, input.Audit?.ChainOk ?? true, pdf.LongLength);
    }

    /// <summary>默认产物路径：&lt;projectDir&gt;/reports/&lt;图纸名&gt;-&lt;时间戳&gt;-review.pdf。</summary>
    public static string DefaultPath(string projectDir, string drawingPath, DateTimeOffset issuedAt) =>
        Path.Combine(projectDir, "reports",
            $"{Path.GetFileNameWithoutExtension(drawingPath)}-{issuedAt:yyyyMMdd-HHmmss}-review.pdf");
}
