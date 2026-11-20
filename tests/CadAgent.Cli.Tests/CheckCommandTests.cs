using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

public class CheckCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"chk-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public CheckCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "字高违规", Height = 4.2 });
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-墙体"));
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void Check_RecommendedIssues_ExitZero_Audited()
    {
        var (code, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, code); // 全 recommended，无 mandatory
        Assert.Contains("R-50001-5.0.2", stdout);
        Assert.Contains("R-50001-13.0.1", stdout);
        Assert.Contains("GB/T 50001-2017", stdout); // 引用义务
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Adjudicate_UpgradeMandatory_ThenCheckExitTwo()
    {
        var (ac, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-5.0.2",
            "--force", "mandatory", "--reason", "院标升级：字高强制");
        Assert.Equal(0, ac);
        var (code, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(2, code);
        // 控制者 setup 裁定：枚举按 SemanticJsonOpts 序列化为 PascalCase（"Mandatory"），
        // brief 原断言 Contains("mandatory") 相应调整为 "Mandatory"。
        Assert.Contains("Mandatory", stdout);
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Adjudicate_Exempt_RemovesIssue()
    {
        Run("rule", "adjudicate", "--rule", "R-50001-13.0.1", "--exempt", "--reason", "本项目图层约定另定");
        var (_, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.DoesNotContain("R-50001-13.0.1", stdout);
    }

    [Fact]
    public void Adjudicate_ForceAndExempt_Conflict_ExitOne()
    {
        var (code, _, stderr) = Run("rule", "adjudicate", "--rule", "R1",
            "--force", "mandatory", "--exempt", "--reason", "x");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Adjudicate_MissingReason_ExitOne()
    {
        var (code, _, _) = Run("rule", "adjudicate", "--rule", "R1", "--exempt");
        Assert.Equal(1, code);
    }

    [Fact]
    public void RuleList_ShowsBuiltinsAndAdjudications()
    {
        Run("rule", "adjudicate", "--rule", "R-50001-5.0.2", "--force", "mandatory", "--reason", "院标");
        var (code, stdout, _) = Run("rule", "list");
        Assert.Equal(0, code);
        Assert.Contains("R-50001-5.0.2", stdout);
        Assert.Contains("院标", stdout);
    }
}