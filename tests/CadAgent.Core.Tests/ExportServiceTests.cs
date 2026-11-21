using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

public class ExportServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;
    public ExportServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class FakeRuntime : ICadRuntime
    {
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => throw new NotSupportedException();
        public void AddText(double x, double y, string text, double height, string? layer = null) => throw new NotSupportedException();
        public void AddCircle(double x, double y, double radius, string? layer = null) => throw new NotSupportedException();
        public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => throw new NotSupportedException();
        public void EnsureBlock(string blockName, IReadOnlyList<CadAgent.Core.Cad.BlockPrimitive> primitives) => throw new NotSupportedException();
        public int EntityCount() => throw new NotSupportedException();

        public bool Fail;
        public int Restarts;
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) { if (Fail) throw new CadRuntimeException("COM 崩溃"); }
        public void SaveAs(string p) => File.WriteAllText(p, "dwg");
        public void Close(bool save) { }
        public void Restart() { Restarts++; Fail = false; }
        public void Dispose() { }
    }

    private static readonly OperatorIdentity Op = new("T\\u", Role.Draw);

    [Fact]
    public void VendorMode_RoundTripMatch_Committed()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        string outDwg = Path.Combine(_dir, "o.dwg");
        var svc = new ExportService(new FakeRuntime(), _ => 3);
        var r = svc.Export(src, outDwg, "s1", _audit, Op);
        Assert.True(r.Ok);
        Assert.Equal("vendor", r.Mode);
        Assert.Contains(_audit.ReadAll(), x => x.Entry.Kind == "export-committed");
    }

    [Fact]
    public void VendorMode_CountMismatch_Rejected()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        var svc = new ExportService(new FakeRuntime(), p => p.EndsWith(".dwg") ? 2 : 3);
        var r = svc.Export(src, Path.Combine(_dir, "o.dwg"), "s1", _audit, Op);
        Assert.False(r.Ok);
        Assert.Contains(_audit.ReadAll(), x => x.Entry.Kind == "export-rejected");
    }

    [Fact]
    public void ComCrash_RestartOnce_ThenSuccess()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        var rt = new FakeRuntime { Fail = true };
        var svc = new ExportService(rt, _ => 3);
        var r = svc.Export(src, Path.Combine(_dir, "o.dwg"), "s1", _audit, Op);
        Assert.True(r.Ok);
        Assert.Equal(1, rt.Restarts);
    }

    [Fact]
    public void NoRuntime_DxfDegraded_ExplicitRisk()
    {
        string src = Path.Combine(_dir, "s.dxf"); File.WriteAllText(src, "x");
        var svc = new ExportService(null, _ => 3);
        var r = svc.Export(src, Path.Combine(_dir, "o.dwg"), "s1", _audit, Op);
        Assert.True(r.Ok);
        Assert.Equal("dxf-degraded", r.Mode);
        Assert.True(File.Exists(Path.Combine(_dir, "o.dxf")));
        Assert.Contains(_audit.ReadAll(), x => x.Entry.Kind == "export-degraded" && x.Entry.ParamsJson.Contains("保真度未验证"));
    }
}