using ACadSharp.IO;
using CadAgent.Cli;
using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// 3j T4 生成式真机基准（C 并入 B，ZWCAD 2025）：LLM 读摘要产**生成式几何计划**
/// （轴线/房间名文字/柱圆——非符号导入）→agent --live→影子 tx+活会话 COM 重放→
/// live.dwg 读回断言生成式实体真正落在厂商文档→com-replay 审计+验链。
/// 兑现"agent 操作 CAD 绘制"本义 + 生成式能力验证。
/// </summary>
[Trait("Category", "RequiresZwcad")]
public class GenerativeLiveBenchmarkTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"genlive-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public GenerativeLiveBenchmarkTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("WALL"));
            var wall = doc.Layers.First(l => l.Name == "WALL");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(6000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 4000), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(0, 4000), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(6000, 0), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(_project, true);
                File.Delete(_drawing);
                if (File.Exists(_drawing + ".live.dwg")) File.Delete(_drawing + ".live.dwg");
                return;
            }
            catch (IOException) { Thread.Sleep(500); }
        }
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private sealed class GenerativeClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                request.Messages[0].Content.Contains("验证智能体")
                    ? "{\"verdict\":\"ok\"}"
                    : "{\"steps\":[" +
                      "{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":2000,\"x2\":6000,\"y2\":2000,\"layer\":\"AXIS\"}}," +
                      "{\"kind\":\"add-text\",\"params\":{\"x\":3000,\"y\":2000,\"text\":\"客厅\",\"height\":5}}," +
                      "{\"kind\":\"add-circle\",\"params\":{\"x\":1000,\"y\":1000,\"radius\":250}}]}",
                new TokenUsage(20, 8), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void GenerativePlan_LiveVendorExecution_ReadBack()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-genlivekey123456").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new GenerativeClient();

        var (code, stdout, err) = Run("agent", "--drawing", _drawing,
            "--intent", "补轴线、房间名与柱", "--yes", "--live");
        Assert.True(code == 0, $"code={code} out={stdout} err={err}");
        Assert.Contains("Executed", stdout);

        string liveDwg = _drawing + ".live.dwg";
        Assert.True(File.Exists(liveDwg));
        using var fs = File.OpenRead(liveDwg);
        var doc = new DwgReader(fs).Read();
        // 生成式几何真正落在厂商文档：轴线/房间名/柱圆
        Assert.Contains(doc.Entities.OfType<Line>(), l =>
            l.Layer?.Name == "AXIS" && l.StartPoint.Y == 2000 && l.EndPoint.X == 6000);
        Assert.Contains(doc.Entities.OfType<TextEntity>(), t => t.Value == "客厅");
        Assert.Contains(doc.Entities.OfType<Circle>(), c => Math.Abs(c.Radius - 250) < 1e-6);
        Assert.Equal(7, doc.Entities.Count()); // 4 墙 + 3 生成

        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        Assert.Equal(3, all.Count(r => r.Entry.Kind == "com-replay"));
        Assert.Contains(all, r => r.Entry.Kind == "com-replay-committed");
        Assert.Contains(all, r => r.Entry.Kind == "agent-verify");
        Assert.True(audit.VerifyChain(out _));
        Assert.All(all, r => Assert.DoesNotContain("sk-genlivekey123456", r.Entry.ParamsJson));
    }
}
