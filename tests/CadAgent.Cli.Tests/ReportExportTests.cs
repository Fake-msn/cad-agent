using System.Text;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CSMath;
using Microsoft.Data.Sqlite;

namespace CadAgent.Cli.Tests;

/// <summary>
/// report export E2E：同一评估管线（规则/overlay/裁定）→ PDF 落盘 + report-exported 审计 +
/// stdout JSON；--out/--png 用法；失败路径（缩略图缺失/损坏 overlay）先于产物且留痕口径。
/// </summary>
public sealed class ReportExportTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"reporte2e-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public ReportExportTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("W-墙体"));
            var wall = doc.Layers.First(l => l.Name == "W-墙体");
            doc.Entities.Add(new Line
            {
                StartPoint = (XYZ)new XY(0, 0),
                EndPoint = (XYZ)new XY(2000, 0),
                Layer = wall
            });
            // 5.0.2 违规：4.2 不在字高系列（recommended issue 来源）
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = (XYZ)new XY(0, 500),
                Value = "设计说明",
                Height = 4.2
            });
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_project, true); } catch { }
        try { File.Delete(_drawing); } catch { }
    }

    private (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private IReadOnlyList<AuditRecord> AuditRecords()
    {
        using var store = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return store.ReadAll();
    }

    [Fact]
    public void Export_DefaultPath_WritesPdfAndAudits()
    {
        var (code, stdout, stderr) = Run("report", "export", "--drawing", _drawing);

        Assert.True(code == 0, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        string path = json.RootElement.GetProperty("path").GetString()!;
        Assert.True(File.Exists(path));
        Assert.Contains("reports", path);
        Assert.EndsWith("-review.pdf", path);
        Assert.True(new FileInfo(path).Length > 1000);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 5));
        Assert.True(json.RootElement.GetProperty("issueCount").GetInt32() >= 1);

        using var store = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var records = store.ReadAll();
        var entry = Assert.Single(records.Where(r => r.Entry.Kind == "report-exported"));
        Assert.Equal("report", entry.Entry.Source);
        Assert.True(store.VerifyChain(out string chainError), chainError);
    }

    [Fact]
    public void Export_ExplicitOutAndThumbnail_UsesGivenPathAndRecordsThumbnail()
    {
        string png = Path.Combine(_project, "thumb.png");
        File.WriteAllBytes(png, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        string outPath = Path.Combine(_project, "custom-dir", "report.pdf");

        var (code, stdout, stderr) = Run("report", "export", "--drawing", _drawing,
            "--out", outPath, "--png", png);

        Assert.True(code == 0, stderr);
        Assert.True(File.Exists(outPath));
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(Path.GetFullPath(outPath), json.RootElement.GetProperty("path").GetString());
        var entry = Assert.Single(AuditRecords().Where(r => r.Entry.Kind == "report-exported"));
        using var pars = JsonDocument.Parse(entry.Entry.ParamsJson);
        Assert.True(pars.RootElement.GetProperty("thumbnail").GetBoolean());
    }

    [Fact]
    public void Export_MissingThumbnail_FailsBeforeAnyAuditOrArtifact()
    {
        var (code, _, stderr) = Run("report", "export", "--drawing", _drawing,
            "--png", Path.Combine(_project, "missing.png"));

        Assert.Equal(1, code);
        Assert.Contains("缩略图文件不存在", stderr);
        Assert.False(Directory.Exists(Path.Combine(_project, "reports")));
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
    }

    [Fact]
    public void Export_UnknownSubcommand_ReportsUsage()
    {
        var (code, _, stderr) = Run("report", "bogus");

        Assert.Equal(1, code);
        Assert.Contains("未知 report 子命令", stderr);
    }

    [Fact]
    public void Export_CorruptOverlay_IsAuditedRejectionWithoutArtifact()
    {
        string agentDir = Path.Combine(_project, ".cadagent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "semantic.json"), "{ broken");

        var (code, _, _) = Run("report", "export", "--drawing", _drawing);

        Assert.Equal(1, code);
        Assert.False(Directory.Exists(Path.Combine(_project, "reports")));
        var rejected = Assert.Single(AuditRecords().Where(r => r.Entry.Kind == "intake-rejected"));
        Assert.Equal("report", rejected.Entry.Source);
    }

    // ===== 合并前 ISTQB 加固 B：只读不变量 / 断链上报 / 用法路径 =====

    /// <summary>
    /// 只读不变量：导出前后图纸哈希一致（report 不得改图），且 report-exported 审计
    /// Before/After 哈希（= 报告 DrawingHash 口径）等于该哈希——报告与审计同源可核。
    /// </summary>
    [Fact]
    public void Export_IsReadOnly_PreservesDrawingHashAndAuditsIt()
    {
        string before = DrawingSession.Load(_drawing).ComputeHash();

        var (code, _, stderr) = Run("report", "export", "--drawing", _drawing);

        Assert.True(code == 0, stderr);
        Assert.Equal(before, DrawingSession.Load(_drawing).ComputeHash());
        var entry = Assert.Single(AuditRecords().Where(r => r.Entry.Kind == "report-exported"));
        Assert.Equal(before, entry.Entry.BeforeHash);
        Assert.Equal(before, entry.Entry.AfterHash);
    }

    /// <summary>
    /// 断链上报：先导出一次，再以原始 SQLite 篡改末行 hash（模拟链损坏），二次导出
    /// 仍须成功（报告照出），stdout auditChainOk=false——断链不得被"已生成"掩盖。
    /// </summary>
    [Fact]
    public void Export_AfterTamperedChain_ReportsAuditChainOkFalse_StillExports()
    {
        var (first, _, firstErr) = Run("report", "export", "--drawing", _drawing);
        Assert.True(first == 0, firstErr);

        string dbPath = Path.Combine(_project, ".cadagent", "audit.db");
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audit SET hash='BROKEN' WHERE seq=(SELECT MAX(seq) FROM audit)";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();

        var (code, stdout, stderr) = Run("report", "export", "--drawing", _drawing);

        Assert.True(code == 0, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(json.RootElement.GetProperty("auditChainOk").GetBoolean());
        Assert.True(File.Exists(json.RootElement.GetProperty("path").GetString()));
    }

    /// <summary>用法路径：report 无子命令 → exit 1 + stderr 用法指引（不产报告）。</summary>
    [Fact]
    public void Report_WithoutSubcommand_ReportsUsage()
    {
        var (code, _, stderr) = Run("report");

        Assert.Equal(1, code);
        Assert.Contains("report 需要子命令", stderr);
        Assert.False(Directory.Exists(Path.Combine(_project, "reports")));
    }
}
