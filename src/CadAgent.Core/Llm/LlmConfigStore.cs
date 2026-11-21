using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadAgent.Core;

/// <summary>
/// LLM 配置存储（llm.json）：档案列表 + 活动档案名。JSON 缩进、camelCase、枚举字符串；
/// 只存 ProviderProfile 纯模型字段——无任何密钥字段（密钥走 DPAPI/env，spec 钉死）。
/// Save 原子写（tmp + Move，同 SemanticOverlay 模式）；Load 缺失文件 → 空存储。
/// </summary>
public sealed class LlmConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;
    private readonly List<ProviderProfile> _profiles = new();

    public LlmConfigStore(string configPath)
    {
        _configPath = configPath;
    }

    public IReadOnlyList<ProviderProfile> Profiles => _profiles;

    public string? ActiveProfileName { get; private set; }

    /// <summary>新增或同名替换（OrdinalIgnoreCase）；先经 ProfileValidator 校验。</summary>
    public void Upsert(ProviderProfile p)
    {
        ProfileValidator.Validate(p);
        int i = _profiles.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _profiles[i] = p; else _profiles.Add(p);
    }

    /// <summary>按名删除（OrdinalIgnoreCase）；删除的若是活动档案则清空活动名。不存在时静默。</summary>
    public void Remove(string name)
    {
        int i = _profiles.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        if (string.Equals(_profiles[i].Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            ActiveProfileName = null;
        _profiles.RemoveAt(i);
    }

    /// <summary>设置活动档案（OrdinalIgnoreCase 匹配，存规范名）；不存在 → InvalidProfileException。</summary>
    public void SetActive(string name)
    {
        ProviderProfile? p = _profiles.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null)
            throw new InvalidProfileException($"活动档案不存在: {name}");
        ActiveProfileName = p.Name;
    }

    /// <summary>原子写：先完整写同目录 tmp 再 Move 覆盖——中途崩溃磁盘旧文件完整。</summary>
    public void Save()
    {
        var doc = new ConfigDoc { Profiles = _profiles, ActiveProfileName = ActiveProfileName };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_configPath))!);
        string json = JsonSerializer.Serialize(doc, JsonOpts) + "\n";
        string tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _configPath, overwrite: true);
    }

    /// <summary>加载配置；文件缺失 → 空存储（不抛异常）。</summary>
    public static LlmConfigStore Load(string path)
    {
        var store = new LlmConfigStore(path);
        if (File.Exists(path))
        {
            ConfigDoc? doc = JsonSerializer.Deserialize<ConfigDoc>(File.ReadAllText(path), JsonOpts);
            if (doc is not null)
            {
                store._profiles.AddRange(doc.Profiles);
                store.ActiveProfileName = doc.ActiveProfileName;
            }
        }
        return store;
    }

    /// <summary>持久化形状：仅档案与活动名，无密钥字段。</summary>
    private sealed class ConfigDoc
    {
        public List<ProviderProfile> Profiles { get; set; } = new();
        public string? ActiveProfileName { get; set; }
    }
}
