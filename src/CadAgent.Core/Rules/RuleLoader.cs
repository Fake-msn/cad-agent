using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CadAgent.Core;

/// <summary>规则 YAML 加载与校验：Id 非空、Source 四字段非空、Verification 合法集、Force/Severity 合法、Check.Type 非空。</summary>
public static class RuleLoader
{
    private static readonly HashSet<string> LegalVerifications = new(StringComparer.Ordinal)
    {
        "source-text",
        "external-corroborated",
        "pending-original",
    };

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static Rule LoadYaml(string yaml)
    {
        RuleDto? dto;
        try
        {
            dto = Deserializer.Deserialize<RuleDto>(yaml);
        }
        catch (YamlException ex)
        {
            throw new InvalidRuleException($"invalid rule YAML: {ex.Message}", ex);
        }

        if (dto is null)
            throw new InvalidRuleException("invalid rule YAML: empty document");
        return Validate(dto);
    }

    public static IReadOnlyList<Rule> LoadDirectory(string dir)
    {
        var rules = new List<Rule>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                rules.Add(LoadYaml(File.ReadAllText(path)));
            }
            catch (InvalidRuleException ex)
            {
                throw new InvalidRuleException($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return rules;
    }

    private static Rule Validate(RuleDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new InvalidRuleException("id must not be empty");

        var src = dto.Source ?? throw new InvalidRuleException("source is required (standard/clause/excerpt/verification)");
        Require(src.Standard, "source.standard");
        Require(src.Clause, "source.clause");
        Require(src.Excerpt, "source.excerpt");
        Require(src.Verification, "source.verification");
        if (!LegalVerifications.Contains(src.Verification!))
            throw new InvalidRuleException(
                $"source.verification '{src.Verification}' is not one of {{source-text, external-corroborated, pending-original}}");

        var force = ParseEnum<RuleForce>(dto.Force, "force");
        var severity = ParseEnum<RuleSeverity>(dto.Severity, "severity");

        var check = dto.Check ?? throw new InvalidRuleException("check is required");
        Require(check.Type, "check.type");

        var scope = dto.Scope is null
            ? new RuleScope()
            : new RuleScope(
                string.IsNullOrWhiteSpace(dto.Scope.Discipline) ? "any" : dto.Scope.Discipline!,
                string.IsNullOrWhiteSpace(dto.Scope.DrawingType) ? "any" : dto.Scope.DrawingType!);

        return new Rule(
            dto.Id!,
            new RuleSource(src.Standard!, src.Clause!, src.Excerpt!, src.Verification!),
            dto.Modal ?? "",
            force,
            severity,
            scope,
            new CheckSpec(check.Type!, check.Params ?? new Dictionary<string, object>()),
            dto.Suggestion ?? "");
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidRuleException($"{field} must not be empty");
    }

    private static T ParseEnum<T>(string? value, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
            throw new InvalidRuleException($"{field} '{value}' is not a legal {typeof(T).Name} value");
        return parsed;
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public SourceDto? Source { get; set; }
        public string? Modal { get; set; }
        public string? Force { get; set; }
        public string? Severity { get; set; }
        public ScopeDto? Scope { get; set; }
        public CheckDto? Check { get; set; }
        public string? Suggestion { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Standard { get; set; }
        public string? Clause { get; set; }
        public string? Excerpt { get; set; }
        public string? Verification { get; set; }
    }

    private sealed class ScopeDto
    {
        public string? Discipline { get; set; }
        public string? DrawingType { get; set; }
    }

    private sealed class CheckDto
    {
        public string? Type { get; set; }
        public Dictionary<string, object>? Params { get; set; }
    }
}
