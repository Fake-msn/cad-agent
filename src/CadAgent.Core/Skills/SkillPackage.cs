using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CadAgent.Core;

/// <summary>图层别名：院标图层名 → 语义类别（aliases.yaml，class 枚举 ignoreCase）。</summary>
public sealed record SkillAlias(string LayerName, SemanticClass Class);

/// <summary>一个已加载的 skill 包：清单 + 规则 + 别名 + 来源目录。支持对象初始化器构造。</summary>
public sealed class SkillPackage
{
    public required SkillManifest Manifest { get; init; }
    public IReadOnlyList<Rule> Rules { get; init; } = Array.Empty<Rule>();
    public IReadOnlyList<SkillAlias> Aliases { get; init; } = Array.Empty<SkillAlias>();
    public required string SourceDir { get; init; }
}

/// <summary>
/// skill 目录加载：skill.yaml 必需（strict YAML，未知键拒绝，沿用 RoleConfig 先例）；
/// rules/*.yaml 经 RuleLoader.LoadYaml（包内 Id 重复拒绝）；aliases.yaml 可选；
/// steps/symbols 子目录容忍不解析（2b-ii-b）。异常带文件名并保留内层异常。
/// </summary>
public static class SkillLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    public static SkillPackage LoadDirectory(string dir)
    {
        var manifestPath = Path.Combine(dir, "skill.yaml");
        if (!File.Exists(manifestPath))
            throw new InvalidSkillException($"skill.yaml not found in '{dir}'");

        var manifest = LoadManifest(manifestPath);
        var rules = LoadRules(Path.Combine(dir, "rules"));
        var aliases = LoadAliases(Path.Combine(dir, "aliases.yaml"));

        return new SkillPackage
        {
            Manifest = manifest,
            Rules = rules,
            Aliases = aliases,
            SourceDir = dir,
        };
    }

    private static SkillManifest LoadManifest(string path)
    {
        ManifestDto? dto;
        try
        {
            dto = Deserializer.Deserialize<ManifestDto>(File.ReadAllText(path));
        }
        catch (YamlException ex)
        {
            throw new InvalidSkillException($"skill.yaml: {ex.Message}", ex);
        }

        if (dto is null)
            throw new InvalidSkillException("skill.yaml: empty document");
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new InvalidSkillException("skill.yaml: name must not be empty");
        if (string.IsNullOrWhiteSpace(dto.Version))
            throw new InvalidSkillException("skill.yaml: version must not be empty");
        if (!VersionPattern.IsMatch(dto.Version!))
            throw new InvalidSkillException(
                $"skill.yaml: version '{dto.Version}' must match digits.digits.digits (e.g. 1.0.0)");

        return new SkillManifest(
            dto.Name!,
            dto.Version!,
            dto.Discipline ?? "",
            dto.DrawingType ?? "",
            dto.Description ?? "");
    }

    private static IReadOnlyList<Rule> LoadRules(string rulesDir)
    {
        if (!Directory.Exists(rulesDir))
            return Array.Empty<Rule>();

        var rules = new List<Rule>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(rulesDir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            Rule rule;
            try
            {
                rule = RuleLoader.LoadYaml(File.ReadAllText(path));
            }
            catch (InvalidRuleException ex)
            {
                throw new InvalidSkillException($"{fileName}: {ex.Message}", ex);
            }

            if (!seenIds.Add(rule.Id))
                throw new InvalidSkillException($"{fileName}: duplicate rule id '{rule.Id}' in skill package");
            rules.Add(rule);
        }
        return rules;
    }

    private static IReadOnlyList<SkillAlias> LoadAliases(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<SkillAlias>();

        AliasesFileDto? dto;
        try
        {
            dto = Deserializer.Deserialize<AliasesFileDto>(File.ReadAllText(path));
        }
        catch (YamlException ex)
        {
            throw new InvalidSkillException($"aliases.yaml: {ex.Message}", ex);
        }

        if (dto?.Aliases is null)
            return Array.Empty<SkillAlias>();

        var aliases = new List<SkillAlias>();
        var seenLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dto.Aliases)
        {
            if (string.IsNullOrWhiteSpace(entry.LayerName))
                throw new InvalidSkillException("aliases.yaml: layerName must not be empty");
            // 全数字串拒绝（2b-ii-b Task 2，strict parity）：Enum.TryParse 会把 "3"
            // 解析为已定义数值——class 只接受枚举名（ignoreCase），先于 parse 拒绝。
            if (string.IsNullOrWhiteSpace(entry.Class) ||
                entry.Class.All(char.IsAsciiDigit) ||
                !Enum.TryParse<SemanticClass>(entry.Class, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
                throw new InvalidSkillException(
                    $"aliases.yaml: class '{entry.Class}' is not a legal SemanticClass value");
            // 包内 layerName 重复拒绝（OrdinalIgnoreCase，与 promote upsert 同口径）。
            if (!seenLayers.Add(entry.LayerName!))
                throw new InvalidSkillException(
                    $"aliases.yaml: duplicate layerName '{entry.LayerName}' in skill package");
            aliases.Add(new SkillAlias(entry.LayerName!, parsed));
        }
        return aliases;
    }

    private sealed class ManifestDto
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Discipline { get; set; }
        public string? DrawingType { get; set; }
        public string? Description { get; set; }
    }

    private sealed class AliasesFileDto
    {
        public List<AliasDto>? Aliases { get; set; }
    }

    private sealed class AliasDto
    {
        public string? LayerName { get; set; }
        public string? Class { get; set; }
    }
}
