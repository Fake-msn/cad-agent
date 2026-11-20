namespace CadAgent.Core.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cadagent-snap-{Guid.NewGuid():N}");
    private readonly string _source;
    public SnapshotStoreTests()
    {
        _source = Path.Combine(Path.GetTempPath(), $"cadagent-src-{Guid.NewGuid():N}.dxf");
        File.WriteAllText(_source, "original");
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_source)) File.Delete(_source);
    }

    [Fact]
    public void Take_CopiesFile_UnderTxDir()
    {
        var store = new SnapshotStore(_root);
        string snap = store.Take(_source, "tx1");
        Assert.True(File.Exists(snap));
        Assert.Equal("original", File.ReadAllText(snap));
        Assert.Equal(Path.Combine(_root, "tx1", Path.GetFileName(_source)), snap);
    }

    [Fact]
    public void Restore_BringsBackOriginalContent()
    {
        var store = new SnapshotStore(_root);
        store.Take(_source, "tx1");
        File.WriteAllText(_source, "modified");
        store.Restore("tx1", _source);
        Assert.Equal("original", File.ReadAllText(_source));
    }

    [Fact]
    public void Find_UnknownTx_Throws()
    {
        var store = new SnapshotStore(_root);
        Assert.Throws<FileNotFoundException>(() => store.Find("nope"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\..\\x")]
    [InlineData("C:\\x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(" ")]
    public void Find_RejectsUnsafeTxIds(string txId)
    {
        var store = new SnapshotStore(_root);
        Assert.Throws<ArgumentException>(() => store.Find(txId));
    }

    [Fact]
    public void Take_RejectsUnsafeTxId()
    {
        var store = new SnapshotStore(_root);
        Assert.Throws<ArgumentException>(() => store.Take(_source, "..\\evil"));
    }

    [Fact]
    public void Find_MultipleFiles_Throws()
    {
        var store = new SnapshotStore(_root);
        store.Take(_source, "tx1");
        File.WriteAllText(Path.Combine(_root, "tx1", "extra.dxf"), "x");
        Assert.Throws<InvalidOperationException>(() => store.Find("tx1"));
    }
}
