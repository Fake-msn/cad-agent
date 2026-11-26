using System.Text;
using CadAgent.Core;
using CadAgent.Report;

namespace CadAgent.Report.Tests;

[Collection(ReportStaticStateCollection.Name)]
public sealed class ReviewReportExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cadagent-report-" + Guid.NewGuid().ToString("N"));

    public ReviewReportExporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试清理失败不影响断言 */ }
    }

    // 1x1 透明 PNG（合法最小缩略图样本）
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static ReviewReportInput MakeInput(byte[]? thumbnail = null) => new()
    {
        DrawingPath = @"C:\项目\一层平面.dwg",
        ProjectDir = @"C:\项目",
        Identity = new ReportIdentity("tester", "Review"),
        IssuedAt = DateTimeOffset.Parse("2026-11-26T10:00:00+08:00"),
        DrawingHash = "ABCDEF0123456789",
        RulePackVersion = "builtin@1",
        AdjudicationHash = "FP123",
        OverlayPresent = true,
        Issues = new[]
        {
            new Issue("R-MAND", "8.1.2", "GB 50001-2017", "原文摘录内容", RuleForce.Mandatory,
                RuleSeverity.High, new[] { "1A0", "1A1" }, "问题描述", "整改建议", "检测依据"),
            new Issue("R-REC", "9.2", "GB 50002-2017", "摘录二", RuleForce.Recommended,
                RuleSeverity.Low, Array.Empty<string>(), "问题二", "建议二", "依据二")
        },
        RulesRun = 5,
        RulesSkipped = 1,
        Skipped = new[] { new SkipReason("Semantic", "R-SKIP", "无匹配检查器") },
        Adjudications = new[]
        {
            new Adjudication("R-MAND", "tester", DateTimeOffset.Parse("2026-11-25T09:00:00+08:00"),
                "现场确认强制", RuleForce.Mandatory)
        },
        Signoffs = new[]
        {
            new ReportSignoff("tx-1234567890", "checker", "Review", true, "同意", 
                DateTimeOffset.Parse("2026-11-25T10:00:00+08:00"))
        },
        Audit = new ReportAuditSummary(12, true, null, 12, "DEADBEEF0123456789"),
        ThumbnailPng = thumbnail,
        RuleVerifications = new Dictionary<string, string> { ["R-MAND"] = "source-text" }
    };

    [Fact]
    public void Export_WithFullInput_WritesPdfWithMagicHeader()
    {
        string path = Path.Combine(_dir, "full.pdf");

        var result = ReviewReportExporter.Export(MakeInput(TinyPng), path);

        Assert.True(File.Exists(path));
        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.True(result.AuditChainOk);
        Assert.True(result.ByteCount > 1000);
        Assert.Equal(result.ByteCount, new FileInfo(path).Length);
        string header = Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 5);
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public void Export_WithEmptyCollections_StillGeneratesPdf()
    {
        var input = new ReviewReportInput
        {
            DrawingPath = @"C:\项目\空图.dwg",
            ProjectDir = @"C:\项目",
            Identity = new ReportIdentity("tester", "Draw"),
            IssuedAt = DateTimeOffset.Now,
            Issues = Array.Empty<Issue>(),
            Audit = null
        };
        string path = Path.Combine(_dir, "empty.pdf");

        var result = ReviewReportExporter.Export(input, path);

        Assert.True(File.Exists(path));
        Assert.True(result.AuditChainOk); // 未提供审计数据 → 不视为断链
    }

    [Fact]
    public void Export_WithBrokenChain_ReportsAuditChainOkFalse()
    {
        ReviewReportInput input = MakeInput() with
        {
            Audit = new ReportAuditSummary(3, false, "broken at seq 2", 3, "H")
        };
        string path = Path.Combine(_dir, "broken.pdf");

        var result = ReviewReportExporter.Export(input, path);

        Assert.False(result.AuditChainOk); // 断链必须原样透出，不能被「已生成」掩盖
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Export_LeavesNoTemporaryFileBehind()
    {
        string path = Path.Combine(_dir, "atomic.pdf");

        ReviewReportExporter.Export(MakeInput(), path);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(_dir, "*.pdf"));
    }

    [Fact]
    public void Export_WhenTargetIsDirectory_ThrowsAndLeavesNoTmp()
    {
        string dirAsTarget = Path.Combine(_dir, "target-dir");
        Directory.CreateDirectory(dirAsTarget);

        var ex = Assert.Throws<ReviewReportException>(
            () => ReviewReportExporter.Export(MakeInput(), dirAsTarget));

        Assert.Contains("写入失败", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Export_EmptyOutputPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ReviewReportExporter.Export(MakeInput(), " "));
    }

    [Fact]
    public void DefaultPath_PlacesUnderProjectReportsWithTimestamp()
    {
        var at = DateTimeOffset.Parse("2026-11-26T10:11:12+08:00");

        string path = ReviewReportExporter.DefaultPath(@"C:\项目", @"C:\dwg\一层平面.dwg", at);

        Assert.Equal(Path.Combine(@"C:\项目", "reports", "一层平面-20261126-101112-review.pdf"), path);
    }
}
