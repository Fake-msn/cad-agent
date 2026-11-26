using CadAgent.Core;
using CadAgent.Report;

namespace CadAgent.Report.Tests;

/// <summary>
/// 报告导出审计收尾（无审计不留产物）共用 helper：审计失败→报告删除 + ReviewReportException
/// （消息含"已撤销报告"，CLI stderr/View 状态栏同源）；成功→审计行追加、报告保留。
/// 两个入口（CLI report export / View ExportReportAsync）此前各写一份且无测试，此套件钉住语义。
/// </summary>
[Collection(ReportStaticStateCollection.Name)]
public sealed class ReportAuditCommitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "cadagent-audit-commit-" + Guid.NewGuid().ToString("N"));

    public ReportAuditCommitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string CreateReport()
    {
        string path = Path.Combine(_dir, "report.pdf");
        File.WriteAllBytes(path, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }); // "%PDF-"
        return path;
    }

    private static AuditEntry Entry() => new(DateTimeOffset.UtcNow, "tester", "Draw", "s1",
        "report", "report-exported", @"C:\drawing.dxf", "{}", "H1", "H1", "", "");

    /// <summary>审计追加抛异常 → 已生成报告须撤销（无审计不留产物）+ 统一异常文案。</summary>
    [Fact]
    public void Commit_AppendThrows_DeletesReportAndThrowsWithUndoMarker()
    {
        string report = CreateReport();

        ReviewReportException ex = Assert.Throws<ReviewReportException>(
            () => ReportAuditCommit.Commit(new ThrowingStore("db locked"), Entry(), report));

        Assert.Contains("已撤销报告", ex.Message);
        Assert.Contains("db locked", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.False(File.Exists(report));
    }

    /// <summary>审计追加成功 → 行追加、报告保留（不得误删产物）。</summary>
    [Fact]
    public void Commit_AppendSucceeds_KeepsReportAndAppendsEntry()
    {
        string report = CreateReport();
        var store = new RecordingStore();

        ReportAuditCommit.Commit(store, Entry(), report);

        Assert.Single(store.Appended);
        Assert.Equal("report-exported", store.Appended[0].Kind);
        Assert.True(File.Exists(report));
    }

    /// <summary>报告已被外部删除时审计失败仍抛（清理幂等，不得因 File.Exists 假而吞错）。</summary>
    [Fact]
    public void Commit_AppendThrows_ReportAlreadyGone_StillThrows()
    {
        string report = Path.Combine(_dir, "never-written.pdf");

        ReviewReportException ex = Assert.Throws<ReviewReportException>(
            () => ReportAuditCommit.Commit(new ThrowingStore("boom"), Entry(), report));

        Assert.Contains("已撤销报告", ex.Message);
        Assert.False(File.Exists(report));
    }

    private sealed class ThrowingStore(string message) : IAuditStore
    {
        public AuditRecord Append(AuditEntry e) => throw new InvalidOperationException(message);
        public IReadOnlyList<AuditRecord> ReadAll() => Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error) { error = ""; return true; }
    }

    private sealed class RecordingStore : IAuditStore
    {
        public List<AuditEntry> Appended { get; } = new();
        public AuditRecord Append(AuditEntry e)
        {
            Appended.Add(e);
            return new AuditRecord(Appended.Count, e, "", "H");
        }
        public IReadOnlyList<AuditRecord> ReadAll() => Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error) { error = ""; return true; }
    }
}
