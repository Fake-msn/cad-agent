using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

/// <summary>
/// CUIX 定位测试：临时目录假 profile 树（等价类：ZWCAD 版本/语言目录、
/// AutoCAD 版本/release/语言目录、未安装空返回）。AppData seam 注入。
/// </summary>
public class CuixLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cuixloc-{Guid.NewGuid():N}");
    private readonly Func<string> _originalAppData = CuixLocator.AppData;

    public CuixLocatorTests()
    {
        Directory.CreateDirectory(_root);
        CuixLocator.AppData = () => _root;
    }

    public void Dispose()
    {
        CuixLocator.AppData = _originalAppData;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeFile(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void ZwcadProfile_FindsCuix_AcrossVersionAndLang()
    {
        var expected = MakeFile("ZWSOFT", "ZWCAD", "2025", "zh-CN", "Support", "ZWCAD.CUIX");
        var found = CuixLocator.FindCuix(VendorProfiles.Zwcad);
        Assert.Equal(new[] { expected }, found);
    }

    [Fact]
    public void AcadProfile_FindsCuix_AcrossReleaseAndLang()
    {
        var a = MakeFile("Autodesk", "AutoCAD 2025", "R25.0", "enu", "Support", "acad.cuix");
        var b = MakeFile("Autodesk", "AutoCAD 2024", "R24.3", "zh-CN", "Support", "acad.cuix");
        var found = CuixLocator.FindCuix(VendorProfiles.Acad);
        Assert.Equal(2, found.Count);
        Assert.Contains(a, found);
        Assert.Contains(b, found);
    }

    [Fact]
    public void AcadProfile_IgnoresNonAutoCadDirs()
    {
        MakeFile("Autodesk", "DWG TrueView 2025", "R25.0", "enu", "Support", "acad.cuix");
        Assert.Empty(CuixLocator.FindCuix(VendorProfiles.Acad));
    }

    [Fact]
    public void NoProfileDirs_ReturnsEmpty()
    {
        Assert.Empty(CuixLocator.FindCuix(VendorProfiles.Zwcad));
        Assert.Empty(CuixLocator.FindCuix(VendorProfiles.Acad));
    }
}
