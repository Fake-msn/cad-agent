using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3c T6：vendor list 矩阵 + export gating 未验证厂商警示+审计。
/// </summary>
public class VendorCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"vendorcli-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public VendorCommandTests()
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

    private sealed class UnverifiedRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("AutoCAD", "2025", true);
        public void Open(string p) { }
        public void SaveAs(string p)
        {
            var doc = CadAgent.Data.DrawingSession.Load(_src!).Doc;
            using var fs = File.Create(p);
            ACadSharp.IO.DwgWriter.Write(fs, doc);
        }
        public void Close(bool save) { }
        public void Restart() { }
        public void Dispose() { }
        public string? _src;
    }

    [Fact]
    public void VendorList_ShowsZwcadVerified()
    {
        var (code, stdout, _) = Run("vendor", "list");
        Assert.Equal(0, code);
        Assert.Contains("ZWCAD", stdout);
        Assert.Contains("Verified", stdout);
        Assert.Contains("CompatibleUnverified", stdout);
    }

    [Fact]
    public void Export_UnverifiedVendor_WarnsAndAudits()
    {
        CommandRouter.RuntimeFactory = () => new UnverifiedRuntime { _src = _drawing };
        string outDwg = Path.Combine(_project, "out.dwg");
        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", outDwg);
        Assert.Equal(0, code); // 不阻止，决策权归人
        Assert.Contains("未经真机验证", stderr);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-gating" && r.Entry.ParamsJson.Contains("AutoCAD"));
    }

    [Fact]
    public void Vendor_UnknownSubcommand_ExitOne()
    {
        var (code, _, _) = Run("vendor", "bogus");
        Assert.Equal(1, code);
    }
}