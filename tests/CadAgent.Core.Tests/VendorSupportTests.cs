using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

public class VendorSupportTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vendor-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Load_Find_VersionPrefixMatch()
    {
        File.WriteAllText(_path, """
            {"entries":[
              {"vendor":"ZWCAD","versionRange":"2025","adapterVersion":"1.0","status":"Verified","verifiedAt":"2026-11-21","testCoverage":"open/saveas/roundtrip"},
              {"vendor":"AutoCAD","versionRange":"2024-2026","adapterVersion":"0.0","status":"CompatibleUnverified","verifiedAt":null,"testCoverage":""}
            ]}
            """);
        var m = VendorMatrix.Load(_path);
        Assert.Equal(2, m.Entries.Count);
        Assert.True(m.IsVerified("ZWCAD", "2025"));
        Assert.False(m.IsVerified("AutoCAD", "2025"));
        Assert.Null(m.Find("GstarCAD", "2025"));
    }

    [Fact]
    public void Load_Missing_Empty()
    {
        Assert.Empty(VendorMatrix.Load(_path).Entries);
    }
}