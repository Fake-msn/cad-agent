using System.Text.Json;
using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Cli;

/// <summary>
/// llm 命令族（§8.3）：add/list/use/remove/test。
/// 密钥硬规则：DPAPI 存储（.cadagent/keys/），配置 JSON 无密钥，审计 paramsJson 无密钥，
/// 错误消息脱敏。test 经 LlmClientFactory 可注入（测试 mock；生产 OpenAiClient）。
/// </summary>
public static partial class CommandRouter
{
    /// <summary>测试注入点：(profile, resolvedKey) → ILlmClient；null → 生产 OpenAiClient。</summary>
    internal static Func<ProviderProfile, string, ILlmClient>? LlmClientFactory;

    private static int Llm(string[] args, string[] positional, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2)
        {
            stderr.WriteLine("用法: llm <add|list|use|remove|test> [选项]");
            return 1;
        }
        string configPath = Path.Combine(agentDir, "llm-profiles.json");
        string keysDir = Path.Combine(agentDir, "keys");
        var store = LlmConfigStore.Load(configPath);
        var protector = new DpapiKeyProtector(keysDir);
        var op = Identity(agentDir).Current;

        switch (positional[1])
        {
            case "add":
            {
                string name = RequireOption(args, "--name");
                string endpoint = RequireOption(args, "--endpoint");
                string model = RequireOption(args, "--model");
                bool vision = args.Contains("--vision");
                string policyRaw = GetOption(args, "--data-policy") ?? "cloud";
                DataPolicy policy = policyRaw.Equals("local", StringComparison.OrdinalIgnoreCase)
                    ? DataPolicy.LocalOnly : DataPolicy.CloudAllowed;
                var profile = new ProviderProfile(name, endpoint, model, vision, policy, new ProfileLimits());
                try { ProfileValidator.Validate(profile); }
                catch (InvalidProfileException ex) { stderr.WriteLine(ex.Message); return 1; }
                string? key = GetOption(args, "--key");
                // 合并前审查 Warning#3：--key-stdin 从标准输入读密钥（不进 shell history/进程列表）；
                // --key 保留便利路径（暴露面文档注记）。两者互斥时 stdin 优先。
                if (args.Contains("--key-stdin"))
                {
                    key = Console.ReadLine();
                    if (string.IsNullOrEmpty(key)) { stderr.WriteLine("--key-stdin 未读到密钥"); return 1; }
                }
                if (!string.IsNullOrEmpty(key)) protector.Store(name, key);
                store.Upsert(profile);
                store.Save();
                if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !IsLocalhostEndpoint(endpoint))
                    stderr.WriteLine("警示: 非 localhost 的 http:// 端点不加密传输（本地模型除外建议 https）");
                using (var audit = OpenAudit(agentDir))
                    audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                        "llm", "llm-profile-added", name,
                        JsonSerializer.Serialize(new { name, endpoint, model, vision, dataPolicy = policy.ToString() }),
                        "", "", "", ""));
                stdout.WriteLine($"已添加 profile: {name}");
                return 0;
            }
            case "list":
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    profiles = store.Profiles.Select(p => new
                    {
                        p.Name, p.Endpoint, p.Model, p.VisionCapable, dataPolicy = p.DataPolicy.ToString()
                    }),
                    activeProfileName = store.ActiveProfileName
                }, SemanticJsonOpts));
                return 0;
            }
            case "use":
            {
                string name = RequireOption(args, "--name");
                try { store.SetActive(name); }
                catch (InvalidProfileException ex) { stderr.WriteLine(ex.Message); return 1; }
                store.Save();
                using (var audit = OpenAudit(agentDir))
                    audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                        "llm", "llm-profile-used", name,
                        JsonSerializer.Serialize(new { name }), "", "", "", ""));
                stdout.WriteLine($"活动 profile: {name}");
                return 0;
            }
            case "remove":
            {
                string name = RequireOption(args, "--name");
                store.Remove(name);
                protector.Delete(name);
                store.Save();
                using (var audit = OpenAudit(agentDir))
                    audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                        "llm", "llm-profile-removed", name,
                        JsonSerializer.Serialize(new { name }), "", "", "", ""));
                stdout.WriteLine($"已删除 profile: {name}");
                return 0;
            }
            case "test":
            {
                string? name = GetOption(args, "--name") ?? store.ActiveProfileName;
                if (name == null) { stderr.WriteLine("无活动 profile，请先 llm use 或 --name 指定"); return 1; }
                var profile = store.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (profile == null) { stderr.WriteLine($"profile 不存在: {name}"); return 1; }
                string? key = DpapiKeyProtector.ResolveKey(protector, profile.Name);
                if (string.IsNullOrEmpty(key)) { stderr.WriteLine("无可用密钥（--key 存储或 CADAGENT_LLM_API_KEY 环境变量）"); return 1; }
                ILlmClient client = LlmClientFactory?.Invoke(profile, key) ?? new OpenAiClient(profile, key);
                var result = new LlmProbe(client).RunAsync(args.Contains("--vision")).GetAwaiter().GetResult();
                using (var audit = OpenAudit(agentDir))
                    audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                        "llm", "llm-probe", profile.Name,
                        JsonSerializer.Serialize(new
                        {
                            name = profile.Name, result.Connected, result.CompletionOk, result.VisionOk,
                            usage = result.Usage == null ? null : new { result.Usage.PromptTokens, result.Usage.CompletionTokens }
                        }), "", "", "", ""));
                stdout.WriteLine(JsonSerializer.Serialize(result, SemanticJsonOpts));
                return result.Connected && result.CompletionOk ? 0 : 1;
            }
            default:
                stderr.WriteLine($"未知 llm 子命令: {positional[1]}（add|list|use|remove|test）");
                return 1;
        }
    }

    private static bool IsLocalhostEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "::1";
    }
}