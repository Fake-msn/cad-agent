namespace CadAgent.Core;

/// <summary>规则强制力：必须／应当／可（对应 必须/应/宜/可 的裁定分级）。</summary>
public enum RuleForce
{
    Mandatory,
    Recommended,
    Informational,
}

/// <summary>违规严重度。</summary>
public enum RuleSeverity
{
    High,
    Medium,
    Low,
}

/// <summary>规则出处引用：标准号、条款号、原文摘录、核验方式。</summary>
/// <param name="Verification">合法集：source-text / external-corroborated / pending-original。</param>
public sealed record RuleSource(string Standard, string Clause, string Excerpt, string Verification);

/// <summary>规则适用范围；any 表示不限。</summary>
public sealed record RuleScope(string Discipline = "any", string DrawingType = "any");

/// <summary>检查器规格。YAML 参数经 YamlDotNet 反序列化为 object——数值为 double/string/list，检查器自行转换。</summary>
public sealed record CheckSpec(string Type, Dictionary<string, object> Params);

/// <summary>一条规范规则：出处引用 + 强制力 + 严重度 + 适用范围 + 检查规格 + 整改建议。</summary>
public sealed record Rule(
    string Id,
    RuleSource Source,
    string Modal,
    RuleForce Force,
    RuleSeverity Severity,
    RuleScope Scope,
    CheckSpec Check,
    string Suggestion);

/// <summary>规则 YAML 非法（结构缺失、字段为空、枚举越界等）。</summary>
public sealed class InvalidRuleException : Exception
{
    public InvalidRuleException(string message) : base(message) { }
}