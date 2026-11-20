namespace CadAgent.Core;

/// <summary>
/// 内置规则 + skill 包的合并结果：全局规则 Id 冲突守卫（builtin×skill 或 skill×skill →
/// InvalidSkillException，消息列冲突 Id 与双方来源名）。builtin 规则由调用方传入
/// （BuiltinRules.Load 在 Data——Core 不引用 Data）。规则序 = builtin 序 + 包序（确定性）；
/// 别名 OrdinalIgnoreCase 合并，后包覆盖前包同层名；版本号 = 内置常量 + "|name@version" 按包序拼接。
/// </summary>
public sealed class SkillSet
{
    private const string BuiltinRulePackVersion = "gb50001-2017.r1";
    private const string BuiltinSourceName = "builtin";

    private readonly HashSet<string> _ruleIds;

    public IReadOnlyList<Rule> AllRules { get; }
    public IReadOnlyDictionary<string, SemanticClass> MergedAliases { get; }
    public string RulePackVersion { get; }

    private SkillSet(
        IReadOnlyList<Rule> allRules,
        IReadOnlyDictionary<string, SemanticClass> mergedAliases,
        string rulePackVersion)
    {
        AllRules = allRules;
        MergedAliases = mergedAliases;
        RulePackVersion = rulePackVersion;
        _ruleIds = new HashSet<string>(allRules.Select(r => r.Id), StringComparer.Ordinal);
    }

    public static SkillSet Compose(IReadOnlyList<Rule> builtinRules, IReadOnlyList<SkillPackage> packages)
    {
        var allRules = new List<Rule>();
        var idSources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rule in builtinRules)
        {
            AddRule(allRules, idSources, rule, BuiltinSourceName);
        }

        var aliases = new Dictionary<string, SemanticClass>(StringComparer.OrdinalIgnoreCase);
        var version = BuiltinRulePackVersion;
        foreach (var package in packages)
        {
            foreach (var rule in package.Rules)
            {
                AddRule(allRules, idSources, rule, package.Manifest.Name);
            }

            // 后包覆盖前包同层名（OrdinalIgnoreCase）——包序即传入序，确定性。
            foreach (var alias in package.Aliases)
            {
                aliases[alias.LayerName] = alias.Class;
            }

            version += $"|{package.Manifest.Name}@{package.Manifest.Version}";
        }

        return new SkillSet(allRules, aliases, version);
    }

    public bool ContainsRule(string id) => _ruleIds.Contains(id);

    private static void AddRule(List<Rule> allRules, Dictionary<string, string> idSources, Rule rule, string sourceName)
    {
        if (idSources.TryGetValue(rule.Id, out var existingSource))
            throw new InvalidSkillException(
                $"duplicate rule id '{rule.Id}': defined in both '{existingSource}' and '{sourceName}'");
        idSources.Add(rule.Id, sourceName);
        allRules.Add(rule);
    }
}
