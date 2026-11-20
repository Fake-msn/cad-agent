using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

public class SemanticCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"semcli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public SemanticCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-自定义"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0),
                EndPoint = (CSMath.XYZ)new CSMath.XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "W-自定义")
            });
        });
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

    [Fact]
    public void Scan_WritesOverlay_Audits_ListsAmbiguous()
    {
        var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("W-自定义", stdout);
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Map_ConfirmsLayer_RemovesAmbiguous_Audits()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        var (code, _, _) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(0, code);
        var (_, showOut, _) = Run("semantic", "show");
        Assert.Contains("HumanConfirmed", showOut);
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Map_InvalidClass_ExitOne()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        var (code, _, stderr) = Run("semantic", "map", "--layer", "W-自定义", "--class", "NotAClass", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Map_WithoutScan_ExitOne()
    {
        var (code, _, stderr) = Run("semantic", "map", "--layer", "X", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.Contains("scan", stderr);
    }

    private string OverlayPath => Path.Combine(_project, ".cadagent", "semantic.json");

    private void WriteCorruptOverlay()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".cadagent"));
        File.WriteAllText(OverlayPath, "{ not valid json !!!");
    }

    private static string WriteCorruptDrawing(string project)
    {
        string corrupt = Path.Combine(project, "corrupt.dxf");
        File.WriteAllBytes(corrupt, new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x42, 0x42, 0x42, 0x0D, 0x0A });
        return corrupt;
    }

    private CadAgent.Core.AuditRecord SingleIntakeRejected(string target)
    {
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("semantic", rec.Entry.Source);
        Assert.Equal(target, rec.Entry.Target);
        Assert.Equal("", rec.Entry.BeforeHash);
        Assert.Equal("", rec.Entry.AfterHash);
        Assert.Contains("Error", rec.Entry.ParamsJson);
        Assert.Contains("File", rec.Entry.ParamsJson);
        Assert.True(audit.VerifyChain(out _));
        return rec;
    }

    [Fact]
    public void Scan_CorruptOverlay_AuditedRefusal()
    {
        WriteCorruptOverlay();
        var (code, _, stderr) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        SingleIntakeRejected(OverlayPath);
    }

    [Fact]
    public void Map_CorruptOverlay_AuditedRefusal()
    {
        WriteCorruptOverlay();
        var (code, _, stderr) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        SingleIntakeRejected(OverlayPath);
    }

    [Fact]
    public void Map_CorruptDrawing_AuditedRefusal()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        string corrupt = WriteCorruptDrawing(_project);
        var (code, _, stderr) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", corrupt);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        SingleIntakeRejected(corrupt);
    }
}
