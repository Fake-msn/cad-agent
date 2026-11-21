using System.Security.Cryptography;
using System.Text;

namespace CadAgent.Llm;

/// <summary>
/// 密钥保护器契约（Core 不引用 DPAPI——实现在 CadAgent.Llm，Windows 目标）。
/// </summary>
public interface IKeyProtector
{
    void Store(string profileName, string apiKey);
    string? Retrieve(string profileName);
    void Delete(string profileName);
}

/// <summary>
/// DPAPI CurrentUser 加密存储：每 profile 一个 {storeDir}/{sanitized}.key 文件。
/// 文件名 sanitize（无效字符→'_'）+ 穿越守卫（分隔符/.. → ArgumentException，同 SnapshotStore 模式）。
/// 密钥值永不进日志/ToString；存储文件非明文（测试钉死）。
/// </summary>
public sealed class DpapiKeyProtector : IKeyProtector
{
    private readonly string _storeDir;

    public DpapiKeyProtector(string storeDir) => _storeDir = storeDir;

    private string PathFor(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("profile 名不能为空", nameof(profileName));
        if (profileName.Contains('/') || profileName.Contains('\\') || profileName.Contains(".."))
            throw new ArgumentException($"profile 名不得含路径分隔符或 ..: {profileName}", nameof(profileName));
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(profileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_storeDir, sanitized + ".key");
    }

    public void Store(string profileName, string apiKey)
    {
        string path = PathFor(profileName);
        Directory.CreateDirectory(_storeDir);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, path, overwrite: true);
    }

    public string? Retrieve(string profileName)
    {
        string path = PathFor(profileName);
        if (!File.Exists(path)) return null;
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void Delete(string profileName)
    {
        string path = PathFor(profileName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>环境变量 CADAGENT_LLM_API_KEY 优先（脚本/CI 注入），否则走存储。</summary>
    public static string? ResolveKey(IKeyProtector protector, string profileName)
    {
        string? env = Environment.GetEnvironmentVariable("CADAGENT_LLM_API_KEY");
        if (!string.IsNullOrEmpty(env)) return env;
        return protector.Retrieve(profileName);
    }
}