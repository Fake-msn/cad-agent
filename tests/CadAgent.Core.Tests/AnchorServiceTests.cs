using CadAgent.Core.Audit;

namespace CadAgent.Core.Tests;

/// <summary>
/// 3c T3：外部锚——HMAC+DPAPI 密钥；尾截断/伪造检测；store Append 自动更新锚。
/// </summary>
public class AnchorServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}");
    public AnchorServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void UpdateVerify_Ok()
    {
        var a = new AnchorService(_dir);
        a.Update(5, "HASH5");
        Assert.Equal(AnchorStatus.Ok, a.Verify(5, "HASH5"));
    }

    [Fact]
    public void Verify_Truncated()
    {
        var a = new AnchorService(_dir);
        a.Update(10, "HASH10");
        Assert.Equal(AnchorStatus.Truncated, a.Verify(7, "HASH7"));
    }

    [Fact]
    public void Verify_Forged_TamperedAnchor()
    {
        var a = new AnchorService(_dir);
        a.Update(5, "HASH5");
        string path = Path.Combine(_dir, "anchor.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("HASH5", "EVIL"));
        Assert.Equal(AnchorStatus.Forged, new AnchorService(_dir).Verify(5, "EVIL"));
    }

    [Fact]
    public void Verify_Missing()
    {
        Assert.Equal(AnchorStatus.Missing, new AnchorService(_dir).Verify(1, "H"));
    }

    [Fact]
    public void StoreAppend_UpdatesAnchor()
    {
        string db = Path.Combine(_dir, "a.db");
        var anchor = new AnchorService(_dir);
        using var store = new SqliteAuditStore(db, anchor);
        var r = store.Append(new AuditEntry(DateTimeOffset.UtcNow, "u", "Draw", "s", "t", "k", "t", "{}", "", "", "", ""));
        Assert.Equal(AnchorStatus.Ok, anchor.Verify(r.Seq, r.Hash));
    }
}