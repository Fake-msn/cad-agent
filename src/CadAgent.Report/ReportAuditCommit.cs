using CadAgent.Core;

namespace CadAgent.Report;

/// <summary>
/// 报告导出审计收尾（CLI report export 与 View ExportReportAsync 共用）：
/// 审计先行（spec §9 无审计旁路）——成功=审计行追加、报告保留；失败=撤销本次
/// 已生成报告（自家产物，不留无审计文件）并抛 <see cref="ReviewReportException"/>
/// （消息含"已撤销报告"，两入口的用户通道——CLI stderr / View 状态栏——同源不漂移）。
/// 撤销清理自身失败不掩盖审计错误（catch 后仍抛原始原因）。
/// </summary>
public static class ReportAuditCommit
{
    public static void Commit(IAuditStore store, AuditEntry entry, string reportPath)
    {
        try
        {
            store.Append(entry);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { /* 清理失败不掩盖审计错误 */ }
            throw new ReviewReportException($"审计写入失败，已撤销报告: {ex.Message}", ex);
        }
    }
}
