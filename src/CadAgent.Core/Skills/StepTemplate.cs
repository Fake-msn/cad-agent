using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadAgent.Core;

/// <summary>模板步骤：kind + params（值可为 "{{name}}" 占位字符串——仅整串精确匹配才替换）。</summary>
public sealed record TemplateStep(string Kind, Dictionary<string, object> Params);

/// <summary>步骤模板（steps/*.yaml）：名称、描述、参数名表、步骤序列。复杂流程经 skill 快速搭建的载体。</summary>
public sealed record StepTemplate(
    string Name,
    string Description,
    IReadOnlyList<string> ParamNames,
    IReadOnlyList<TemplateStep> Sequence);

/// <summary>
/// 模板实例化：ParamNames 全部必须在 args（缺→InvalidSkillException 列名）；
/// 字符串值整串精确 "{{name}}" → args[name] 原样透传（数值保持数值；数字形态字符串
/// 不做 coerce——类型校验在 StepPlanParser/引擎处理器下游）；未知占位符（不在
/// ParamNames，即便 args 带同名键）→InvalidSkillException。planId = "{name}-{Guid:N}"。
/// 不就地改写模板；替换单程（args 值本身含 "{{...}}" 不再递归）。
/// </summary>
public static class TemplateInstantiator
{
    private static readonly Regex PlaceholderPattern = new(@"^\{\{([^{}]+)\}\}$", RegexOptions.Compiled);

    public static StepPlan Instantiate(StepTemplate t, IReadOnlyDictionary<string, object> args)
    {
        var missing = t.ParamNames.Where(n => !args.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidSkillException(
                $"template '{t.Name}': missing args: {string.Join(", ", missing)}");

        var known = new HashSet<string>(t.ParamNames, StringComparer.Ordinal);
        var steps = new List<StepSpec>(t.Sequence.Count);
        foreach (var step in t.Sequence)
        {
            var substituted = new Dictionary<string, object>(step.Params.Count);
            foreach (var (key, value) in step.Params)
            {
                if (value is string s && PlaceholderPattern.Match(s) is { Success: true } m)
                {
                    var name = m.Groups[1].Value;
                    if (!known.Contains(name))
                        throw new InvalidSkillException(
                            $"template '{t.Name}': unknown placeholder '{{{{{name}}}}}' in step '{step.Kind}' param '{key}'");
                    substituted[key] = args[name];
                }
                else
                {
                    substituted[key] = value;
                }
            }
            steps.Add(new StepSpec(step.Kind, JsonSerializer.SerializeToElement(substituted)));
        }
        return new StepPlan($"{t.Name}-{Guid.NewGuid():N}", steps);
    }
}
