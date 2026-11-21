using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using ACadSharp.IO;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3j T3：--live 接线（fake 厂商运行时）——重放审计/读回闸口/live.dwg 交付/
/// 不支持 kind 响亮失败/无厂商响亮失败/agent 生成式离线重放。
/// </summary>
public class LiveReplayCliTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"live-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _plan;
    private readonly FakeLiveRuntime _rt = new();

    public LiveReplayCliTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(_plan,
            "{\"planId\":\"LIVE-1\",\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}}]}");
        CommandRouter.RuntimeFactory = () => _rt;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        CommandRouter.RuntimeFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
        if (File.Exists(_drawing + ".live.dwg")) File.Delete(_drawing + ".live.dwg");
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private sealed class FakeLiveRuntime : ICadRuntime
    {
        private int _count;
        public List<string> Calls { get; } = new();
        public string? SavedPath { get; private set; }
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string path)
        {
            using var fs = File.OpenRead(path);
            _count = new DxfReader(fs).Read().Entities.Count();
            Calls.Add("open:" + Path.GetFileName(path));
        }
        public void SaveAs(string path) { SavedPath = path; Calls.Add("saveas"); }
        public void Close(bool save) => Calls.Add("close");
        public void Restart() => Calls.Add("restart");
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) { _count++; Calls.Add("line"); }
        public void AddText(double x, double y, string text, double height, string? layer = null) { _count++; Calls.Add("text"); }
        public void AddCircle(double x, double y, double r, string? layer = null) { _count++; Calls.Add("circle"); }
        public void AddInsert(string b, double x, double y, double s = 1, double r = 0, string? layer = null) { _count++; Calls.Add("insert"); }
        public void EnsureBlock(string name, IReadOnlyList<BlockPrimitive> prims) => Calls.Add("block:" + name);
        public int EntityCount() => _count;
        public void Dispose() { }
    }

    private sealed class GenerativeClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                request.Messages[0].Content.Contains("验证智能体")
                    ? "{\"verdict\":\"ok\"}"
                    : "{\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":100,\"y2\":0}}," +
                      "{\"kind\":\"add-text\",\"params\":{\"x\":50,\"y\":10,\"text\":\"客厅\",\"height\":5}}]}",
                new TokenUsage(10, 5), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void Run_Live_ReplaysAuditsAndDelivers()
    {
        var (code, stdout, err) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.True(code == 0, $"code={code} out={stdout} err={err}");
        Assert.Contains(".live.dwg", stdout);
        Assert.Equal(_drawing + ".live.dwg", _rt.SavedPath);
        Assert.Contains(_rt.Calls, c => c.StartsWith("open:"));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-committed");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_Live_UnsupportedKind_FailsLoudly()
    {
        var s = CadAgent.Data.DrawingSession.Load(_drawing);
        string handle = s.Doc.Entities.First().Handle.ToString();
        File.WriteAllText(_plan,
            "{\"planId\":\"LIVE-2\",\"steps\":[{\"kind\":\"move-entity\",\"params\":{\"handle\":" + handle + ",\"dx\":1,\"dy\":1}}]}");
        var (code, _, err) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.Equal(1, code);
        Assert.Contains("不支持步骤类型", err);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-failed");
    }

    [Fact]
    public void Run_Live_NoVendor_FailsLoudly()
    {
        CommandRouter.RuntimeFactory = () => null;
        var (code, _, err) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.Equal(1, code);
        Assert.Contains("无可用厂商运行时", err);
    }

    [Fact]
    public void Agent_Live_GenerativePlan_Replayed()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-livekey12345678").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new GenerativeClient();
        var (code, stdout, err) = Run("agent", "--drawing", _drawing, "--intent", "画线并写房间名", "--yes", "--live");
        Assert.True(code == 0, $"code={code} out={stdout} err={err}");
        Assert.Contains("Executed", stdout);
        Assert.Contains("line", _rt.Calls);
        Assert.Contains("text", _rt.Calls);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Equal(2, audit.ReadAll().Count(r => r.Entry.Kind == "com-replay"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-committed");
    }
}
