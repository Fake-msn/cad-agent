using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3h T2：insert-symbol CLI 接线——run/agent 带 --skill 注入导入处理器；
/// 无 --skill 时 kind 不注册→响亮失败（不静默半执行）。
/// </summary>
public class SymbolImportCliTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"symimp-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _plan;

    public SymbolImportCliTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(_ => { });
        _plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(_plan,
            "{\"planId\":\"IMP-TEST-1\",\"steps\":[{\"kind\":\"insert-symbol\",\"params\":{\"symbol\":\"端子\",\"x\":10,\"y\":20}}]}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ElectricalDir() =>
        Path.Combine(RepoRoot(), "examples", "skills", "electrical");

    private sealed class ImportPlanClient : ILlmClient
    {
        public string Plan { get; set; } =
            "{\"steps\":[{\"kind\":\"insert-symbol\",\"params\":{\"symbol\":\"端子\",\"x\":5,\"y\":5}}]}";
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(Plan, new TokenUsage(10, 5), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void Run_WithSkill_ImportsAndAudits()
    {
        var (code, _, err) = Run("run", _plan, "--drawing", _drawing, "--skill", ElectricalDir());
        Assert.Equal(0, code);
        Assert.DoesNotContain("错误", err);
        var s = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Single(s.Doc.Entities.OfType<ACadSharp.Entities.Insert>());
        Assert.True(s.Doc.BlockRecords.TryGetValue("端子", out _));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "step" && r.Entry.ParamsJson.Contains("insert-symbol"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_WithoutSkill_FailsUnregistered()
    {
        var (code, stdout, stderr) = Run("run", _plan, "--drawing", _drawing);
        Assert.Equal(1, code);
        using (var doc = System.Text.Json.JsonDocument.Parse(stdout))
            Assert.Contains("未注册", doc.RootElement.GetProperty("error").GetString());
        Assert.Empty(CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<ACadSharp.Entities.Insert>());
    }

    [Fact]
    public void Agent_WithSkill_ExecutesImport()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-symimpkey123456").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new ImportPlanClient();
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "放一个端子",
            "--yes", "--skill", ElectricalDir());
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        Assert.Single(CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<ACadSharp.Entities.Insert>());
    }

    [Fact]
    public void Agent_WithoutSkill_PlanRejectedAtValidation()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-symimpkey123456").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new ImportPlanClient();
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "放一个端子", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Failed", stdout);
        Assert.Empty(CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<ACadSharp.Entities.Insert>());
    }

    [Fact]
    public void Agent_WithSkill_UnknownSymbol_Escalates()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-symimpkey123456").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new ImportPlanClient
        {
            Plan = "{\"steps\":[{\"kind\":\"insert-symbol\",\"params\":{\"symbol\":\"不存在的符号\",\"x\":0,\"y\":0}}]}"
        };
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "放符号",
            "--yes", "--skill", ElectricalDir());
        Assert.Equal(1, code);
        Assert.Contains("Escalated", stdout);
        Assert.Empty(CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<ACadSharp.Entities.Insert>());
    }
}
