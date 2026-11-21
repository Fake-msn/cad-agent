using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// LLM 基础设施 T7：llm add/list/use/remove/test 全链 + 审计无密钥 + mock 工厂注入。
/// </summary>
public class LlmCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"llmcli-{Guid.NewGuid():N}");
    public LlmCommandTests() => Directory.CreateDirectory(_project);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", null);
        Directory.Delete(_project, true);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private string AgentDir => Path.Combine(_project, ".cadagent");

    [Fact]
    public void Add_List_Use_Remove_Chain_AuditKeyFree()
    {
        var (addCode, _, addErr) = Run("llm", "add", "--name", "云端", "--endpoint", "https://api.test/v1",
            "--model", "gpt-4o", "--vision", "--key", "sk-supersecret123");
        Assert.Equal(0, addCode);
        Assert.DoesNotContain("sk-supersecret123", addErr);

        var (listCode, listOut, _) = Run("llm", "list");
        Assert.Equal(0, listCode);
        Assert.Contains("云端", listOut);
        Assert.DoesNotContain("sk-supersecret123", listOut);

        var (useCode, _, _) = Run("llm", "use", "--name", "云端");
        Assert.Equal(0, useCode);
        var (_, listOut2, _) = Run("llm", "list");
        Assert.Contains("云端", listOut2);

        // 配置文件全文无密钥明文
        string config = File.ReadAllText(Path.Combine(AgentDir, "llm-profiles.json"));
        Assert.DoesNotContain("sk-supersecret123", config);
        // DPAPI 密文存在
        Assert.True(Directory.GetFiles(Path.Combine(AgentDir, "keys")).Length == 1);

        // 审计无密钥
        using (var audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db")))
        {
            Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-supersecret123", r.Entry.ParamsJson));
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "llm-profile-added");
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "llm-profile-used");
            Assert.True(audit.VerifyChain(out _));
        }

        var (rmCode, _, _) = Run("llm", "remove", "--name", "云端");
        Assert.Equal(0, rmCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(AgentDir, "keys")));
        var (_, listOut3, _) = Run("llm", "list");
        Assert.DoesNotContain("云端", listOut3);
    }

    [Fact]
    public void Add_InvalidEndpoint_ExitOne()
    {
        var (code, _, _) = Run("llm", "add", "--name", "bad", "--endpoint", "not-a-url", "--model", "m");
        Assert.Equal(1, code);
    }

    [Fact]
    public void Add_InsecureHttp_Warns()
    {
        var (code, _, stderr) = Run("llm", "add", "--name", "远端", "--endpoint", "http://remote.test/v1", "--model", "m");
        Assert.Equal(0, code);
        Assert.Contains("http", stderr);
    }

    [Fact]
    public void Test_MockFactory_ProbeOk_Audited()
    {
        Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1", "--model", "m", "--key", "sk-testkey12345678");
        Run("llm", "use", "--name", "p");
        CommandRouter.LlmClientFactory = (profile, key) => new FakeLlmClient();
        var (code, stdout, _) = Run("llm", "test");
        Assert.Equal(0, code);
        Assert.Contains("connected", stdout.ToLower());
        using var audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "llm-probe");
        Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-testkey12345678", r.Entry.ParamsJson));
    }

    [Fact]
    public void Test_NoKey_ExitOne()
    {
        Run("llm", "add", "--name", "nokey", "--endpoint", "https://api.test/v1", "--model", "m");
        Run("llm", "use", "--name", "nokey");
        var (code, _, stderr) = Run("llm", "test");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Test_EnvKeyOverride_Works()
    {
        Run("llm", "add", "--name", "envp", "--endpoint", "https://api.test/v1", "--model", "m");
        Run("llm", "use", "--name", "envp");
        Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", "sk-envkey12345678");
        CommandRouter.LlmClientFactory = (profile, key) =>
        {
            Assert.Equal("sk-envkey12345678", key);
            return new FakeLlmClient();
        };
        var (code, _, _) = Run("llm", "test");
        Assert.Equal(0, code);
    }

    [Fact]
    public void UnknownSubcommand_ExitOne()
    {
        var (code, _, _) = Run("llm", "bogus");
        Assert.Equal(1, code);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse("OK", new TokenUsage(3, 2), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }
}