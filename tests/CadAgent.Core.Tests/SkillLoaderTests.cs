namespace CadAgent.Core.Tests;

public class SkillLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skill-{Guid.NewGuid():N}");
    public SkillLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private void WriteSkill(string version = "1.0.0", string rules = "", string aliases = "")
    {
        File.WriteAllText(Path.Combine(_dir, "skill.yaml"), $"""
            name: 示例院标
            version: "{version}"
            discipline: architecture
            drawingType: any
            description: 测试 skill
            """);
        if (rules != "")
        {
            Directory.CreateDirectory(Path.Combine(_dir, "rules"));
            File.WriteAllText(Path.Combine(_dir, "rules", "r1.yaml"), rules);
        }
        if (aliases != "")
            File.WriteAllText(Path.Combine(_dir, "aliases.yaml"), aliases);
    }

    private const string RuleYaml = """
        id: SK-TEST-001
        source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
        modal: "应"
        force: mandatory
        severity: high
        scope: { discipline: architecture, drawingType: any }
        check: { type: layer-name, params: { mode: charset } }
        suggestion: "测试"
        """;

    [Fact]
    public void Load_ValidPackage_ManifestRulesAliases()
    {
        WriteSkill(rules: RuleYaml, aliases: "aliases:\n  - layerName: W-新墙\n    class: Wall\n");
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Equal("示例院标", pkg.Manifest.Name);
        Assert.Equal("1.0.0", pkg.Manifest.Version);
        Assert.Single(pkg.Rules);
        Assert.Single(pkg.Aliases);
        Assert.Equal(SemanticClass.Wall, pkg.Aliases[0].Class);
    }

    [Fact]
    public void Load_MissingManifest_Throws()
    {
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("skill.yaml", ex.Message);
    }

    [Fact]
    public void Load_BadVersion_Throws()
    {
        WriteSkill(version: "abc");
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
    }

    [Fact]
    public void Load_DuplicateRuleId_Throws()
    {
        WriteSkill(rules: RuleYaml);
        File.WriteAllText(Path.Combine(_dir, "rules", "r2.yaml"), RuleYaml);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("SK-TEST-001", ex.Message);
    }

    [Fact]
    public void Load_NoAliasesFile_Empty()
    {
        WriteSkill();
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Empty(pkg.Aliases);
        Assert.Empty(pkg.Rules);
    }

    [Fact]
    public void Load_StepsSymbolsDirs_Tolerated()
    {
        WriteSkill();
        Directory.CreateDirectory(Path.Combine(_dir, "steps"));
        Directory.CreateDirectory(Path.Combine(_dir, "symbols"));
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Equal("示例院标", pkg.Manifest.Name);
    }

    [Fact]
    public void Load_NumericClassString_Throws()
    {
        // 2b-ii-b Task 2：全数字串（如 "3"）虽能被 Enum.TryParse 解析为已定义数值，
        // 但 strict parity 要求拒绝——class 必须是枚举名（ignoreCase），不接受数字串。
        WriteSkill(aliases: "aliases:\n  - layerName: W-墙\n    class: \"3\"\n");
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("class", ex.Message);
    }

    [Fact]
    public void Load_DuplicateLayerName_CaseInsensitive_Throws()
    {
        // 2b-ii-b Task 2：包内 layerName OrdinalIgnoreCase 重复→拒绝并列出重复名。
        WriteSkill(aliases: "aliases:\n  - layerName: W-墙\n    class: Wall\n  - layerName: w-墙\n    class: Door\n");
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("w-墙", ex.Message);
    }

    [Fact]
    public void Load_UnknownManifestKey_Throws()
    {
        WriteSkill();
        File.AppendAllText(Path.Combine(_dir, "skill.yaml"), "\nbogusKey: x\n");
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
    }
}
