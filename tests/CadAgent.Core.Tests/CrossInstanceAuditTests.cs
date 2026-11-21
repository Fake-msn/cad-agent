namespace CadAgent.Core.Tests;

/// <summary>
/// Plan 3a T5: cross-process/cross-instance audit serialization — BEGIN IMMEDIATE
/// acquires the write lock at transaction start; busy retry as belt-and-braces.
/// Two store instances on the same DB appending concurrently must yield one valid chain.
/// </summary>
public class CrossInstanceAuditTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"xinst-{Guid.NewGuid():N}.db");
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_db)) File.Delete(_db);
        if (File.Exists(_db + "-wal")) File.Delete(_db + "-wal");
        if (File.Exists(_db + "-shm")) File.Delete(_db + "-shm");
    }

    private static AuditEntry E(string k) =>
        new(DateTimeOffset.UtcNow, "T\\u", "Draw", "s", "test", k, "t", "{}", "b", "a", "", "p");

    [Fact]
    public async Task TwoInstances_ConcurrentAppend_ChainValid()
    {
        using var a = new SqliteAuditStore(_db);
        using var b = new SqliteAuditStore(_db);
        using var barrier = new Barrier(2);
        var tasks = new[]
        {
            Task.Run(() => { barrier.SignalAndWait(); for (int i = 0; i < 500; i++) { a.Append(E($"a{i}")); Thread.Yield(); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (int i = 0; i < 500; i++) { b.Append(E($"b{i}")); Thread.Yield(); } })
        };
        await Task.WhenAll(tasks);
        var all = a.ReadAll();
        Assert.Equal(1000, all.Count);
        Assert.Equal(Enumerable.Range(1, 1000).Select(i => (long)i), all.Select(r => r.Seq));
        Assert.True(a.VerifyChain(out _));
        Assert.True(b.VerifyChain(out _));
    }
}