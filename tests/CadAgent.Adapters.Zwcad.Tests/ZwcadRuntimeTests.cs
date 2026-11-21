using ACadSharp.IO;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// 3c T2 真机集成测试（本机 ZWCAD 2025）。无 ZWCAD 环境：dotnet test --filter Category!=RequiresZwcad。
/// </summary>
[Trait("Category", "RequiresZwcad")]
public class ZwcadRuntimeTests : IDisposable
{
    private readonly string _dxf;
    private readonly string _dwg = Path.Combine(Path.GetTempPath(), $"zwcad-{Guid.NewGuid():N}.dwg");
    public ZwcadRuntimeTests() => _dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose()
    {
        File.Delete(_dxf);
        if (File.Exists(_dwg)) File.Delete(_dwg);
    }

    [Fact]
    public void GetInfo_Available_Version()
    {
        using var rt = new ZwcadRuntime();
        var info = rt.GetInfo();
        Assert.True(info.Available);
        Assert.Equal("ZWCAD", info.Vendor);
        Assert.False(string.IsNullOrEmpty(info.Version));
    }

    [Fact]
    public void OpenSaveAs_RoundTrip_EntityCountPreserved()
    {
        using var rt = new ZwcadRuntime();
        rt.Open(_dxf);
        rt.SaveAs(_dwg);
        rt.Close(false);
        Assert.True(File.Exists(_dwg));
        var source = DrawingSession.Load(_dxf);
        using var reader = new DwgReader(_dwg);
        var exported = reader.Read();
        Assert.Equal(source.Summarize().Entities.Count, exported.Entities.Count());
    }
}