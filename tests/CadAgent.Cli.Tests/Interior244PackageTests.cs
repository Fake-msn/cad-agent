using CadAgent.Core;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3e T3：interior-244 真实条文规则包——validate；check 房名 issue（recommended，exit 0）
/// + 4 条 VISUAL/深度规则 NoChecker 结构化跳过（诚实登记，不静默）；
/// skill run label-luminaires 模板：Lighting 语义闸口拒/放行（别名 scan 0.8）。
/// </summary>
public class Interior244PackageTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"i244-{Guid.NewGuid():N}");
    private readonly string _skillCopy;
    private readonly string _lightingCopy;
    private readonly string _noRoom;
    private readonly string _withRoom;

    public Interior244PackageTests()
    {
        Directory.CreateDirectory(_project);
        _skillCopy = Path.Combine(_project, "interior-244");
        CopyDirectory(SkillDir("interior-244"), _skillCopy);
        _lightingCopy = Path.Combine(_project, "lighting");
        CopyDirectory(SkillDir("lighting"), _lightingCopy);
        _noRoom = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("E-LITE"));
            var lite = doc.Layers.First(l => l.Name == "E-LITE");
            var block = new BlockRecord("LUM");
            block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(0, 0), Layer = lite });
            doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(2000, 0), Layer = lite });
        });
        _withRoom = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(100, 100), Value = "客厅", Height = 5 });
            var block = new BlockRecord("LUM");
            block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(0, 0) });
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_noRoom);
        File.Delete(_withRoom);
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

    [Fact]
    public void Package_Validates()
    {
        var (code, _, err) = Run("skill", "validate", _skillCopy);
        Assert.Equal(0, code);
        Assert.Empty(err);
    }

    [Fact]
    public void Check_NoRoom_RecommendedIssue_DeferredRulesSkippedLoudly()
    {
        var (code, stdout, stderr) = Run("check", "--drawing", _noRoom, "--skill", _skillCopy);
        Assert.Equal(0, code); // recommended 不影响退出码（决策权归人）
        Assert.Contains("R-244-5.2.5", stdout);
        Assert.Contains("NoChecker", stderr);
        foreach (string t in new[] { "luminaire-legend", "equipment-index-symbol", "luminaire-layout-depth", "custom-legend-governance" })
            Assert.Contains(t, stderr);
    }

    [Fact]
    public void Check_WithRoom_NoRoomNameIssue()
    {
        var (code, stdout, _) = Run("check", "--drawing", _withRoom, "--skill", _skillCopy);
        Assert.Equal(0, code);
        Assert.DoesNotContain("R-244-5.2.5", stdout);
    }

    [Fact]
    public void SkillRun_Label_WithoutLightingSemantics_GateRefuses()
    {
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _noRoom).code);
        var (code, stdout, _) = Run("skill", "run", _skillCopy,
            "--template", "label-luminaires", "--drawing", _noRoom, "--args", """{"prefix":"L"}""");
        Assert.Equal(1, code);
        Assert.Contains("Lighting", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-gate-refused");
        Assert.Empty(CadAgent.Data.DrawingSession.Load(_noRoom).Doc.Entities.OfType<TextEntity>());
    }

    [Fact]
    public void SkillRun_Label_WithLightingAlias_LabelsGenerated()
    {
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _noRoom, "--skill", _lightingCopy).code);
        var (code, stdout, err) = Run("skill", "run", _skillCopy,
            "--template", "label-luminaires", "--drawing", _noRoom, "--args", """{"prefix":"L"}""");
        Assert.Equal(0, code);
        Assert.Empty(err);
        using (var doc = System.Text.Json.JsonDocument.Parse(stdout))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var texts = CadAgent.Data.DrawingSession.Load(_noRoom).Doc.Entities.OfType<TextEntity>()
            .OrderBy(t => t.Handle).Select(t => t.Value).ToList();
        Assert.Equal(new[] { "L1", "L2" }, texts);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }
}
