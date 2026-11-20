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

    [Fact]
    public void FieldBoundaryShift_ChangesHash()
    {
        var a = new AuditEntry(DateTimeOffset.UnixEpoch, "a|b", "c", "s", "src", "k", "t", "{}", "b", "a", "", "p");
        var b = a with { Operator = "a", Role = "b|c" };
        Assert.NotEqual(
            HashChain.Compute(a, 1, HashChain.GenesisHash),
            HashChain.Compute(b, 1, HashChain.GenesisHash));
    }

    [Fact]
    public void Canonical_GoldenVector_PinsContract()
    {
        var e = new AuditEntry(
            new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero),
            "TEST\\user", "Draw", "s1", "plan", "intake", "h1", "{}",
            "before", "after", "snap", "p1");
        string canonical = HashChain.Canonical(e, 1, HashChain.GenesisHash);
        Assert.Equal(
            "1|2026-11-20T00:00:00.0000000+00:00|TEST\\\\user|Draw|s1|plan|intake|h1|{}|before|after|snap|p1|" + HashChain.GenesisHash,
            canonical);
        // pin the uppercase SHA256 of exactly that canonical string
        // (computed independently via Node crypto and PowerShell SHA256 over UTF8-no-BOM bytes)
        string expected = "E17C60B9A88BD81C9F17A64CE7FAD795C4A334AB08242C1588F243E772270C03";
        Assert.Equal(expected, HashChain.Compute(e, 1, HashChain.GenesisHash));
    }
}
