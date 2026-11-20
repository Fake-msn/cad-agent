namespace CadAgent.Core.Tests;

public class HashChainTests
{
    private static AuditEntry Entry(string kind) =>
        new(DateTimeOffset.UnixEpoch, "TEST\\user", "Draw", "s1", "plan",
            kind, "handle-1", "{}", "before", "after", "", "p1");

    [Fact]
    public void SameInput_SameHash()
    {
        var h1 = HashChain.Compute(Entry("intake"), 1, HashChain.GenesisHash);
        var h2 = HashChain.Compute(Entry("intake"), 1, HashChain.GenesisHash);
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);
    }

    [Fact]
    public void TamperedField_ChangesHash()
    {
        var original = Entry("intake");
        var tampered = original with { Operator = "TEST\\attacker" };
        Assert.NotEqual(
            HashChain.Compute(original, 1, HashChain.GenesisHash),
            HashChain.Compute(tampered, 1, HashChain.GenesisHash));
    }

    [Fact]
    public void PrevHash_LinksChain()
    {
        var h1 = HashChain.Compute(Entry("a"), 1, HashChain.GenesisHash);
        var h2a = HashChain.Compute(Entry("b"), 2, h1);
        var h2b = HashChain.Compute(Entry("b"), 2, HashChain.GenesisHash);
        Assert.NotEqual(h2a, h2b);
    }
}