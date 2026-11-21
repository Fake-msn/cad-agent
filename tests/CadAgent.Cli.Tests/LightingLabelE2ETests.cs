using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3e T4 编号 E2E：布灯基准延伸——布灯→再 scan（E-LITE Insert 获 Lighting 别名 0.8）→
/// skill run label-luminaires（interior-244 模板）→6 索引符号+L1..L6→签认→验链。
/// 闸口变体：无 Lighting 确认→agent 编号计划 Escalated+semantic-gate-refused（Lighting 类首次实战）。
/// 时序关键：scan 在布灯前则 E-LITE Insert 尚不存在——布灯后必须再 scan 才有 Lighting 标注。
/// </summary>
public class LightingLabelE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"lightlabel-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _lightingCopy;
    private readonly string _interiorCopy;

    public LightingLabelE2ETests()
    {
        Directory.CreateDirectory(_project);
        _lightingCopy = Path.Combine(_project, "lighting");
        CopyDirectory(SkillDir("lighting"), _lightingCopy);
        _interiorCopy = Path.Combine(_project, "interior-244");
        CopyDirectory(SkillDir("interior-244"), _interiorCopy);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("WALL"));
            var wall = doc.Layers.First(l => l.Name == "WALL");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(6000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 4000), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(0, 4000), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(6000, 0), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
            doc.Layers.Add(new Layer("ROOM-NAME"));
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = (XYZ)new XY(3000, 2000),
                Value = "客厅",
                Height = 5,
                Layer = doc.Layers.First(l => l.Name == "ROOM-NAME")
            });
            var block = new BlockRecord("LUM");
            block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
            doc.BlockRecords.Add(block);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        CommandRouter.IdentityOverride = null;
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

    private static string SkillDir(string name) =>
        Path.Combine(RepoRoot(), "examples", "skills", name);

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    private sealed class PlanClient : ILlmClient
    {
        public string Plan { get; set; } =
            "{\"steps\":[{\"kind\":\"layout-lighting\",\"params\":{\"x\":0,\"y\":0,\"width\":6000,\"depth\":4000,\"spacing\":2000,\"block\":\"LUM\",\"layer\":\"E-LITE\"}}]}";
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(Plan, new TokenUsage(20, 8), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private sealed class FixedId : IIdentityProvider
    {
        public FixedId(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private string LastTxId()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var tx = audit.ReadAll().Last(r => r.Entry.Kind == "tx-commit");
        return Path.GetFileName(Path.GetDirectoryName(tx.Entry.SnapshotRef)!)!;
    }

    private void PrepareAndLayout()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-labelkey12345678").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient();
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _drawing, "--skill", _lightingCopy).code);
        Assert.Equal(0, Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing).code);
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "布灯", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        Assert.Equal(6, DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>().Count());
    }

    [Fact]
    public void FullChain_Layout_Rescan_Label_Signoff_ChainGreen()
    {
        PrepareAndLayout();
        // 布灯后再 scan：E-LITE 层 Insert 获 Lighting 别名 0.8（时序关键）
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _drawing, "--skill", _lightingCopy).code);
        var (code, stdout, err) = Run("skill", "run", _interiorCopy,
            "--template", "label-luminaires", "--drawing", _drawing, "--args", """{"prefix":"L"}""");
        Assert.Equal(0, code);
        Assert.Empty(err);
        using (var doc = System.Text.Json.JsonDocument.Parse(stdout))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var session = DrawingSession.Load(_drawing);
        var texts = session.Doc.Entities.OfType<TextEntity>()
            .Where(t => t.Value.StartsWith("L", StringComparison.Ordinal) && t.Value != "客厅")
            .OrderBy(t => t.Handle).Select(t => t.Value).ToList();
        Assert.Equal(new[] { "L1", "L2", "L3", "L4", "L5", "L6" }, texts);
        // 6 符号 ×（6 边 + 1 内径线）= 42 新 Line（底图 4 墙线 + 块内 1 线不计入 Doc.Entities）
        Assert.Equal(46, session.Doc.Entities.OfType<Line>().Count());

        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var (ac, _, aErr) = Run("review", "signoff", "--drawing", _drawing, "--tx", LastTxId(),
            "--approve", "--comment", "编号合格");
        Assert.Equal(0, ac);
        Assert.Empty(aErr);
        CommandRouter.IdentityOverride = null;

        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        Assert.Contains(all, r => r.Entry.Kind == "skill-run");
        Assert.Contains(all, r => r.Entry.Kind == "review-approved");
        Assert.True(audit.VerifyChain(out _));
        Assert.All(all, r => Assert.DoesNotContain("sk-labelkey12345678", r.Entry.ParamsJson));
    }

    [Fact]
    public void NoLightingConfirmation_AgentLabelPlan_Escalated()
    {
        PrepareAndLayout();
        // 再 scan 不带 lighting skill：E-LITE 无别名→Lighting 语义缺失
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _drawing).code);
        CommandRouter.LlmClientFactory = (p, k) => new PlanClient
        {
            Plan = "{\"steps\":[{\"kind\":\"label-luminaires\",\"params\":{\"prefix\":\"L\"}}]}"
        };
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "给灯具编号", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Escalated", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-gate-refused");
        var session = DrawingSession.Load(_drawing);
        Assert.DoesNotContain(session.Doc.Entities.OfType<TextEntity>(), t => t.Value == "L1");
    }
}
