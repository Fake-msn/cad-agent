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
    // 整串精确锚定用 \z 而非 $：.NET 的 $ 也匹配末尾 \n 之前，会把 "{{x1}}\n"
    // （YAML literal 块标量可达形态）误判为精确占位。\z 钉死字符串绝对末尾。
    // 内含空白形态 "{{ x1 }}" 匹配占位形状但 name（" x1 "）不在 ParamNames →
    // 未知占位符拒绝——刻意的 strict 解读：占位符不做内空白修剪。
    private static readonly Regex PlaceholderPattern = new(@"^\{\{([^{}]+)\}\}\z", RegexOptions.Compiled);

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
                substituted[key] = SubstituteValue(value, args, known, t.Name, step.Kind, key);
            steps.Add(new StepSpec(step.Kind, JsonSerializer.SerializeToElement(substituted)));
        }
        return new StepPlan($"{t.Name}-{Guid.NewGuid():N}", steps);
    }

    /// <summary>
    /// 递归替换（计划 3a 嵌套 params 契约）：string 整串精确占位→args 原样透传；
    /// 映射（string 键或 YamlDotNet object 键）/列表递归处理；其他值原样。
    /// 未知占位符在任意深度拒绝。单程替换（args 值含占位不再递归）。
    /// </summary>
    private static object SubstituteValue(object value, IReadOnlyDictionary<string, object> args,
        HashSet<string> known, string templateName, string stepKind, string path)
    {
        switch (value)
        {
            case string s when PlaceholderPattern.Match(s) is { Success: true } m:
            {
                var name = m.Groups[1].Value;
                if (!known.Contains(name))
                    throw new InvalidSkillException(
                        $"template '{templateName}': unknown placeholder '{{{{{name}}}}}' in step '{stepKind}' param '{path}'");
                return args[name];
            }
            case IDictionary<string, object> map:
            {
                var result = new Dictionary<string, object>(map.Count);
                foreach (var (k, v) in map)
                    result[k] = SubstituteValue(v, args, known, templateName, stepKind, $"{path}.{k}");
                return result;
            }
            case IDictionary<object, object> objMap:
            {
                var result = new Dictionary<string, object>(objMap.Count);
                foreach (var (k, v) in objMap)
                    result[k.ToString() ?? ""] = SubstituteValue(v, args, known, templateName, stepKind, $"{path}.{k}");
                return result;
            }
            case IList<object> list:
            {
                var result = new List<object>(list.Count);
                for (int i = 0; i < list.Count; i++)
                    result.Add(SubstituteValue(list[i], args, known, templateName, stepKind, $"{path}[{i}]"));
                return result;
            }
            default:
                return value;
        }
    }
}
