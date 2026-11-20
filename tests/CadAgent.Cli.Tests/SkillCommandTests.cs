using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 2b-ii-a T7：CLI skill 命令族（list/validate）、check/scan --skill 接线、
/// semantic promote（项目映射→skill 别名 upsert + manifest patch bump + 审计）。
/// </summary>
public class SkillCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"skcli-{Guid.NewGuid():N}");
    private readonly string _skillDir;
    private readonly string _drawing;
    public SkillCommandTests()
    {
        Directory.CreateDirectory(_project);
        _skillDir = Path.Combine(_project, "myskill");
        Directory.CreateDirectory(Path.Combine(_skillDir, "rules"));
        File.WriteAllText(Path.Combine(_skillDir, "skill.yaml"), """
            name: 测试院标
            version: "1.0.0"
            discipline: architecture
            drawingType: any
            description: 测试
            """);
        File.WriteAllText(Path.Combine(_skillDir, "rules", "r.yaml"), """
            id: SK-TEST-001
            source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
            modal: "应"
            force: mandatory
            severity: high
            scope: { discipline: any, drawingType: any }
            check: { type: layer-name, params: { mode: charset } }
            suggestion: "测试"
            """);
        File.WriteAllText(Path.Combine(_skillDir, "aliases.yaml"), "aliases:\n  - layerName: W-自定义\n    class: Wall\n");
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-自定义"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0), EndPoint = (CSMath.XYZ)new CSMath.XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "W-自定义")
            });
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
    public void SkillValidate_Valid_ExitZero()
    {
        var (code, stdout, _) = Run("skill", "validate", _skillDir);
        Assert.Equal(0, code);
        Assert.Contains("测试院标", stdout);
    }

    [Fact]
    public void SkillValidate_BadVersion_ExitOne()
    {
        File.WriteAllText(Path.Combine(_skillDir, "skill.yaml"),
            File.ReadAllText(Path.Combine(_skillDir, "skill.yaml")).Replace("1.0.0", "abc"));
        var (code, _, stderr) = Run("skill", "validate", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Check_WithSkill_MandatoryRule_ExitTwo()
    {
        // SK-TEST-001 mandatory layer-charset：drawing2 层 W-墙 汉英混用→违规触发。
        var drawing2 = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-墙"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0), EndPoint = (CSMath.XYZ)new CSMath.XY(1, 1),
                Layer = doc.Layers.First(l => l.Name == "W-墙")
            });
        });
        try
        {
            var (code, stdout, _) = Run("check", "--drawing", drawing2, "--skill", _skillDir);
            Assert.Equal(2, code); // skill mandatory 规则生效
            Assert.Contains("SK-TEST-001", stdout);
        }
        finally { File.Delete(drawing2); }
    }

    [Fact]
    public void Check_SkillIdCollision_ExitOne()
    {
        // 复制 builtin Id 到 skill
        File.WriteAllText(Path.Combine(_skillDir, "rules", "dup.yaml"),
            File.ReadAllText(Path.Combine(_skillDir, "rules", "r.yaml")).Replace("SK-TEST-001", "R-50001-5.0.2"));
        var (code, _, stderr) = Run("check", "--drawing", _drawing, "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.Contains("R-50001-5.0.2", stderr);
    }

    [Fact]
    public void Scan_WithSkillAlias_RemovesAmbiguous()
    {
        var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing, "--skill", _skillDir);
        Assert.Equal(0, code);
        // 别名命中→不再不明确。断言落在 ambiguous 数组（SemanticE2ETests 先例）：
        // stdout annotations 的 evidence 按 2a 契约含层名（"skill别名: W-自定义"），
        // 整串 DoesNotContain 与既有 stdout 契约矛盾——按测试意图（不再不明确）收窄到 ambiguous。
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        var ambiguous = doc.RootElement.GetProperty("ambiguous").EnumerateArray()
            .Select(a => a.GetProperty("layerName").GetString()).ToList();
        Assert.DoesNotContain("W-自定义", ambiguous);
        // 别名确实命中：该层实体被标注为 Wall（LayerInference 0.8）。
        var annotations = doc.RootElement.GetProperty("annotations").EnumerateArray().ToList();
        Assert.Contains(annotations, a =>
            a.GetProperty("class").GetString() == "Wall" &&
            a.GetProperty("provenance").GetString() == "LayerInference");
    }

    [Fact]
    public void Promote_WritesAlias_BumpsVersion_Audited()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        Run("semantic", "map", "--layer", "W-自定义", "--class", "Column", "--drawing", _drawing);
        var (code, _, _) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
        Assert.Equal(0, code);
        string aliases = File.ReadAllText(Path.Combine(_skillDir, "aliases.yaml"));
        Assert.Contains("Column", aliases);
        string manifest = File.ReadAllText(Path.Combine(_skillDir, "skill.yaml"));
        Assert.Contains("1.0.1", manifest); // patch bump
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Promote_NoMapping_ExitOne()
    {
        var (code, _, stderr) = Run("semantic", "promote", "--layer", "不存在", "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void SkillList_ProjectSkills_ShowsPackage()
    {
        string skillsRoot = Path.Combine(_project, ".cadagent", "skills", "测试院标");
        Directory.CreateDirectory(skillsRoot);
        foreach (string f in Directory.GetFiles(_skillDir))
            File.Copy(f, Path.Combine(skillsRoot, Path.GetFileName(f)));
        Directory.CreateDirectory(Path.Combine(skillsRoot, "rules"));
        File.Copy(Path.Combine(_skillDir, "rules", "r.yaml"), Path.Combine(skillsRoot, "rules", "r.yaml"));
        var (code, stdout, _) = Run("skill", "list");
        Assert.Equal(0, code);
        Assert.Contains("测试院标", stdout);
    }
}
