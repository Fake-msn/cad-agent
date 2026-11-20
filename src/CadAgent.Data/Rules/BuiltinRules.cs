using CadAgent.Core;

namespace CadAgent.Data.Rules;

/// <summary>
/// 内置规则（GB/T 50001-2017，7 条，逐字摘录自 extraction-50001.md，控制者已核）：
/// YAML 以 EmbeddedResource 随 Data 程序集分发（Rules/builtin/*.yaml）。
/// Load 读取全部嵌入资源并经 RuleLoader.LoadYaml 校验；按资源名 Ordinal 排序保证确定性顺序。
/// </summary>
public static class BuiltinRules
{
    private const string ResourcePrefix = "CadAgent.Data.Rules.builtin.";

    public static IReadOnlyList<Rule> Load()
    {
        var assembly = typeof(BuiltinRules).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".yaml", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
        var rules = new List<Rule>();
        foreach (string name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"嵌入资源缺失: {name}");
            using var reader = new StreamReader(stream);
            rules.Add(RuleLoader.LoadYaml(reader.ReadToEnd()));
        }
        return rules;
    }
}
