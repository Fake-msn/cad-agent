namespace CadAgent.Core.Tests;

public class AdjudicationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"adj-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); }

    private static Rule MakeRule(RuleForce f) =>
        new("R1", new RuleSource("GB/T 50001-2017", "5.0.2", "excerpt", "source-text"),
            "应", f, RuleSeverity.High, new RuleScope(), new CheckSpec("t", new()), "fix");

    [Fact]
    public void Load_Missing_Empty()
    {
        var s = AdjudicationSet.Load(_path);
        Assert.Empty(s.Items);
    }

    [Fact]
    public void Upsert_SameRule_Replaces_LatestWins()
    {
        var s = new AdjudicationSet();
        s.Upsert(new Adjudication("R1", "u1", DateTimeOffset.UnixEpoch, "r1", RuleForce.Mandatory));
        s.Upsert(new Adjudication("R1", "u2", DateTimeOffset.UnixEpoch, "r2", RuleForce.Informational));
        Assert.Single(s.Items);
        Assert.Equal(RuleForce.Informational, s.EffectiveForce(MakeRule(RuleForce.Recommended)));
    }

    [Fact]
    public void EffectiveForce_NoAdjudication_RuleDefault()
    {
        var s = new AdjudicationSet();
        Assert.Equal(RuleForce.Recommended, s.EffectiveForce(MakeRule(RuleForce.Recommended)));
    }

    [Fact]
    public void Exempt_Signal()
    {
        var s = new AdjudicationSet();
        s.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "本图类不适用", null, true));
        Assert.True(s.IsExempt("R1"));
    }

    [Fact]
    public void Save_Atomic_RoundTrips()
    {
        var s = new AdjudicationSet();
        s.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "理由", RuleForce.Mandatory, false, "解读"));
        s.Save(_path);
        var loaded = AdjudicationSet.Load(_path);
        Assert.Single(loaded.Items);
        Assert.Equal("解读", loaded.Items[0].Interpretation);
        Assert.False(File.Exists(_path + ".tmp"));
    }
}