using System.Text.Json;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 2b-ii-b T6 电气/模型规则包 CLI 接线测试：5 新检查器注册进 StandardsEngine 后，
/// check --skill 电气包对 T4 fixture（BV-3×2.5-PC20-WC 合规 / BV-3×2.5-XX20-WC 槽位违规 /
/// 字高 2.0 违规）产出引用 GB/T 50786-2012 条款的 issues，全 recommended→exit 0；
/// 无 NoChecker 跳过（接线完整）；审计 rulePackVersion 含电气包@1.0.0。
/// model 包 file-naming：坏文件名→issue 引用 JGJ/T 448-2018 5.4.3；合规名无 issue。
/// skill validate 两包 exit 0（examples 包加载治理；examples 非 builtin，不钉治理哈希）。
/// </summary>
public class ElectricalModelPackageCliTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"elecpkg-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public ElectricalModelPackageCliTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-PC20-WC", Height = 3.5 });  // 合规简写
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-XX20-WC", Height = 3.5 }); // 敷设方式 XX 非法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "AL1", Height = 2.0 });               // 字母字高 2.0 < 2.5 违规
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ElectricalDir() => Path.Combine(RepoRoot(), "examples", "skills", "electrical");
    private static string ModelDir() => Path.Combine(RepoRoot(), "examples", "skills", "model");

    [Fact]
    public void SkillValidate_BothExamplePackages_ExitZero()
    {
        var (eCode, eOut, _) = Run("skill", "validate", ElectricalDir());
        Assert.Equal(0, eCode);
        using (var doc = JsonDocument.Parse(eOut))
        {
            Assert.Equal("建筑电气制图（GB/T 50786-2012）", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("1.0.0", doc.RootElement.GetProperty("version").GetString());
            Assert.Equal(4, doc.RootElement.GetProperty("ruleCount").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("aliasCount").GetInt32());
        }

        var (mCode, mOut, _) = Run("skill", "validate", ModelDir());
        Assert.Equal(0, mCode);
        using (var doc = JsonDocument.Parse(mOut))
        {
            Assert.Equal("信息模型制图（JGJ/T 448-2018）", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("ruleCount").GetInt32());
        }
    }

    [Fact]
    public void Check_ElectricalPackage_SlotAndHeightViolations_CiteClauses_ExitZero()
    {
        var (code, stdout, stderr) = Run("check", "--drawing", _drawing, "--skill", ElectricalDir());
        Assert.Equal(0, code); // 全 recommended——决策权归人，不影响退出码
        // 接线完整：电气 4 规则均有检查器，不得出现 NoChecker 跳过。
        Assert.DoesNotContain("[NoChecker]", stderr);

        using var doc = JsonDocument.Parse(stdout);
        var issues = doc.RootElement.GetProperty("issues").EnumerateArray().ToList();

        // 槽位违规：XX 不在表4.2.1-1 白名单——引用 3.4.3（应级条文，recommended 承载）。
        var slot = Assert.Single(issues, i => i.GetProperty("ruleId").GetString() == "R-50786-3.4.3");
        Assert.Equal("GB/T 50786-2012", slot.GetProperty("standard").GetString());
        Assert.Equal("3.4.3", slot.GetProperty("clause").GetString());
        Assert.Equal("Recommended", slot.GetProperty("force").GetString());
        Assert.Contains("XX", slot.GetProperty("message").GetString());
        Assert.Contains("应按本标准表 4.2.1-1～表 4.2.1-3 的文字符号标注", slot.GetProperty("excerpt").GetString());

        // 字高违规：AL1 2.0mm < 2.5mm（不应）——引用 5.1.3。
        var height = Assert.Single(issues, i => i.GetProperty("ruleId").GetString() == "R-50786-5.1.3");
        Assert.Equal("GB/T 50786-2012", height.GetProperty("standard").GetString());
        Assert.Equal("5.1.3", height.GetProperty("clause").GetString());
        Assert.Contains("AL1", height.GetProperty("message").GetString());
        Assert.Contains("2.5mm", height.GetProperty("message").GetString());

        // 合规缆线 BV-3×2.5-PC20-WC 不成任何电气 issue；cable-format 无违规（两缆线均合规简写形）。
        Assert.DoesNotContain(issues, i => i.GetProperty("ruleId").GetString() == "R-50786-4.1.5-8");
        Assert.DoesNotContain(issues, i => i.GetProperty("message").GetString()!.Contains("BV-3×2.5-PC20-WC"));

        // builtin 4 跑（轴号 8.0.4/标高 11.8.4/11.8.5 NotEvaluable 跳过 3）+ 电气 4 跑 = 8；无 mandatory。
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(8, summary.GetProperty("rulesRun").GetInt32());
        Assert.Equal(3, summary.GetProperty("rulesSkipped").GetInt32());
        Assert.Equal(0, summary.GetProperty("mandatoryViolations").GetInt32());

        // 审计复现证据：rulePackVersion 反映电气包。
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var run = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "check" && r.Entry.Kind == "check-run");
        using var paramsDoc = JsonDocument.Parse(run.Entry.ParamsJson);
        string pack = paramsDoc.RootElement.GetProperty("rulePackVersion").GetString()!;
        Assert.StartsWith("gb50001-2017.r1|", pack);
        Assert.Contains("建筑电气制图（GB/T 50786-2012）@1.0.0", pack);
    }

    [Fact]
    public void Check_ModelPackage_BadFileName_Issue_GoodName_Clean()
    {
        string bad = Path.Combine(_project, "电气平面图.dxf");
        File.Copy(_drawing, bad);
        var (badCode, badOut, _) = Run("check", "--drawing", bad, "--skill", ModelDir());
        Assert.Equal(0, badCode); // recommended/medium
        using (var doc = JsonDocument.Parse(badOut))
        {
            var issue = Assert.Single(doc.RootElement.GetProperty("issues").EnumerateArray(),
                i => i.GetProperty("ruleId").GetString() == "R-448-5.4.3");
            Assert.Equal("JGJ/T 448-2018", issue.GetProperty("standard").GetString());
            Assert.Equal("5.4.3", issue.GetProperty("clause").GetString());
            Assert.Equal("Recommended", issue.GetProperty("force").GetString());
            Assert.Equal("Medium", issue.GetProperty("severity").GetString());
            Assert.Contains("电气平面图.dxf", issue.GetProperty("message").GetString());
        }

        string good = Path.Combine(_project, "A_001_平面图.dxf");
        File.Copy(_drawing, good);
        var (goodCode, goodOut, _) = Run("check", "--drawing", good, "--skill", ModelDir());
        Assert.Equal(0, goodCode);
        using var goodDoc = JsonDocument.Parse(goodOut);
        Assert.DoesNotContain(goodDoc.RootElement.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("ruleId").GetString() == "R-448-5.4.3");
    }
}
