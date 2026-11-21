using System.Text.Json;

namespace CadAgent.Core.Llm;

/// <summary>数据出境决策（§8.5）。</summary>
public enum EgressDecision { Allowed, BlockedLocalOnly, NeedsFirstCloudConsent }

/// <summary>
/// 数据出境管控：项目 local-only.txt 硬约束覆盖一切；profile.LocalOnly 仅允许 localhost；
/// 云端 profile 首次发送图纸数据须显式确认（cloud-consent.json 持久化）；localhost 一等公民。
/// </summary>
public sealed class EgressPolicy
{
    private readonly string _projectDir;

    public EgressPolicy(string projectDir) => _projectDir = projectDir;

    private string LocalOnlyPath => Path.Combine(_projectDir, "local-only.txt");
    private string ConsentPath => Path.Combine(_projectDir, "cloud-consent.json");

    public bool ConsentGiven => File.Exists(ConsentPath);

    public EgressDecision Evaluate(ProviderProfile profile, bool drawingData)
    {
        if (File.Exists(LocalOnlyPath)) return EgressDecision.BlockedLocalOnly;
        bool local = IsLocalhost(profile.Endpoint);
        if (profile.DataPolicy == DataPolicy.LocalOnly && !local) return EgressDecision.BlockedLocalOnly;
        if (drawingData && profile.DataPolicy == DataPolicy.CloudAllowed && !local && !ConsentGiven)
            return EgressDecision.NeedsFirstCloudConsent;
        return EgressDecision.Allowed;
    }

    public void ConfirmCloudConsent()
    {
        Directory.CreateDirectory(_projectDir);
        string json = JsonSerializer.Serialize(new { consentedAt = DateTimeOffset.UtcNow.ToString("O") }) + "\n";
        string tmp = ConsentPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConsentPath, overwrite: true);
    }

    private static bool IsLocalhost(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "::1";
    }
}