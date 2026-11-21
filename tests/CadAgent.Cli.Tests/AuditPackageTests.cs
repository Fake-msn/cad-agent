using CadAgent.Core;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3c T5：审计包（manifest+内容齐全+无密钥）+ verify --anchor（Ok/尾截断检测）。
/// </summary>
public class AuditPackageTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"auditpkg-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public AuditPackageTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private void RunTx()
    {
        string plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(plan, """{"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":3,"y2":3}}]}""");
        Assert.Equal(0, Run("run", plan, "--drawing", _drawing).code);
    }

    [Fact]
    public void Package_ContentsAndManifest_NoSecrets()
    {
        RunTx();
        string outDir = Path.Combine(_project, "pkg");
        var (code, stdout, _) = Run("audit", "package", "--out", outDir);
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(outDir, "audit.db")));
        Assert.True(File.Exists(Path.Combine(outDir, "anchor.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(outDir, "llm-profiles.json")));
        Assert.False(File.Exists(Path.Combine(outDir, "anchor-key.bin")));
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("chainVerified").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("recordCount").GetInt32() > 0);
        Assert.Equal("Ok", manifest.RootElement.GetProperty("anchorStatus").GetString());
    }

    [Fact]
    public void VerifyAnchor_Ok()
    {
        RunTx();
        var (code, stdout, _) = Run("audit", "verify", "--anchor");
        Assert.Equal(0, code);
        Assert.Contains("外部锚校验通过", stdout);
    }

    [Fact]
    public void VerifyAnchor_TailTruncation_Detected()
    {
        RunTx();
        // 尾截断：删除末条记录（链仍完整，但锚序号大于 DB 头）
        string db = Path.Combine(_project, ".cadagent", "audit.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM audit WHERE seq = (SELECT MAX(seq) FROM audit)";
            cmd.ExecuteNonQuery();
        }
        var (code, _, stderr) = Run("audit", "verify", "--anchor");
        Assert.Equal(1, code);
        Assert.Contains("Truncated", stderr);
    }
}