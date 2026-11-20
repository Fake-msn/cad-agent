using System.Text.Json;
using System.Text.RegularExpressions;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan 2b-ii-a end-to-end loop (Task 8). The sample institute skill under
/// examples/skills/sample-institute is a REPOSITORY SAMPLE, NOT a real institute
/// standard (real standards await user delivery, spec §12 open item). It is copied
/// into a temp project so promote mutates the copy, never the repo asset:
/// skill validate → semantic scan --skill (alias hit removes ambiguity) →
/// check --skill exit 2 (SK-SAMPLE-001 mandatory, mixed Han/Latin layers) →
/// rule adjudicate --exempt the skill rule → check exit 0 → human map of the
/// ambiguous layer → semantic promote into the copy (alias append + patch bump
/// 1.0.0→1.0.1) → audit verify green (semantic-promoted present, paramsJson
/// {layer,class,skill,newVersion}) → check audit paramsJson carries adjudicationHash
/// (64 hex) + rulePackVersion containing 示例院标@1.0.1 → re-run check with the same
/// adjudication set → identical adjudicationHash (fingerprint reproducibility).
/// Coverage additions (T7 review minors): skill list with one invalid package
/// (valid listed + stderr warning + exit 0); check --skill Id collision leaves no
/// check-run audit row (validation precedes side effects).
/// </summary>
public class SkillE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"skille2e-{Guid.NewGuid():N}");
    private readonly string _skillCopy;
    private readonly string _drawing;

    public SkillE2ETests()
    {
        Directory.CreateDirectory(_project);
        _skillCopy = Path.Combine(_project, "sample-institute");
        CopyDirectory(SampleSkillDir(), _skillCopy);
        // W-示例墙：skill 别名→Wall（scan 不再不明确）；汉英混用→SK-SAMPLE-001 违规。
        // Z-示例柱：无别名→ambiguous（待人工 map→Column 后 promote 进 skill 副本）；同样混用。
        // 两线不平行（一横一竖），避免墙对检测器引入额外标注。
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("W-示例墙"));
            doc.Layers.Add(new Layer("Z-示例柱"));
            var wall = doc.Layers.First(l => l.Name == "W-示例墙");
            var column = doc.Layers.First(l => l.Name == "Z-示例柱");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 3000), EndPoint = (XYZ)new XY(0, 3500), Layer = column });
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

    /// <summary>仓库根：从测试程序集目录上溯找 CadAgent.sln（示例 skill 为入库文件，非嵌入资源）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string SampleSkillDir() =>
        Path.Combine(RepoRoot(), "examples", "skills", "sample-institute");

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    private List<CadAgent.Core.AuditRecord> AuditRecords()
    {
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    private static List<JsonElement> CheckRuns(IEnumerable<CadAgent.Core.AuditRecord> records) =>
        records.Where(r => r.Entry.Source == "check" && r.Entry.Kind == "check-run")
            .Select(r => JsonDocument.Parse(r.Entry.ParamsJson).RootElement.Clone()).ToList();

    [Fact]
    public void FullLoop_Validate_Scan_Check_Exempt_Map_Promote_Audit_FingerprintReuse()
    {
        // 1) skill validate：副本可加载，清单/规则/别名计数正确。
        var (vCode, vOut, _) = Run("skill", "validate", _skillCopy);
        Assert.Equal(0, vCode);
        using (var vDoc = JsonDocument.Parse(vOut))
        {
            Assert.Equal("示例院标", vDoc.RootElement.GetProperty("name").GetString());
            Assert.Equal("1.0.0", vDoc.RootElement.GetProperty("version").GetString());
            Assert.Equal(1, vDoc.RootElement.GetProperty("ruleCount").GetInt32());
            Assert.Equal(1, vDoc.RootElement.GetProperty("aliasCount").GetInt32());
        }

        // 2) scan --skill：别名命中→W-示例墙 不再不明确，且标注证据确为 skill 别名
        //    （非约定库——W-示例墙 不在 ConventionLibrary）；Z-示例柱 无别名→ambiguous。
        var (scanCode, scanOut, _) = Run("semantic", "scan", "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(0, scanCode);
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        using (var scanDoc = JsonDocument.Parse(scanOut))
        {
            var ambiguous = scanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
                .Select(a => a.GetProperty("layerName").GetString()).ToList();
            Assert.DoesNotContain("W-示例墙", ambiguous);
            Assert.Contains("Z-示例柱", ambiguous);
            var annotations = scanDoc.RootElement.GetProperty("annotations").EnumerateArray().ToList();
            Assert.Contains(annotations, a =>
                a.GetProperty("class").GetString() == "Wall" &&
                a.GetProperty("provenance").GetString() == "LayerInference" &&
                a.GetProperty("evidence").GetString() == "skill别名: W-示例墙");
        }

        // 3) check --skill：SK-SAMPLE-001 mandatory 生效——两层汉英混用各 1 项→exit 2；
        //    引用义务：issue 携带示例来源（非真实院标，明示）。
        var (checkCode, checkOut, _) = Run("check", "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(2, checkCode);
        using (var checkDoc = JsonDocument.Parse(checkOut))
        {
            var skIssues = checkDoc.RootElement.GetProperty("issues").EnumerateArray()
                .Where(i => i.GetProperty("ruleId").GetString() == "SK-SAMPLE-001").ToList();
            Assert.Equal(2, skIssues.Count);
            Assert.All(skIssues, i =>
            {
                Assert.Equal("Mandatory", i.GetProperty("force").GetString());
                Assert.Equal("示例院标（非真实）", i.GetProperty("standard").GetString());
            });
            Assert.Equal(2, checkDoc.RootElement.GetProperty("summary").GetProperty("mandatoryViolations").GetInt32());
        }

        // 4) 豁免该 skill 规则（--skill 必带：孤儿校验须能识别 SK-SAMPLE-001）。
        var (adjCode, _, _) = Run("rule", "adjudicate", "--rule", "SK-SAMPLE-001",
            "--exempt", "--reason", "示例项目图层约定另定（E2E 演示裁定）", "--skill", _skillCopy);
        Assert.Equal(0, adjCode);

        // 5) check --skill：豁免生效→exit 0（builtin 13.0.1 仍 recommended，不影响退出码）；
        //    豁免规则 Id 从 stdout 移除，结构化跳过走 stderr（不静默）。
        var (code0, out0, err0) = Run("check", "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(0, code0);
        Assert.DoesNotContain("SK-SAMPLE-001", out0);
        Assert.Contains("跳过", err0);
        Assert.Contains("[Exempt]", err0);
        Assert.Contains("SK-SAMPLE-001", err0);
        Assert.Contains("R-50001-13.0.1", out0); // builtin recommended 仍在（决策权归人）

        // 6) 人工确认 ambiguous 层→Column（human > skill > builtin 优先级的人工一环）。
        var (mapCode, _, _) = Run("semantic", "map", "--layer", "Z-示例柱", "--class", "Column", "--drawing", _drawing);
        Assert.Equal(0, mapCode);

        // 7) promote 到副本：别名追加（原 W-示例墙→Wall 保留）+ manifest patch bump 1.0.0→1.0.1。
        var (promCode, promOut, _) = Run("semantic", "promote", "--layer", "Z-示例柱", "--skill", _skillCopy);
        Assert.Equal(0, promCode);
        using (var promDoc = JsonDocument.Parse(promOut))
        {
            Assert.Equal("Z-示例柱", promDoc.RootElement.GetProperty("layer").GetString());
            Assert.Equal("Column", promDoc.RootElement.GetProperty("class").GetString());
            Assert.Equal("1.0.1", promDoc.RootElement.GetProperty("newVersion").GetString());
        }
        var promoted = CadAgent.Core.SkillLoader.LoadDirectory(_skillCopy); // 升版后仍可加载
        Assert.Equal("1.0.1", promoted.Manifest.Version);
        Assert.Equal(2, promoted.Aliases.Count);
        Assert.Contains(promoted.Aliases, a => a.LayerName == "W-示例墙" && a.Class == CadAgent.Core.SemanticClass.Wall);
        Assert.Contains(promoted.Aliases, a => a.LayerName == "Z-示例柱" && a.Class == CadAgent.Core.SemanticClass.Column);
        var (v2Code, v2Out, _) = Run("skill", "validate", _skillCopy);
        Assert.Equal(0, v2Code);
        using (var v2Doc = JsonDocument.Parse(v2Out))
        {
            Assert.Equal("1.0.1", v2Doc.RootElement.GetProperty("version").GetString());
            Assert.Equal(2, v2Doc.RootElement.GetProperty("aliasCount").GetInt32());
        }

        // 8) audit verify 绿：semantic-promoted 在链上，paramsJson 四字段精确
        //    （T7 评审 minor 覆盖：{layer,class,skill,newVersion}）。
        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
        var records = AuditRecords();
        var promotedRec = Assert.Single(records, r => r.Entry.Kind == "semantic-promoted");
        Assert.Equal("semantic", promotedRec.Entry.Source);
        Assert.Equal("Z-示例柱", promotedRec.Entry.Target);
        using (var promParams = JsonDocument.Parse(promotedRec.Entry.ParamsJson))
        {
            Assert.Equal("Z-示例柱", promParams.RootElement.GetProperty("layer").GetString());
            Assert.Equal("Column", promParams.RootElement.GetProperty("class").GetString());
            Assert.Equal(_skillCopy, promParams.RootElement.GetProperty("skill").GetString());
            Assert.Equal("1.0.1", promParams.RootElement.GetProperty("newVersion").GetString());
        }

        // 9) promote 后 check：裁定仍生效（exit 0）；审计 paramsJson 复现证据——
        //    adjudicationHash 64 位 hex + rulePackVersion 含 示例院标@1.0.1。
        var (codeA, _, _) = Run("check", "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(0, codeA);
        var runsA = CheckRuns(AuditRecords());
        Assert.Equal(3, runsA.Count); // 步骤 3/5/9 各一次 check
        string hashA = runsA[2].GetProperty("adjudicationHash").GetString()!;
        Assert.Matches("^[0-9A-F]{64}$", hashA);
        string packA = runsA[2].GetProperty("rulePackVersion").GetString()!;
        Assert.Contains("示例院标@1.0.1", packA);
        Assert.StartsWith("gb50001-2017.r1|", packA);
        // 指纹与规则包版本无关：豁免后（升版前）的 check 与升版后的 check 指纹一致。
        Assert.Equal(hashA, runsA[1].GetProperty("adjudicationHash").GetString());
        Assert.Contains("示例院标@1.0.0", runsA[1].GetProperty("rulePackVersion").GetString());

        // 10) 指纹复用：同裁定集重跑 check→adjudicationHash 逐字一致（复现证据可复用）。
        var (codeB, _, _) = Run("check", "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(0, codeB);
        var runsB = CheckRuns(AuditRecords());
        Assert.Equal(4, runsB.Count);
        Assert.Equal(hashA, runsB[3].GetProperty("adjudicationHash").GetString());
        Assert.Equal(packA, runsB[3].GetProperty("rulePackVersion").GetString());
        using (var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db")))
            Assert.True(audit.VerifyChain(out _));

        // 11) 入库示例零污染：promote 只改副本——仓库 examples/ 的清单仍 1.0.0、别名仍仅 W-示例墙。
        string repoManifest = File.ReadAllText(Path.Combine(SampleSkillDir(), "skill.yaml"));
        Assert.Contains("version: \"1.0.0\"", repoManifest);
        string repoAliases = File.ReadAllText(Path.Combine(SampleSkillDir(), "aliases.yaml"));
        Assert.Contains("W-示例墙", repoAliases);
        Assert.DoesNotContain("Z-示例柱", repoAliases);
    }

    [Fact]
    public void SkillList_OneInvalidPackage_ValidListed_Warning_ExitZero()
    {
        // T7 评审 minor 覆盖：list 属信息性命令——坏包跳过 + stderr 警告，好包照列，exit 0。
        string skillsRoot = Path.Combine(_project, ".cadagent", "skills");
        CopyDirectory(SampleSkillDir(), Path.Combine(skillsRoot, "sample-institute"));
        string broken = Path.Combine(skillsRoot, "broken-package");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "skill.yaml"), """
            name: 坏包
            version: abc
            discipline: architecture
            drawingType: any
            description: 版本非法的示例坏包
            """);
        var (code, stdout, stderr) = Run("skill", "list");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("示例院标", entry.GetProperty("name").GetString());
        Assert.Equal("1.0.0", entry.GetProperty("version").GetString());
        Assert.Contains("警告", stderr);
        Assert.Contains("broken-package", stderr);
    }

    [Fact]
    public void Check_SkillIdCollision_NoCheckRunAuditRow()
    {
        // T7 评审 minor 覆盖：Id 冲突→用法错误先于 OpenAudit/Load——无 check-run 审计行。
        string collisionDir = Path.Combine(_project, "collision-skill");
        CopyDirectory(SampleSkillDir(), collisionDir);
        string rulePath = Path.Combine(collisionDir, "rules", "layer-charset.yaml");
        File.WriteAllText(rulePath, File.ReadAllText(rulePath).Replace("SK-SAMPLE-001", "R-50001-13.0.1"));
        var (code, _, stderr) = Run("check", "--drawing", _drawing, "--skill", collisionDir);
        Assert.Equal(1, code);
        Assert.Contains("R-50001-13.0.1", stderr);
        Assert.Contains("duplicate", stderr);
        // 校验先于副作用：audit.db 可能根本未创建；若存在则不得含任何 check-run 行。
        string auditDb = Path.Combine(_project, ".cadagent", "audit.db");
        if (File.Exists(auditDb))
        {
            using var audit = new CadAgent.Core.SqliteAuditStore(auditDb);
            Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "check-run");
        }
    }
}
