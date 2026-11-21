using System.Text.Json;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan 2b-ii-b Task 10 end-to-end: electrical rule-package check → human adjudication
/// upgrades force → skill run template execution (lay-line + insert-block) → audit verify
/// → reproducibility evidence. The electrical package and the sample institute skill are
/// REPOSITORY SAMPLES copied into a temp project — adjudication and the extra insert-lum
/// template only touch the copies (repo samples stay pristine, asserted):
/// 1) fixture: valid cable "BV-3×2.5-PC20-WC" + bad-method cable "BV-3×2.5-XX20-WC" +
///    latin text "AL1" height 2.0 (&lt;2.5) + in-drawing BlockRecord "LUM" (one line inside);
/// 2) check --skill electrical → exit 0 (all recommended — decision power stays with the
///    human), issues cite GB/T 50786-2012 clauses 3.4.3 (slot) / 5.1.3 (height), the valid
///    cable is NOT flagged, no NoChecker skip (wiring complete);
/// 3) rule adjudicate --rule R-50786-3.4.3 --force mandatory --reason 院标升级 →
///    check --skill exit 2 (slot issue Mandatory, height issue still Recommended);
/// 4) skill run sample-institute --template lay-line {x1:0,y1:0,x2:100,y2:100} → exit 0,
///    line added, skill-run REQUEST audit (Source="skill") sequenced BEFORE tx-start,
///    tx-start/step/tx-commit present (StepEngine pipeline, no bypass);
/// 5) the copy gains steps/insert-lum.yaml (insert-block template, params x/y) → skill run
///    → Insert added referencing the in-drawing LUM block (cross-document symbol import
///    is deferred to plan 3; symbols.yaml metadata existence is NOT enforced);
/// 6) audit verify green; check-run paramsJson rulePackVersion contains
///    建筑电气制图（GB/T 50786-2012）@1.0.0; adjudicationHash changes with the adjudication
///    set (64-hex reproducibility evidence).
/// Plus: sample symbols metadata loads from the copy (LUM/lum.dxf, file absent);
/// insert-block on a drawing WITHOUT the block → audited failure (step-failed), no
/// Insert, no tx-commit, chain green.
/// </summary>
public class ElectricalSkillE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"elece2e-{Guid.NewGuid():N}");
    private readonly string _elecCopy;
    private readonly string _sampleCopy;
    private readonly string _drawing;

    public ElectricalSkillE2ETests()
    {
        Directory.CreateDirectory(_project);
        _elecCopy = Path.Combine(_project, "electrical");
        CopyDirectory(ElectricalDir(), _elecCopy);
        _sampleCopy = Path.Combine(_project, "sample-institute");
        CopyDirectory(SampleSkillDir(), _sampleCopy);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-PC20-WC", Height = 3.5 });  // 合规简写
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-XX20-WC", Height = 3.5 }); // 敷设方式 XX 非法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "AL1", Height = 2.0 });               // 字母字高 2.0 < 2.5 违规
            DxfFixture.AddLumBlock(doc); // 图纸内 BlockRecord "LUM"（内含一条线）——insert-block 夹具
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
    private static string SampleSkillDir() => Path.Combine(RepoRoot(), "examples", "skills", "sample-institute");

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

    private bool AuditChainGreen()
    {
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.VerifyChain(out _);
    }

    /// <summary>insert-lum 模板只写入测试副本（仓库示例 steps/ 仅 lay-line——零污染断言）。</summary>
    private void WriteInsertLumTemplate()
    {
        string stepsDir = Path.Combine(_sampleCopy, "steps");
        Directory.CreateDirectory(stepsDir);
        File.WriteAllText(Path.Combine(stepsDir, "insert-lum.yaml"), """
            # E2E 临时模板（仅测试副本，不入仓库示例）：insert-block 引用图纸内 LUM 块。
            templates:
              - name: insert-lum
                description: 在指定坐标插入图纸内 LUM 灯具块（E2E 演示模板）
                params: [x, y]
                sequence:
                  - kind: insert-block
                    params:
                      block: LUM
                      x: "{{x}}"
                      y: "{{y}}"
            """);
    }

    [Fact]
    public void FullLoop_ElectricalCheck_AdjudicateMandatory_SkillRunLineAndInsert_AuditGreen()
    {
        // 1) 电气副本可加载：清单/规则计数正确（4 规则，无别名）。
        var (vCode, vOut, _) = Run("skill", "validate", _elecCopy);
        Assert.Equal(0, vCode);
        using (var vDoc = JsonDocument.Parse(vOut))
        {
            Assert.Equal("建筑电气制图（GB/T 50786-2012）", vDoc.RootElement.GetProperty("name").GetString());
            Assert.Equal("1.0.0", vDoc.RootElement.GetProperty("version").GetString());
            Assert.Equal(4, vDoc.RootElement.GetProperty("ruleCount").GetInt32());
        }

        // 2) check --skill 电气包：全 recommended→exit 0；issues 引用 GB/T 50786-2012 条款；
        //    合规缆线不成任何 issue；接线完整（无 NoChecker 跳过）。
        var (code0, out0, err0) = Run("check", "--drawing", _drawing, "--skill", _elecCopy);
        Assert.Equal(0, code0);
        Assert.DoesNotContain("[NoChecker]", err0);
        using (var doc0 = JsonDocument.Parse(out0))
        {
            var issues0 = doc0.RootElement.GetProperty("issues").EnumerateArray().ToList();
            var slot0 = Assert.Single(issues0, i => i.GetProperty("ruleId").GetString() == "R-50786-3.4.3");
            Assert.Equal("GB/T 50786-2012", slot0.GetProperty("standard").GetString());
            Assert.Equal("3.4.3", slot0.GetProperty("clause").GetString());
            Assert.Equal("Recommended", slot0.GetProperty("force").GetString());
            Assert.Contains("XX", slot0.GetProperty("message").GetString());
            Assert.Contains("应按本标准表 4.2.1-1～表 4.2.1-3 的文字符号标注", slot0.GetProperty("excerpt").GetString());
            var height0 = Assert.Single(issues0, i => i.GetProperty("ruleId").GetString() == "R-50786-5.1.3");
            Assert.Equal("GB/T 50786-2012", height0.GetProperty("standard").GetString());
            Assert.Equal("5.1.3", height0.GetProperty("clause").GetString());
            Assert.Contains("AL1", height0.GetProperty("message").GetString());
            Assert.Contains("2.5mm", height0.GetProperty("message").GetString());
            // 合规缆线 BV-3×2.5-PC20-WC 不成任何 issue（含 builtin）；cable-format 无违规。
            Assert.DoesNotContain(issues0, i => i.GetProperty("ruleId").GetString() == "R-50786-4.1.5-8");
            Assert.DoesNotContain(issues0, i => i.GetProperty("message").GetString()!.Contains("BV-3×2.5-PC20-WC"));
            // builtin 5.0.2 字高系列同场生效（AL1 2.0 不在系列并集）——双层引擎共存，仍 recommended。
            var builtin = Assert.Single(issues0, i => i.GetProperty("ruleId").GetString() == "R-50001-5.0.2");
            Assert.Equal("Recommended", builtin.GetProperty("force").GetString());
            // builtin 4 跑 + 电气 4 跑 = 8；轴号/标高 NotEvaluable 跳过 3；无 mandatory。
            var summary0 = doc0.RootElement.GetProperty("summary");
            Assert.Equal(8, summary0.GetProperty("rulesRun").GetInt32());
            Assert.Equal(3, summary0.GetProperty("rulesSkipped").GetInt32());
            Assert.Equal(0, summary0.GetProperty("mandatoryViolations").GetInt32());
        }

        // 3) 人工裁定升级槽位规则 mandatory（院标升级）——孤儿校验须带 --skill 电气副本。
        var (adjCode, adjOut, _) = Run("rule", "adjudicate", "--rule", "R-50786-3.4.3",
            "--force", "mandatory", "--reason", "院标升级", "--skill", _elecCopy);
        Assert.Equal(0, adjCode);
        using (var adjDoc = JsonDocument.Parse(adjOut))
        {
            Assert.Equal("Mandatory", adjDoc.RootElement.GetProperty("forceOverride").GetString());
            Assert.False(adjDoc.RootElement.GetProperty("exempt").GetBoolean());
            Assert.Equal("院标升级", adjDoc.RootElement.GetProperty("reason").GetString());
        }

        // 4) check --skill：升级生效→exit 2；槽位 issue Mandatory，字高 issue 仍 Recommended。
        var (code2, out2, _) = Run("check", "--drawing", _drawing, "--skill", _elecCopy);
        Assert.Equal(2, code2);
        using (var doc2 = JsonDocument.Parse(out2))
        {
            var issues2 = doc2.RootElement.GetProperty("issues").EnumerateArray().ToList();
            var slot2 = Assert.Single(issues2, i => i.GetProperty("ruleId").GetString() == "R-50786-3.4.3");
            Assert.Equal("Mandatory", slot2.GetProperty("force").GetString());
            var height2 = Assert.Single(issues2, i => i.GetProperty("ruleId").GetString() == "R-50786-5.1.3");
            Assert.Equal("Recommended", height2.GetProperty("force").GetString());
            Assert.Equal(1, doc2.RootElement.GetProperty("summary").GetProperty("mandatoryViolations").GetInt32());
        }

        // 5) skill run lay-line：exit 0，画线成功（重载实体 3→4，端点即 args 透传值）；
        //    skill-run 请求审计先于 tx-start（无旁路：tx-start/step/tx-commit 全在链上）。
        var (runCode, runOut, _) = Run("skill", "run", _sampleCopy,
            "--template", "lay-line", "--drawing", _drawing,
            "--args", """{"x1":0,"y1":0,"x2":100,"y2":100}""");
        Assert.Equal(0, runCode);
        using (var runDoc = JsonDocument.Parse(runOut))
        {
            Assert.True(runDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, runDoc.RootElement.GetProperty("stepsDone").GetInt32());
            Assert.Equal("", runDoc.RootElement.GetProperty("error").GetString());
        }
        var afterLine = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(4, afterLine.Summarize().Entities.Count);
        Assert.Contains(afterLine.Doc.Entities.OfType<Line>(), l =>
            l.StartPoint.X == 0 && l.StartPoint.Y == 0 && l.EndPoint.X == 100 && l.EndPoint.Y == 100);

        // 6) 副本增 insert-lum 模板→skill run：exit 0，Insert 引用图纸内 LUM 块（重载 4→5）。
        WriteInsertLumTemplate();
        var (insCode, insOut, _) = Run("skill", "run", _sampleCopy,
            "--template", "insert-lum", "--drawing", _drawing, "--args", """{"x":10,"y":20}""");
        Assert.Equal(0, insCode);
        using (var insDoc = JsonDocument.Parse(insOut))
        {
            Assert.True(insDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, insDoc.RootElement.GetProperty("stepsDone").GetInt32());
        }
        var afterInsert = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(5, afterInsert.Summarize().Entities.Count);
        var insert = Assert.Single(afterInsert.Doc.Entities.OfType<Insert>());
        Assert.Equal("LUM", insert.Block.Name);
        Assert.Equal(10, insert.InsertPoint.X, 3);
        Assert.Equal(20, insert.InsertPoint.Y, 3);
        Assert.Equal(1, insert.XScale, 3);
        Assert.Equal(0, insert.Rotation, 3);

        // 7) 审计形状：两次 skill-run 请求（Source="skill"，paramsJson 四字段）各自先于同
        //    planId 的 tx-start；tx-start/step/tx-commit 各 2；裁定留痕 forceOverride/reason。
        Assert.True(AuditChainGreen());
        var records = AuditRecords();
        var requests = records.Where(r => r.Entry.Kind == "skill-run").ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal("skill", r.Entry.Source));
        var layReq = Assert.Single(requests, r => r.Entry.ParamsJson.Contains("\"template\":\"lay-line\""));
        var insReq = Assert.Single(requests, r => r.Entry.ParamsJson.Contains("\"template\":\"insert-lum\""));
        string layPlanId, insPlanId;
        using (var p = JsonDocument.Parse(layReq.Entry.ParamsJson))
        {
            Assert.Equal(_sampleCopy, p.RootElement.GetProperty("skill").GetString());
            Assert.Equal(100, p.RootElement.GetProperty("args").GetProperty("x2").GetInt32());
            layPlanId = p.RootElement.GetProperty("planId").GetString()!;
        }
        using (var p = JsonDocument.Parse(insReq.Entry.ParamsJson))
        {
            Assert.Equal(10, p.RootElement.GetProperty("args").GetProperty("x").GetInt32());
            insPlanId = p.RootElement.GetProperty("planId").GetString()!;
        }
        Assert.StartsWith("lay-line-", layPlanId);
        Assert.StartsWith("insert-lum-", insPlanId);
        Assert.Equal(layPlanId, layReq.Entry.PlanRef);
        Assert.Equal(insPlanId, insReq.Entry.PlanRef);
        foreach (string planId in new[] { layPlanId, insPlanId })
        {
            var txStart = Assert.Single(records, r => r.Entry.Kind == "tx-start" && r.Entry.PlanRef == planId);
            var txCommit = Assert.Single(records, r => r.Entry.Kind == "tx-commit" && r.Entry.PlanRef == planId);
            Assert.Single(records, r => r.Entry.Kind == "step" && r.Entry.PlanRef == planId);
            var req = Assert.Single(requests, r => r.Entry.PlanRef == planId);
            Assert.True(req.Seq < txStart.Seq); // 请求审计先于执行
            Assert.True(txStart.Seq < txCommit.Seq);
        }
        var adjudicated = Assert.Single(records, r => r.Entry.Kind == "rule-adjudicated");
        Assert.Equal("rule", adjudicated.Entry.Source);
        Assert.Equal("R-50786-3.4.3", adjudicated.Entry.Target);
        using (var p = JsonDocument.Parse(adjudicated.Entry.ParamsJson))
        {
            Assert.Equal("Mandatory", p.RootElement.GetProperty("forceOverride").GetString());
            Assert.Equal("院标升级", p.RootElement.GetProperty("reason").GetString());
        }

        // 8) audit verify 绿；check-run 复现证据：rulePackVersion 含电气包@1.0.0（builtin 常量
        //    前缀），adjudicationHash 随裁定集变化（两次 check 各 64 位 hex，互不相同）。
        var (verifyCode, verifyOut, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
        Assert.Contains("审计链完整", verifyOut);
        var checkRuns = records.Where(r => r.Entry.Source == "check" && r.Entry.Kind == "check-run")
            .OrderBy(r => r.Seq).ToList();
        Assert.Equal(2, checkRuns.Count);
        string[] hashes = new string[2];
        for (int i = 0; i < 2; i++)
        {
            using var p = JsonDocument.Parse(checkRuns[i].Entry.ParamsJson);
            string pack = p.RootElement.GetProperty("rulePackVersion").GetString()!;
            Assert.StartsWith("gb50001-2017.r1|", pack);
            Assert.Contains("建筑电气制图（GB/T 50786-2012）@1.0.0", pack);
            hashes[i] = p.RootElement.GetProperty("adjudicationHash").GetString()!;
            Assert.Matches("^[0-9A-F]{64}$", hashes[i]);
        }
        Assert.NotEqual(hashes[0], hashes[1]); // 裁定集变化→指纹变化

        // 9) 入库示例零污染：insert-lum 只在副本；仓库电气规则 force 仍 recommended；
        //    仓库 sample steps/ 仍仅 lay-line；symbols.yaml 为入库示例资产（副本同源）。
        Assert.Single(Directory.GetFiles(Path.Combine(SampleSkillDir(), "steps")),
            f => Path.GetFileName(f) == "lay-line.yaml");
        Assert.False(File.Exists(Path.Combine(SampleSkillDir(), "steps", "insert-lum.yaml")));
        Assert.Contains("force: recommended",
            File.ReadAllText(Path.Combine(ElectricalDir(), "rules", "R-50786-3.4.3.yaml")));
        Assert.True(File.Exists(Path.Combine(SampleSkillDir(), "symbols", "symbols.yaml")));
    }

    [Fact]
    public void SampleSymbols_MetadataLoads_FileExistenceNotEnforced()
    {
        // 示例符号元数据（入库资产）：LUM→lum.dxf；lum.dxf 未随包提供——本 slice 仅校验
        // 元数据，文件存在性不校验（跨文档符号导入留计划3）。class 取 Auxiliary：
        // SemanticClass 尚无 Lighting 成员（布灯语义类随计划3布灯处理器引入）。
        var pkg = CadAgent.Core.SkillLoader.LoadDirectory(_sampleCopy);
        var sym = Assert.Single(pkg.Symbols);
        Assert.Equal("LUM", sym.Name);
        Assert.Equal("lum.dxf", sym.File);
        Assert.Equal(CadAgent.Core.SemanticClass.Auxiliary, sym.Class);
        Assert.Equal("示例灯具块（元数据演示，跨文档导入计划3）", sym.Description);
        Assert.False(File.Exists(Path.Combine(_sampleCopy, "symbols", "lum.dxf")));
        // 符号元数据不影响清单/规则/别名计数（加性加载）。
        var (code, stdout, _) = Run("skill", "validate", _sampleCopy);
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("示例院标", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("1.0.0", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("ruleCount").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("aliasCount").GetInt32());
    }

    [Fact]
    public void SkillRun_InsertLum_BlockMissing_AuditedFailure_NoInsert()
    {
        // 图纸内块缺失→处理器失败结果（跨文档导入留计划3）：exit 1、step-failed 留痕、
        // 无 tx-commit（未落盘）、图纸无 Insert、链绿。
        WriteInsertLumTemplate();
        string plain = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        try
        {
            var (code, stdout, _) = Run("skill", "run", _sampleCopy,
                "--template", "insert-lum", "--drawing", plain, "--args", """{"x":0,"y":0}""");
            Assert.Equal(1, code);
            using (var doc = JsonDocument.Parse(stdout))
            {
                Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Contains("块不存在", doc.RootElement.GetProperty("error").GetString());
            }
            Assert.Empty(CadAgent.Data.DrawingSession.Load(plain).Doc.Entities.OfType<Insert>());
            Assert.True(AuditChainGreen());
            var records = AuditRecords();
            Assert.Single(records, r => r.Entry.Kind == "skill-run"); // 请求留痕不丢
            Assert.Single(records, r => r.Entry.Kind == "step-failed");
            Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
            Assert.DoesNotContain(records, r => r.Entry.Kind == "step");
        }
        finally
        {
            File.Delete(plain);
        }
    }
}
