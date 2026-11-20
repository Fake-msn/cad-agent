namespace CadAgent.Core.Tests;

public class SqliteAuditStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cadagent-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditStore NewStore() => new(_dbPath);
    public void Dispose()
    {
        // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static AuditEntry Entry(string kind) =>
        new(DateTimeOffset.UtcNow, "TEST\\user", "Draw", "s1", "plan",
            kind, "t", "{}", "b", "a", "", "p");

    [Fact]
    public void Append_AssignsSeqAndLinksChain()
    {
        using var store = NewStore();
        var r1 = store.Append(Entry("intake"));
        var r2 = store.Append(Entry("step"));
        Assert.Equal(1, r1.Seq);
        Assert.Equal(2, r2.Seq);
        Assert.Equal(HashChain.GenesisHash, r1.PrevHash);
        Assert.Equal(r1.Hash, r2.PrevHash);
    }

    [Fact]
    public void VerifyChain_OkOnUntamperedLog()
    {
        using var store = NewStore();
        store.Append(Entry("a"));
        store.Append(Entry("b"));
        Assert.True(store.VerifyChain(out string error));
        Assert.Equal("", error);
    }

    [Fact]
    public void VerifyChain_DetectsTampering()
    {
        using (var store = NewStore())
        {
            store.Append(Entry("a"));
            store.Append(Entry("b"));
        }
        // 绕过应用层直接改库，模拟篡改
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audit SET operator = 'TEST\\attacker' WHERE seq = 1";
            cmd.ExecuteNonQuery();
        }
        using var verify = NewStore();
        Assert.False(verify.VerifyChain(out string error));
        Assert.Contains("seq 1", error);
    }

    [Fact]
    public void ConcurrentAppends_AllPersistedAndChainValid()
    {
        using var store = NewStore();
        Parallel.For(0, 50, i => store.Append(Entry($"k{i}")));
        var all = store.ReadAll();
        Assert.Equal(50, all.Count);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), all.Select(r => r.Seq));
        Assert.True(store.VerifyChain(out _));
    }

    [Fact]
    public void ReopenStore_PreservesChain()
    {
        using (var store = NewStore()) store.Append(Entry("a"));
        using var reopened = NewStore();
        var r2 = reopened.Append(Entry("b"));
        Assert.Equal(2, r2.Seq);
        Assert.True(reopened.VerifyChain(out _));
    }
}