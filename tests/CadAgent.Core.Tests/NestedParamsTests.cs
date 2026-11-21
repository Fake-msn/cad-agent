namespace CadAgent.Core.Tests;

/// <summary>
/// Plan 3a T3: nested params contract — recursive placeholder substitution through
/// maps/lists; unknown placeholders rejected at any depth; numerics preserved;
/// \z whole-string exactness unchanged.
/// </summary>
public class NestedParamsTests
{
    private static StepTemplate T(Dictionary<string, object> stepParams) =>
        new("t", "", new[] { "x" }, new[] { new TemplateStep("add-line", stepParams) });

    [Fact]
    public void NestedMap_PlaceholderSubstituted()
    {
        var t = T(new Dictionary<string, object>
        {
            ["x1"] = "{{x}}",
            ["nested"] = new Dictionary<string, object> { ["inner"] = "{{x}}", ["keep"] = 5 }
        });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 7 });
        var p = plan.Steps[0].Params;
        Assert.Equal(7, p.GetProperty("x1").GetInt32());
        Assert.Equal(7, p.GetProperty("nested").GetProperty("inner").GetInt32());
        Assert.Equal(5, p.GetProperty("nested").GetProperty("keep").GetInt32());
    }

    [Fact]
    public void NestedList_PlaceholderSubstituted()
    {
        var t = T(new Dictionary<string, object> { ["pts"] = new List<object> { "{{x}}", 0, "{{x}}" } });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 3 });
        var arr = plan.Steps[0].Params.GetProperty("pts");
        Assert.Equal(3, arr[0].GetInt32());
        Assert.Equal(0, arr[1].GetInt32());
        Assert.Equal(3, arr[2].GetInt32());
    }

    [Fact]
    public void NestedUnknownPlaceholder_Rejected()
    {
        var t = T(new Dictionary<string, object>
        {
            ["nested"] = new Dictionary<string, object> { ["inner"] = "{{bogus}}" }
        });
        var ex = Assert.Throws<InvalidSkillException>(() =>
            TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 1, ["bogus"] = 2 }));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void NestedDeepMixed_Substituted()
    {
        var t = T(new Dictionary<string, object>
        {
            ["a"] = new List<object>
            {
                new Dictionary<string, object> { ["b"] = "{{x}}" },
                "{{x}}"
            }
        });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 9 });
        var a = plan.Steps[0].Params.GetProperty("a");
        Assert.Equal(9, a[0].GetProperty("b").GetInt32());
        Assert.Equal(9, a[1].GetInt32());
    }

    [Fact]
    public void YamlLoaderNestedMap_ObjectKeys_Substituted()
    {
        // YamlDotNet 嵌套映射默认 Dictionary<object,object>——递归须兼容
        var t = T(new Dictionary<string, object>
        {
            ["nested"] = new Dictionary<object, object> { ["inner"] = "{{x}}" }
        });
        var plan = TemplateInstantiator.Instantiate(t, new Dictionary<string, object> { ["x"] = 4 });
        Assert.Equal(4, plan.Steps[0].Params.GetProperty("nested").GetProperty("inner").GetInt32());
    }
}