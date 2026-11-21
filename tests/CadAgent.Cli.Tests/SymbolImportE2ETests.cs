using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3h T3 E2E：跨文档符号导入落图闭环——建筑底图（墙+房名，无灯具块）→scan/map 确认 Room→
/// skill run place-luminaire 模板×3（interior-244 筒灯符号跨文档导入）→块复用（1 块 3 Insert）→
/// 审查签认（职责分离）→验链。符号库 324+17 自此真正落图。
/// </summary>
public class SymbolImportE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"syme2e-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public SymbolImportE2ETests()
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
            doc.Layers.Add(new Layer("ROOM-NAME"));
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = (XYZ)new XY(3000, 2000),
                Value = "客厅",
                Height = 5,
                Layer = doc.Layers.First(l => l.Name == "ROOM-NAME")
            });
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private static string InteriorDir() =>
        Path.Combine(RepoRoot(), "examples", "skills", "interior-244");

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

    [Fact]
    public void PlaceLuminaires_ImportFromSkillLibrary_Signoff_ChainGreen()
    {
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _drawing, "--skill", InteriorDir()).code);
        Assert.Equal(0, Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing).code);

        var points = new[] { (1000, 1000), (3000, 2000), (5000, 3000) };
        foreach (var (x, y) in points)
        {
            var (code, stdout, err) = Run("skill", "run", InteriorDir(),
                "--template", "place-luminaire", "--drawing", _drawing,
                "--args", $"{{\"symbol\":\"筒灯\",\"x\":{x},\"y\":{y}}}");
            Assert.Equal(0, code);
            Assert.DoesNotContain("错误", err);
            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        var s = DrawingSession.Load(_drawing);
        var inserts = s.Doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(3, inserts.Count);
        Assert.All(inserts, i => Assert.Equal("筒灯", i.Block?.Name));
        Assert.Equal(1, s.Doc.BlockRecords.Count(b => b.Name == "筒灯"));
        Assert.True(s.Doc.BlockRecords.TryGetValue("筒灯", out var block));
        Assert.True(block.Entities.Count() > 0);

        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var (ac, _, aErr) = Run("review", "signoff", "--drawing", _drawing, "--tx", LastTxId(),
            "--approve", "--comment", "灯具落图合格");
        Assert.Equal(0, ac);
        Assert.Empty(aErr);
        CommandRouter.IdentityOverride = null;

        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        Assert.Contains(all, r => r.Entry.Kind == "skill-run");
        Assert.Contains(all, r => r.Entry.Kind == "step" && r.Entry.ParamsJson.Contains("insert-symbol"));
        Assert.Contains(all, r => r.Entry.Kind == "review-approved");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void PlaceLuminaire_UnknownSymbol_FailsNoMutation()
    {
        var (code, stdout, _) = Run("skill", "run", InteriorDir(),
            "--template", "place-luminaire", "--drawing", _drawing,
            "--args", "{\"symbol\":\"不存在的灯\",\"x\":0,\"y\":0}");
        Assert.Equal(1, code);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("符号不存在", doc.RootElement.GetProperty("error").GetString());
        Assert.Empty(DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>());
    }
}
