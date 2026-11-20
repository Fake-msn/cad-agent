namespace CadAgent.Core.Tests;

public class SkillSetTests
{
    private static Rule R(string id) => new(id,
        new RuleSource("院标", "1.0", "摘录", "source-text"), "应",
        RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = "charset" }), "建议");

    private static SkillPackage Pkg(string name, string version, params Rule[] rules) =>
        new() { Manifest = new SkillManifest(name, version, "any", "any", ""), Rules = rules,
                Aliases = Array.Empty<SkillAlias>(), SourceDir = "." };

    [Fact]
    public void Compose_NoCollision_MergesRulesAndVersion()
    {
        var set = SkillSet.Compose(new[] { R("B1") }, new[] { Pkg("院标A", "1.0.0", R("S1")) });
        Assert.Equal(2, set.AllRules.Count);
        Assert.Contains("gb50001-2017.r1", set.RulePackVersion);
        Assert.Contains("院标A@1.0.0", set.RulePackVersion);
        Assert.True(set.ContainsRule("S1"));
        Assert.False(set.ContainsRule("NOPE"));
    }

    [Fact]
    public void Compose_BuiltinSkillCollision_Throws()
    {
        var ex = Assert.Throws<InvalidSkillException>(() =>
            SkillSet.Compose(new[] { R("DUP") }, new[] { Pkg("院标A", "1.0.0", R("DUP")) }));
        Assert.Contains("DUP", ex.Message);
        Assert.Contains("院标A", ex.Message);
    }

    [Fact]
    public void Compose_SkillSkillCollision_Throws()
    {
        Assert.Throws<InvalidSkillException>(() =>
            SkillSet.Compose(Array.Empty<Rule>(), new[] { Pkg("A", "1.0.0", R("X")), Pkg("B", "1.0.0", R("X")) }));
    }

    [Fact]
    public void Compose_AliasesMerge_CaseInsensitive_LastWins()
    {
        // T1 SkillPackage.Aliases 为 init-only：以对象初始化器构造（契约不变）。
        var p1 = new SkillPackage
        {
            Manifest = new SkillManifest("A", "1.0.0", "any", "any", ""), SourceDir = ".",
            Aliases = new[] { new SkillAlias("W-墙", SemanticClass.Wall) },
        };
        var p2 = new SkillPackage
        {
            Manifest = new SkillManifest("B", "1.0.0", "any", "any", ""), SourceDir = ".",
            Aliases = new[] { new SkillAlias("w-墙", SemanticClass.Column) },
        };
        var set = SkillSet.Compose(Array.Empty<Rule>(), new[] { p1, p2 });
        Assert.Equal(SemanticClass.Column, set.MergedAliases["W-墙"]);
    }
}
