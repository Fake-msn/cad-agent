using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

public class BridgeFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bridge").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void NextPending_SkipsCompletedAndPicksLowestId()
    {
        File.WriteAllText(Path.Combine(_dir, "req-3.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "res-3.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "res-3.done"), "x");
        File.WriteAllText(Path.Combine(_dir, "req-5.txt"), "y");
        var p = BridgeFiles.NextPending(_dir)!;
        Assert.Equal("5", p.Value.Id);
    }

    [Fact]
    public void NextPending_NonePending_ReturnsNull()
        => Assert.Null(BridgeFiles.NextPending(_dir));

    [Fact]
    public void WriteResultAtomic_WritesResThenDone()
    {
        BridgeFiles.WriteResultAtomic(_dir, "7", "status\tok\n");
        Assert.True(File.Exists(Path.Combine(_dir, "res-7.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "res-7.done")));
        Assert.Equal("status\tok\n", File.ReadAllText(Path.Combine(_dir, "res-7.txt")));
    }

    [Fact]
    public void WriteResultAtomic_ResultFileHasNoBom_SoFirstFieldStaysStatus()
    {
        BridgeFiles.WriteResultAtomic(_dir, "9", "status\tok\n");
        byte[] bytes = File.ReadAllBytes(Path.Combine(_dir, "res-9.txt"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("ok", BridgeResult.Parse(File.ReadAllText(Path.Combine(_dir, "res-9.txt"))).Status);
    }
}
