using System.Text.Json;

namespace CadAgent.Core.Tests;

/// <summary>
/// 2b-ii-b Task 7：TemplateInstantiator 契约——精确整串占位替换、缺参列名、
/// 未知占位符拒绝、数值原样透传（类型校验下游）、planId = name-Guid:N、模板不可变。
/// </summary>
public class StepTemplateTests
{
    private static StepTemplate LineTemplate() => new(
        "line-run",
        "two-point line",
        new[] { "x1", "x2" },
        new[]
        {
            new TemplateStep("add-line", new Dictionary<string, object>
            {
                ["x1"] = "{{x1}}",
                ["y1"] = 0L,
                ["x2"] = "{{x2}}",
                ["y2"] = 0L,
                ["layer"] = "E-LITE",
            }),
        });

    [Fact]
    public void Instantiate_SubstitutesExactPlaceholders_KeepsLiterals()
    {
        var plan = TemplateInstantiator.Instantiate(LineTemplate(),
            new Dictionary<string, object> { ["x1"] = 10.5, ["x2"] = 20 });
        var step = Assert.Single(plan.Steps);
        Assert.Equal("add-line", step.Kind);
        Assert.Equal(10.5, step.Params.GetProperty("x1").GetDouble());
        Assert.Equal(20, step.Params.GetProperty("x2").GetInt32());
        Assert.Equal(0, step.Params.GetProperty("y1").GetInt32());
        Assert.Equal(0, step.Params.GetProperty("y2").GetInt32());
        Assert.Equal("E-LITE", step.Params.GetProperty("layer").GetString());
    }

    [Fact]
    public void Instantiate_PlanId_TemplateNamePlusGuidN_Unique()
    {
        var args = new Dictionary<string, object> { ["x1"] = 0, ["x2"] = 1 };
        var p1 = TemplateInstantiator.Instantiate(LineTemplate(), args);
        var p2 = TemplateInstantiator.Instantiate(LineTemplate(), args);
        Assert.StartsWith("line-run-", p1.PlanId);
        var suffix = p1.PlanId["line-run-".Length..];
        Assert.Equal(32, suffix.Length);
        Assert.All(suffix, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.NotEqual(p1.PlanId, p2.PlanId);
    }

    [Fact]
    public void Instantiate_MissingArgs_ThrowsListingAllMissingNames()
    {
        var t = new StepTemplate("t", "", new[] { "x1", "x2", "x3" },
            new[] { new TemplateStep("add-line", new Dictionary<string, object>()) });
        var ex = Assert.Throws<InvalidSkillException>(() =>
            TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x1"] = 0 }));
        Assert.Contains("t", ex.Message);
        Assert.Contains("x2", ex.Message);
        Assert.Contains("x3", ex.Message);
    }

    [Fact]
    public void Instantiate_UnknownPlaceholder_Throws_EvenIfArgPresent()
    {
        // 未知 = 不在 ParamNames——即便 args 恰好带了同名键也拒绝（模板声明即契约）。
        var t = new StepTemplate("t", "", new[] { "x1" },
            new[] { new TemplateStep("add-line", new Dictionary<string, object> { ["y2"] = "{{bogus}}" }) });
        var ex = Assert.Throws<InvalidSkillException>(() =>
            TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x1"] = 0, ["bogus"] = 5 }));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Instantiate_NumericArgs_PassThroughAsNumbers_StringArgsVerbatim()
    {
        var t = new StepTemplate("t", "", new[] { "a", "b", "s", "ns" },
            new[]
            {
                new TemplateStep("k", new Dictionary<string, object>
                {
                    ["a"] = "{{a}}", ["b"] = "{{b}}", ["s"] = "{{s}}", ["ns"] = "{{ns}}",
                }),
            });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object>
        {
            ["a"] = 123, ["b"] = 45.5, ["s"] = "E-LITE", ["ns"] = "3.5",
        });
        var p = plan.Steps[0].Params;
        Assert.Equal(JsonValueKind.Number, p.GetProperty("a").ValueKind);
        Assert.Equal(123, p.GetProperty("a").GetInt32());
        Assert.Equal(JsonValueKind.Number, p.GetProperty("b").ValueKind);
        Assert.Equal(45.5, p.GetProperty("b").GetDouble());
        Assert.Equal(JsonValueKind.String, p.GetProperty("s").ValueKind);
        Assert.Equal("E-LITE", p.GetProperty("s").GetString());
        // 第一 slice：数字形态字符串原样透传，类型校验在 StepPlanParser/引擎处理器下游。
        Assert.Equal(JsonValueKind.String, p.GetProperty("ns").ValueKind);
        Assert.Equal("3.5", p.GetProperty("ns").GetString());
    }

    [Fact]
    public void Instantiate_NonExactPlaceholderStrings_LeftVerbatim()
    {
        // 仅整串精确 "{{name}}" 才替换；嵌入/带空白的一律原样保留。
        var t = new StepTemplate("t", "", new[] { "x1" },
            new[]
            {
                new TemplateStep("k", new Dictionary<string, object>
                {
                    ["embedded"] = "pre{{x1}}post",
                    ["trailing"] = "{{x1}} ",
                    ["exact"] = "{{x1}}",
                }),
            });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x1"] = 7 });
        var p = plan.Steps[0].Params;
        Assert.Equal("pre{{x1}}post", p.GetProperty("embedded").GetString());
        Assert.Equal("{{x1}} ", p.GetProperty("trailing").GetString());
        Assert.Equal(7, p.GetProperty("exact").GetInt32());
    }

    [Fact]
    public void Instantiate_MultipleSteps_OrderPreserved_TemplateNotMutated()
    {
        var t = new StepTemplate("t", "", new[] { "x" },
            new[]
            {
                new TemplateStep("add-line", new Dictionary<string, object> { ["x1"] = "{{x}}" }),
                new TemplateStep("add-text", new Dictionary<string, object> { ["content"] = "{{x}}" }),
            });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = "hello" });
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("add-line", plan.Steps[0].Kind);
        Assert.Equal("add-text", plan.Steps[1].Kind);
        Assert.Equal("hello", plan.Steps[1].Params.GetProperty("content").GetString());
        // 实例化不得就地改写模板（同一模板可反复实例化）。
        Assert.Equal("{{x}}", t.Sequence[0].Params["x1"]);
        Assert.Equal("{{x}}", t.Sequence[1].Params["content"]);
    }

    [Fact]
    public void Instantiate_NoParamNames_LiteralOnly_Works()
    {
        var t = new StepTemplate("fixed", "", Array.Empty<string>(),
            new[] { new TemplateStep("add-line", new Dictionary<string, object> { ["x1"] = 1 }) });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object>());
        Assert.Equal(1, plan.Steps[0].Params.GetProperty("x1").GetInt32());
    }
}

/// <summary>
/// 2b-ii-b Task 7：SkillLoader steps/ 加性加载——缺失→空； strict schema（未知键拒绝、
/// 名称非空/包内唯一、kind 非空、sequence 必需）；非法 YAML→InvalidSkillException 保留 inner；
/// plain 数值标量保持数值类型（引号标量保持字符串）。
/// </summary>
public class StepTemplateLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skill-steps-{Guid.NewGuid():N}");

    public StepTemplateLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "skill.yaml"), """
            name: steps-skill
            version: "1.0.0"
            discipline: electrical
            drawingType: any
            description: step template test
            """);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private void WriteSteps(string fileName, string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "steps"));
        File.WriteAllText(Path.Combine(_dir, "steps", fileName), yaml);
    }

    private const string LineYaml = """
        templates:
          - name: line-run
            description: two-point line
            params: [x1, x2]
            sequence:
              - kind: add-line
                params: { x1: "{{x1}}", y1: 0, x2: "{{x2}}", y2: 0, ratio: 2.5, tag: "0" }
        """;

    [Fact]
    public void Load_Steps_ParsesTemplateSchema_NumericScalarsTyped()
    {
        WriteSteps("line.yaml", LineYaml);
        var pkg = SkillLoader.LoadDirectory(_dir);
        var t = Assert.Single(pkg.Steps);
        Assert.Equal("line-run", t.Name);
        Assert.Equal("two-point line", t.Description);
        Assert.Equal(new[] { "x1", "x2" }, t.ParamNames);
        var step = Assert.Single(t.Sequence);
        Assert.Equal("add-line", step.Kind);
        Assert.Equal("{{x1}}", step.Params["x1"]);
        // plain YAML 数值标量→数值（long/double）；引号标量→字符串。
        Assert.Equal(0L, step.Params["y1"]);
        Assert.Equal(2.5, step.Params["ratio"]);
        Assert.Equal("0", step.Params["tag"]);

        // 加载→实例化贯通：数值字面量在计划 JSON 中仍为 number。
        var plan = TemplateInstantiator.Instantiate(t,
            new Dictionary<string, object> { ["x1"] = 1.5, ["x2"] = 9 });
        var p = plan.Steps[0].Params;
        Assert.Equal(JsonValueKind.Number, p.GetProperty("y1").ValueKind);
        Assert.Equal(1.5, p.GetProperty("x1").GetDouble());
        Assert.Equal(9, p.GetProperty("x2").GetInt32());
        Assert.Equal("0", p.GetProperty("tag").GetString());
    }

    [Fact]
    public void Load_NoStepsDirOrEmptyDir_Empty()
    {
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Steps);
        Directory.CreateDirectory(Path.Combine(_dir, "steps"));
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Steps);
    }

    [Fact]
    public void Load_MultipleFilesAndTemplates_OrderedByFileName()
    {
        WriteSteps("b.yaml", """
            templates:
              - name: tb
                sequence: [ { kind: k } ]
            """);
        WriteSteps("a.yaml", """
            templates:
              - name: ta1
                sequence: [ { kind: k } ]
              - name: ta2
                sequence: [ { kind: k } ]
            """);
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Equal(new[] { "ta1", "ta2", "tb" }, pkg.Steps.Select(t => t.Name));
        // description/params 缺省→空串/空表。
        Assert.Equal("", pkg.Steps[0].Description);
        Assert.Empty(pkg.Steps[0].ParamNames);
    }

    [Fact]
    public void Load_MalformedStepsYaml_ThrowsWithInner()
    {
        WriteSteps("bad.yaml", "templates: [unclosed");
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("bad.yaml", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Load_UnknownTemplateKey_Throws()
    {
        WriteSteps("u.yaml", """
            templates:
              - name: t
                params: []
                sequence: [ { kind: k } ]
                bogusKey: x
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("u.yaml", ex.Message);
    }

    [Fact]
    public void Load_EmptyTemplateName_Throws()
    {
        WriteSteps("n.yaml", """
            templates:
              - name: ""
                sequence: [ { kind: k } ]
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Load_DuplicateTemplateName_Throws()
    {
        WriteSteps("a.yaml", LineYaml);
        WriteSteps("b.yaml", LineYaml);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("line-run", ex.Message);
    }

    [Fact]
    public void Load_DuplicateParamName_Throws()
    {
        WriteSteps("d.yaml", """
            templates:
              - name: t
                params: [x1, x1]
                sequence: [ { kind: k } ]
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("x1", ex.Message);
    }

    [Fact]
    public void Load_EmptyKind_Throws()
    {
        WriteSteps("k.yaml", """
            templates:
              - name: t
                sequence: [ { kind: "" } ]
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("kind", ex.Message);
    }

    [Fact]
    public void Load_MissingSequence_Throws()
    {
        WriteSteps("s.yaml", """
            templates:
              - name: t
                params: [x1]
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("sequence", ex.Message);
    }

    [Fact]
    public void Load_EmptyTemplatesDocument_NoTemplates()
    {
        WriteSteps("a.yaml", "templates: []");
        WriteSteps("b.yaml", "");
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Steps);
    }
}
