using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3c T4 CLI export：降级路径（RuntimeFactory→null）+ 厂商路径（fake runtime）+ 闸口拒绝。
/// 真机 ZWCAD 路径留 T7 E2E。
/// </summary>
public class ExportCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"exportcli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public ExportCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private sealed class FakeRuntime : ICadRuntime
    {
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => throw new NotSupportedException();
        public void AddText(double x, double y, string text, double height, string? layer = null) => throw new NotSupportedException();
        public void AddCircle(double x, double y, double radius, string? layer = null) => throw new NotSupportedException();
        public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => throw new NotSupportedException();
        public void EnsureBlock(string blockName, IReadOnlyList<CadAgent.Core.Cad.BlockPrimitive> primitives) => throw new NotSupportedException();
        public int EntityCount() => throw new NotSupportedException();

        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) { }
        public void SaveAs(string p)
        {
            var doc = CadAgent.Data.DrawingSession.Load(_drawingSource!).Doc;
            using var fs = File.Create(p);
            ACadSharp.IO.DwgWriter.Write(fs, doc);
        }
        public void Close(bool save) { }
        public void Restart() { }
        public void Dispose() { }
        public string? _drawingSource;
    }

    [Fact]
    public void Export_NoRuntime_DegradesToDxf_ExplicitRisk()
    {
        CommandRouter.RuntimeFactory = () => null;
        string outDwg = Path.Combine(_project, "out.dwg");
        var (code, stdout, stderr) = Run("export", "--drawing", _drawing, "--out", outDwg);
        Assert.Equal(0, code);
        Assert.Contains("dxf-degraded", stdout);
        Assert.True(File.Exists(Path.Combine(_project, "out.dxf")));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-degraded");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Export_VendorFake_RoundTripOk_Committed()
    {
        var rt = new FakeRuntime { _drawingSource = _drawing };
        CommandRouter.RuntimeFactory = () => rt;
        string outDwg = Path.Combine(_project, "out.dwg");
        var (code, stdout, _) = Run("export", "--drawing", _drawing, "--out", outDwg);
        Assert.Equal(0, code);
        Assert.Contains("vendor", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-committed");
    }
}