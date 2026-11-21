namespace CadAgent.Core.Tests;

public class RuleLoaderTests
{
    private const string ValidYaml = """
        id: R-50001-5.0.2
        source:
          standard: "GB/T 50001-2017"
          clause: "5.0.2"
          excerpt: "文字的字高，应从表 5.0.2 中选用。"
          verification: source-text
        modal: "应／宜"
        force: recommended
        severity: high
        scope: { discipline: any, drawingType: any }
        check:
          type: text-height-series
          params:
            hanVector: [3.5, 5, 7, 10, 14, 20]
            other: [3, 4, 6, 8, 10, 14, 20]
        suggestion: "字高改选系列值"
        """;

    [Fact]
    public void LoadYaml_Valid_ParsesAllFields()
    {
        var rule = RuleLoader.LoadYaml(ValidYaml);
        Assert.Equal("R-50001-5.0.2", rule.Id);
        Assert.Equal("GB/T 50001-2017", rule.Source.Standard);
        Assert.Equal("5.0.2", rule.Source.Clause);
        Assert.Equal(RuleForce.Recommended, rule.Force);
        Assert.Equal(RuleSeverity.High, rule.Severity);
        Assert.Equal("text-height-series", rule.Check.Type);
        Assert.Equal(2, rule.Check.Params.Count);
    }

    [Fact]
    public void LoadYaml_MissingSource_Throws()
    {
        var ex = Assert.Throws<InvalidRuleException>(() => RuleLoader.LoadYaml("id: X\ncheck:\n  type: t\n"));
        Assert.Contains("source", ex.Message);
    }

    [Fact]
    public void LoadYaml_BadVerification_Throws()
    {
        string yaml = ValidYaml.Replace("verification: source-text", "verification: guessed");
        Assert.Throws<InvalidRuleException>(() => RuleLoader.LoadYaml(yaml));
    }

    [Fact]
    public void LoadYaml_MalformedYaml_InnerExceptionPreserved()
    {
        // 2b-ii-b Task 2：YamlException wrap 必须保留内层异常（SkillLoader 已把
        // InvalidRuleException 作为 inner——链条至此完整）。
        var ex = Assert.Throws<InvalidRuleException>(() => RuleLoader.LoadYaml("id: [unclosed"));
        Assert.IsType<YamlDotNet.Core.YamlException>(ex.InnerException);
    }

    [Fact]
    public void LoadDirectory_InvalidRuleFile_InnerExceptionPreserved()
    {
        // 终审修复波（裁定 3）：目录级 rewrap 不得丢内层——文件名包装须携带 ex，
        // 链条 LoadDirectory(InvalidRuleException) → LoadYaml(InvalidRuleException) → YamlException 完整。
        string dir = Path.Combine(Path.GetTempPath(), $"ruleloader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.yaml"), "id: [unclosed");
            var ex = Assert.Throws<InvalidRuleException>(() => RuleLoader.LoadDirectory(dir));
            Assert.Contains("bad.yaml", ex.Message);
            var inner = Assert.IsType<InvalidRuleException>(ex.InnerException);
            Assert.IsType<YamlDotNet.Core.YamlException>(inner.InnerException);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadYaml_PendingOriginal_RejectedForNumericRules()
    {
        // pending-original 允许加载但引擎标记不可执行——加载层仅校验枚举合法性
        string yaml = ValidYaml.Replace("verification: source-text", "verification: pending-original");
        var rule = RuleLoader.LoadYaml(yaml);
        Assert.Equal("pending-original", rule.Source.Verification);
    }
}
