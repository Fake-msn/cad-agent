using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3d T3：语义闸口实战——layout-lighting 声明 Room 需求：
/// scan 推断 0.6 &lt; 闸口 0.8 → 拒执行（semantic-gate-refused 留痕，重规划一次仍拒→Escalated）；
/// 人工 map 确认 Room 1.0 → 放行，网格 Insert 落图，审计链绿。
/// 灯具 skill 包（示例院标）校验 + E-LITE→Lighting 别名接入扫描。
/// </summary>
public class LightingGateTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"lightgate-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _skillCopy;

    public LightingGateTests()
    {
        Directory.CreateDirectory(_project);
        _skillCopy = Path.Combine(_project, "lighting");
        CopyDirectory(LightingSkillDir(), _skillCopy);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("ROOM-NAME"));
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = (XYZ)new XY(3000, 2000),
                Value = "客厅",
                Height = 5,
                Layer = doc.Layers.First(l => l.Name == "ROOM-NAME")
            });
            doc.Layers.Add(new Layer("E-LITE"));
            doc.Entities.Add(new Line
            {
                StartPoint = (XYZ)new XY(0, 0),
                EndPoint = (XYZ)new XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "E-LITE")
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

    private static string LightingSkillDir() =>
        Path.Combine(RepoRoot(), "examples", "skills", "lighting");

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    private void SetupProfile()
    {
        Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1", "--model", "m", "--key", "sk-lightkey12345678");
        Run("llm", "use", "--name", "p");
        CommandRouter.LlmClientFactory = (p, k) => new LightingPlanClient();
    }

    private sealed class LightingPlanClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                "{\"steps\":[{\"kind\":\"layout-lighting\",\"params\":{\"x\":0,\"y\":0,\"width\":6000,\"depth\":4000,\"spacing\":2000,\"block\":\"LUM\"}}]}",
                new TokenUsage(10, 5), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public void SkillPackage_Validates_AliasScansLighting()
    {
        var (vCode, _, vErr) = Run("skill", "validate", _skillCopy);
        Assert.Equal(0, vCode);
        Assert.Empty(vErr);
        var (sCode, _, sErr) = Run("semantic", "scan", "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(0, sCode);
        Assert.Empty(sErr);
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Contains(overlay.Annotations, a => a.Class == SemanticClass.Lighting && a.Confidence >= 0.8);
    }

    [Fact]
    public void NoRoomConfirmation_GateRefuses_Escalated()
    {
        SetupProfile();
        var (scanCode, _, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "布灯", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Escalated", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-gate-refused");
        Assert.Empty(CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>());
        Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-lightkey12345678", r.Entry.ParamsJson));
    }

    [Fact]
    public void RoomConfirmed_Executes_GridInserted()
    {
        SetupProfile();
        Run("semantic", "scan", "--drawing", _drawing);
        var (mapCode, _, mapErr) = Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing);
        Assert.Equal(0, mapCode);
        Assert.Empty(mapErr);
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "布灯", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        var inserts = CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(6, inserts.Count);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-map");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
    }
}
