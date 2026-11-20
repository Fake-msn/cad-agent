using System.Text.Json;
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
    public void Promote_FlowStyleManifest_ExitOne_ZeroMutation()
    {
        // 回归（评审 Important）：flow-style manifest loader 可加载，但无列 0 version 行。
        // promote 必须在任何写入之前校验 version 行→exit 1，aliases/manifest 字节不变、
        // 无 semantic-promoted 审计（validation-before-side-effects）。
        Run("semantic", "scan", "--drawing", _drawing);
        Run("semantic", "map", "--layer", "W-自定义", "--class", "Column", "--drawing", _drawing);
        File.WriteAllText(Path.Combine(_skillDir, "skill.yaml"),
            "{name: 测试院标, version: \"1.0.0\", discipline: architecture, drawingType: any, description: 测试}\n");
        byte[] aliasesBefore = File.ReadAllBytes(Path.Combine(_skillDir, "aliases.yaml"));
        byte[] manifestBefore = File.ReadAllBytes(Path.Combine(_skillDir, "skill.yaml"));
        var (code, _, stderr) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Equal(aliasesBefore, File.ReadAllBytes(Path.Combine(_skillDir, "aliases.yaml")));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(_skillDir, "skill.yaml")));
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "semantic-promoted");
    }

    [Fact]
    public void Promote_NestedVersionLine_TopLevelBumpedOnly()
    {
        // 回归（评审 minor #2）：version 正则锚定列 0——缩进的嵌套 version 行不得被命中。
        // loader 拒绝未知键（metadata 等），故用 description 块标量内的缩进 version 行构造。
        Run("semantic", "scan", "--drawing", _drawing);
        Run("semantic", "map", "--layer", "W-自定义", "--class", "Column", "--drawing", _drawing);
        File.WriteAllText(Path.Combine(_skillDir, "skill.yaml"), """
            name: 测试院标
            description: |
              测试
              version: "9.9.9"
            version: "1.0.0"
            discipline: architecture
            drawingType: any
            """);
        var (code, _, _) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
        Assert.Equal(0, code);
        string[] lines = File.ReadAllText(Path.Combine(_skillDir, "skill.yaml")).Split('\n');
        Assert.Contains("version: \"1.0.1\"", lines); // 顶层 patch bump（列 0 整行匹配）
        Assert.Contains("  version: \"9.9.9\"", lines); // 嵌套行原样保留
        Assert.DoesNotContain("9.9.10", string.Join("\n", lines));
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

    // ---- 最终审查 fix wave：promote 两阶段提交 + semantic-scan 规则包溯源 ----

    private List<CadAgent.Core.AuditRecord> AuditRecords()
    {
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    private void ScanAndMapForPromote()
    {
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _drawing).code);
        Assert.Equal(0, Run("semantic", "map", "--layer", "W-自定义", "--class", "Column", "--drawing", _drawing).code);
    }

    [Fact]
    public void Promote_FirstStageWriteFailure_ZeroMutation_FailureAudit()
    {
        // 模拟选择（最终审查裁定）：aliases.yaml.tmp 预置为目录——File.WriteAllText 对该
        // 路径在任何 Move 之前抛异常，证明"先完整暂存、后 Move"的阶段分离。
        // 失败必须留痕：semantic-promote-failed（movedFiles 空）+ stderr + exit 1，零变更。
        ScanAndMapForPromote();
        Directory.CreateDirectory(Path.Combine(_skillDir, "aliases.yaml.tmp"));
        byte[] aliasesBefore = File.ReadAllBytes(Path.Combine(_skillDir, "aliases.yaml"));
        byte[] manifestBefore = File.ReadAllBytes(Path.Combine(_skillDir, "skill.yaml"));
        var (code, _, stderr) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Equal(aliasesBefore, File.ReadAllBytes(Path.Combine(_skillDir, "aliases.yaml")));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(_skillDir, "skill.yaml")));
        Assert.False(File.Exists(Path.Combine(_skillDir, "skill.yaml.tmp"))); // 第二文件未暂存，更未 Move
        var records = AuditRecords();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "semantic-promoted");
        var failed = Assert.Single(records, r => r.Entry.Kind == "semantic-promote-failed");
        Assert.Equal("semantic", failed.Entry.Source);
        Assert.Equal("W-自定义", failed.Entry.Target);
        using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
        Assert.Equal("W-自定义", p.RootElement.GetProperty("layer").GetString());
        Assert.Equal(_skillDir, p.RootElement.GetProperty("skill").GetString());
        Assert.NotEmpty(p.RootElement.GetProperty("error").GetString()!);
        Assert.Empty(p.RootElement.GetProperty("movedFiles").EnumerateArray());
    }

    [Fact]
    public void Promote_SecondStageWriteFailure_ZeroMutation_NoPrematureMove()
    {
        // 更强模拟：skill.yaml.tmp 预置为目录。旧实现（写 aliases→Move→写 manifest→Move）
        // 走到此处时 aliases.yaml 已被 Move——内容变更却无升版无审计（审查 Important #1）。
        // 两阶段提交必须先完整暂存两个 .tmp 再开始 Move→此处零变更 + 失败审计 movedFiles 空。
        ScanAndMapForPromote();
        Directory.CreateDirectory(Path.Combine(_skillDir, "skill.yaml.tmp"));
        byte[] aliasesBefore = File.ReadAllBytes(Path.Combine(_skillDir, "aliases.yaml"));
        byte[] manifestBefore = File.ReadAllBytes(Path.Combine(_skillDir, "skill.yaml"));
        var (code, _, stderr) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Equal(aliasesBefore, File.ReadAllBytes(Path.Combine(_skillDir, "aliases.yaml")));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(_skillDir, "skill.yaml")));
        var records = AuditRecords();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "semantic-promoted");
        var failed = Assert.Single(records, r => r.Entry.Kind == "semantic-promote-failed");
        using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
        Assert.Empty(p.RootElement.GetProperty("movedFiles").EnumerateArray());
    }

    [Fact]
    public void Promote_MoveFailureMidSequence_FailureAudit_RecordsMovedFiles()
    {
        // 中途 Move 失败模拟（Windows）：skill.yaml 置只读——File.Move(overwrite:true)
        // 对只读目标抛 UnauthorizedAccessException，而第一个 Move（aliases.yaml）已成功。
        // 失败审计必须如实记录 movedFiles=["aliases.yaml"]（已变更未升版的可追溯证据），
        // 无 semantic-promoted；manifest 字节不变；stderr + exit 1。
        ScanAndMapForPromote();
        string manifestPath = Path.Combine(_skillDir, "skill.yaml");
        byte[] manifestBefore = File.ReadAllBytes(manifestPath);
        File.SetAttributes(manifestPath, FileAttributes.ReadOnly);
        try
        {
            var (code, _, stderr) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
            Assert.Equal(1, code);
            Assert.NotEmpty(stderr);
            Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath)); // 版本未升
            Assert.Contains("Column", File.ReadAllText(Path.Combine(_skillDir, "aliases.yaml"))); // 第一 Move 已发生（不回滚，审计留痕）
            var records = AuditRecords();
            Assert.DoesNotContain(records, r => r.Entry.Kind == "semantic-promoted");
            var failed = Assert.Single(records, r => r.Entry.Kind == "semantic-promote-failed");
            Assert.Equal("semantic", failed.Entry.Source);
            using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
            Assert.Equal("W-自定义", p.RootElement.GetProperty("layer").GetString());
            Assert.Equal(_skillDir, p.RootElement.GetProperty("skill").GetString());
            Assert.NotEmpty(p.RootElement.GetProperty("error").GetString()!);
            Assert.Equal(new[] { "aliases.yaml" },
                p.RootElement.GetProperty("movedFiles").EnumerateArray().Select(x => x.GetString()).ToList());
        }
        finally
        {
            File.SetAttributes(manifestPath, FileAttributes.Normal); // Dispose 递归删除需要可写
        }
    }

    [Fact]
    public void Scan_Audit_CarriesRulePackVersion_WithSkill()
    {
        // 审查 Important #2：semantic-scan 审计必须携带规则包溯源（别名命中改变 overlay，
        // 审计员须能分辨是哪个 skill 版本产生的）。临时 skill：示例院标@1.0.0。
        string sampleDir = Path.Combine(_project, "provenance-skill");
        Directory.CreateDirectory(sampleDir);
        File.WriteAllText(Path.Combine(sampleDir, "skill.yaml"), """
            name: 示例院标
            version: "1.0.0"
            discipline: architecture
            drawingType: any
            description: 溯源测试 skill
            """);
        File.WriteAllText(Path.Combine(sampleDir, "aliases.yaml"), "aliases:\n  - layerName: W-自定义\n    class: Wall\n");
        var (code, _, _) = Run("semantic", "scan", "--drawing", _drawing, "--skill", sampleDir);
        Assert.Equal(0, code);
        var rec = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        string pack = p.RootElement.GetProperty("rulePackVersion").GetString()!;
        Assert.Contains("示例院标@1.0.0", pack);
        Assert.StartsWith("gb50001-2017.r1|", pack);
    }

    [Fact]
    public void Scan_Audit_CarriesBuiltinRulePackVersion_WithoutSkill()
    {
        // 无 --skill→builtin 常量（与 check/run-regression 同口径）。
        var (code, _, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, code);
        var rec = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal("gb50001-2017.r1", p.RootElement.GetProperty("rulePackVersion").GetString());
    }
}
