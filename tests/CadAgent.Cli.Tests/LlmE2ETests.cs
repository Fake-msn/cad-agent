using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// LLM 基础设施 T8 E2E（mock HTTP 工厂，全离线）：
/// add(key)→list(无泄漏)→use→test(补全OK+usage)→审计链绿(三类事件无key)→remove(key文件删)
/// →配置全文扫描无key明文→env override 路径。
/// </summary>
public class LlmE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"llme2e-{Guid.NewGuid():N}");
    public LlmE2ETests() => Directory.CreateDirectory(_project);
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
    private const string Key = "sk-e2e-secret-987654321";

    private sealed class FakeClient : ILlmClient
    {
        public string? UsedKey;
        public FakeClient(string key) => UsedKey = key;
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse("OK", new TokenUsage(7, 3), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void FullLifecycle_KeySafe_EndToEnd()
    {
        // add with key
        var (addCode, _, addErr) = Run("llm", "add", "--name", "主力", "--endpoint", "https://api.test/v1",
            "--model", "gpt-4o", "--vision", "--data-policy", "cloud", "--key", Key);
        Assert.Equal(0, addCode);
        Assert.DoesNotContain(Key, addErr);

        // list — no leakage
        var (listCode, listOut, _) = Run("llm", "list");
        Assert.Equal(0, listCode);
        Assert.Contains("主力", listOut);
        Assert.DoesNotContain(Key, listOut);

        // use
        Assert.Equal(0, Run("llm", "use", "--name", "主力").code);

        // test via mock factory — completion OK + usage
        CommandRouter.LlmClientFactory = (p, k) => new FakeClient(k);
        var (testCode, testOut, _) = Run("llm", "test");
        Assert.Equal(0, testCode);
        Assert.Contains("\"connected\":true", testOut.Replace(" ", ""));
        Assert.DoesNotContain(Key, testOut);

        // audit chain green with all three event kinds, key-free
        using (var audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db")))
        {
            var all = audit.ReadAll();
            Assert.Contains(all, r => r.Entry.Kind == "llm-profile-added");
            Assert.Contains(all, r => r.Entry.Kind == "llm-profile-used");
            Assert.Contains(all, r => r.Entry.Kind == "llm-probe");
            Assert.All(all, r => Assert.DoesNotContain(Key, r.Entry.ParamsJson));
            Assert.True(audit.VerifyChain(out _));
        }

        // config full-text scan — no plaintext key anywhere under .cadagent except encrypted key file
        foreach (string f in Directory.GetFiles(AgentDir, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".key")) continue; // DPAPI 密文（非明文断言在 KeyProtectorTests）
            if (f.EndsWith(".db") || f.EndsWith("-wal") || f.EndsWith("-shm")) continue;
            Assert.DoesNotContain(Key, File.ReadAllText(f));
        }

        // remove — key file deleted
        Assert.Equal(0, Run("llm", "remove", "--name", "主力").code);
        Assert.Empty(Directory.GetFiles(Path.Combine(AgentDir, "keys")));

        // env override path: add without key, env provides it
        Assert.Equal(0, Run("llm", "add", "--name", "env档案", "--endpoint", "https://api.test/v1", "--model", "m").code);
        Assert.Equal(0, Run("llm", "use", "--name", "env档案").code);
        Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", "sk-env-override-123456");
        string? capturedKey = null;
        CommandRouter.LlmClientFactory = (p, k) => { capturedKey = k; return new FakeClient(k); };
        Assert.Equal(0, Run("llm", "test").code);
        Assert.Equal("sk-env-override-123456", capturedKey);
    }
}