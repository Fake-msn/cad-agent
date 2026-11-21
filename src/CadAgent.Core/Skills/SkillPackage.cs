using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CadAgent.Core;

/// <summary>图层别名：院标图层名 → 语义类别（aliases.yaml，class 枚举 ignoreCase）。</summary>
public sealed record SkillAlias(string LayerName, SemanticClass Class);

/// <summary>一个已加载的 skill 包：清单 + 规则 + 别名 + 步骤模板 + 符号元数据 + 来源目录。支持对象初始化器构造。</summary>
public sealed class SkillPackage
{
    public required SkillManifest Manifest { get; init; }
    public IReadOnlyList<Rule> Rules { get; init; } = Array.Empty<Rule>();
    public IReadOnlyList<SkillAlias> Aliases { get; init; } = Array.Empty<SkillAlias>();
    public IReadOnlyList<StepTemplate> Steps { get; init; } = Array.Empty<StepTemplate>();
    public IReadOnlyList<SymbolMeta> Symbols { get; init; } = Array.Empty<SymbolMeta>();
    public required string SourceDir { get; init; }
}

/// <summary>
/// skill 目录加载：skill.yaml 必需（strict YAML，未知键拒绝，沿用 RoleConfig 先例）；
/// rules/*.yaml 经 RuleLoader.LoadYaml（包内 Id 重复拒绝）；aliases.yaml 可选；
/// steps/*.yaml 可选（2b-ii-b Task 7：模板 schema strict，包内模板名重复拒绝）；
/// symbols/symbols.yaml 可选（2b-ii-b Task 9：元数据 strict，包内符号名重复拒绝；
/// 文件存在性不强制——跨文档导入计划 3）。异常带文件名并保留内层异常。
/// </summary>
public static class SkillLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

    // steps 专用：plain（无引号）数值/布尔标量保持数值类型——params 字面量 y1: 0
    // 实例化后须为 JSON number（引擎处理器 GetDouble 不接受字符串）；引号标量仍为字符串。
    private static readonly IDeserializer StepsDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithNodeTypeResolver(new PlainScalarTypeResolver())
        .Build();

    public static SkillPackage LoadDirectory(string dir)
    {
        var manifestPath = Path.Combine(dir, "skill.yaml");
        if (!File.Exists(manifestPath))
            throw new InvalidSkillException($"skill.yaml not found in '{dir}'");

        var manifest = LoadManifest(manifestPath);
        var rules = LoadRules(Path.Combine(dir, "rules"));
        var aliases = LoadAliases(Path.Combine(dir, "aliases.yaml"));
        var steps = LoadSteps(Path.Combine(dir, "steps"));
        var symbols = LoadSymbols(Path.Combine(dir, "symbols", "symbols.yaml"));

        return new SkillPackage
        {
            Manifest = manifest,
            Rules = rules,
            Aliases = aliases,
            Steps = steps,
            Symbols = symbols,
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

    private static IReadOnlyList<StepTemplate> LoadSteps(string stepsDir)
    {
        if (!Directory.Exists(stepsDir))
            return Array.Empty<StepTemplate>();

        var templates = new List<StepTemplate>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(stepsDir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            StepsFileDto? dto;
            try
            {
                dto = StepsDeserializer.Deserialize<StepsFileDto>(File.ReadAllText(path));
            }
            catch (YamlException ex)
            {
                throw new InvalidSkillException($"{fileName}: {ex.Message}", ex);
            }

            if (dto?.Templates is null)
                continue;

            foreach (var t in dto.Templates)
            {
                if (string.IsNullOrWhiteSpace(t.Name))
                    throw new InvalidSkillException($"{fileName}: template name must not be empty");
                if (!seenNames.Add(t.Name!))
                    throw new InvalidSkillException(
                        $"{fileName}: duplicate template name '{t.Name}' in skill package");

                var paramNames = t.Params ?? new List<string>();
                var seenParams = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in paramNames)
                {
                    if (string.IsNullOrWhiteSpace(p) || !seenParams.Add(p))
                        throw new InvalidSkillException(
                            $"{fileName}: template '{t.Name}' has empty or duplicate param name '{p}'");
                }

                if (t.Sequence is null || t.Sequence.Count == 0)
                    throw new InvalidSkillException(
                        $"{fileName}: template '{t.Name}' sequence must not be empty");
                var sequence = new List<TemplateStep>(t.Sequence.Count);
                foreach (var s in t.Sequence)
                {
                    if (string.IsNullOrWhiteSpace(s.Kind))
                        throw new InvalidSkillException(
                            $"{fileName}: template '{t.Name}' step kind must not be empty");
                    sequence.Add(new TemplateStep(s.Kind!, s.Params ?? new Dictionary<string, object>()));
                }

                templates.Add(new StepTemplate(t.Name!, t.Description ?? "", paramNames, sequence));
            }
        }
        return templates;
    }

    private static IReadOnlyList<SymbolMeta> LoadSymbols(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<SymbolMeta>();

        SymbolsFileDto? dto;
        try
        {
            dto = Deserializer.Deserialize<SymbolsFileDto>(File.ReadAllText(path));
        }
        catch (YamlException ex)
        {
            throw new InvalidSkillException($"symbols.yaml: {ex.Message}", ex);
        }

        if (dto?.Symbols is null)
            return Array.Empty<SymbolMeta>();

        var symbols = new List<SymbolMeta>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dto.Symbols)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                throw new InvalidSkillException("symbols.yaml: name must not be empty");
            if (string.IsNullOrWhiteSpace(entry.File))
                throw new InvalidSkillException("symbols.yaml: file must not be empty");
            // 全数字串拒绝（2b-ii-b Task 2，strict parity）：Enum.TryParse 会把 "3"
            // 解析为已定义数值——class 只接受枚举名（ignoreCase），先于 parse 拒绝。
            if (string.IsNullOrWhiteSpace(entry.Class) ||
                entry.Class.All(char.IsAsciiDigit) ||
                !Enum.TryParse<SemanticClass>(entry.Class, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
                throw new InvalidSkillException(
                    $"symbols.yaml: class '{entry.Class}' is not a legal SemanticClass value");
            // 包内符号名重复拒绝（OrdinalIgnoreCase，与 aliases layerName 同口径）。
            if (!seenNames.Add(entry.Name!))
                throw new InvalidSkillException(
                    $"symbols.yaml: duplicate symbol name '{entry.Name}' in skill package");
            // 文件存在性不强制——跨文档符号导入留计划 3（第一 slice 仅元数据校验）。
            symbols.Add(new SymbolMeta(entry.Name!, entry.File!, parsed, entry.Description ?? ""));
        }
        return symbols;
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

    private sealed class StepsFileDto
    {
        public List<TemplateDto>? Templates { get; set; }
    }

    private sealed class TemplateDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Params { get; set; }
        public List<TemplateStepDto>? Sequence { get; set; }
    }

    private sealed class TemplateStepDto
    {
        public string? Kind { get; set; }
        public Dictionary<string, object>? Params { get; set; }
    }

    private sealed class SymbolsFileDto
    {
        public List<SymbolDto>? Symbols { get; set; }
    }

    private sealed class SymbolDto
    {
        public string? Name { get; set; }
        public string? File { get; set; }
        public string? Class { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// 仅对 object 目标的 plain（无引号）标量推断 bool/long/double；引号/块标量与
    /// 非 object 目标（string 属性等）不介入。steps params 数值 literal 由此保持数值。
    /// </summary>
    private sealed class PlainScalarTypeResolver : INodeTypeResolver
    {
        public bool Resolve(NodeEvent? nodeEvent, ref Type currentType)
        {
            if (currentType != typeof(object) ||
                nodeEvent is not Scalar scalar ||
                scalar.Style is not (ScalarStyle.Any or ScalarStyle.Plain))
                return false;

            var value = scalar.Value;
            if (bool.TryParse(value, out _))
            {
                currentType = typeof(bool);
                return true;
            }
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                currentType = typeof(long);
                return true;
            }
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                double.IsFinite(d))
            {
                currentType = typeof(double);
                return true;
            }
            return false;
        }
    }
}
