using System.Text.Json;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan 2b-i end-to-end loop (Task 8): fixture with four violations (text height
/// 4.2, mixed Han/Latin layer "W-墙体", axis letter "I", elevation "+3.000") →
/// semantic scan → human map of the axis-text / elevation-text layers →
/// check exit 0 (all recommended, issues cite GB/T 50001-2017) → adjudicate
/// upgrade text-height rule to mandatory → check exit 2 → adjudicate exempt the
/// mixed-layer rule → its issue gone from stdout (skip reason on stderr) →
/// run geometric plan succeeds with run-regression warning (no auto-rollback) →
/// audit verify green → adjudications persist across a fresh check invocation.
/// The axis/elevation texts sit on named custom layers so the scan→map command
/// chain (not a hand-written overlay) establishes their semantics.
/// </summary>
public class CheckE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"chke2e-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public CheckE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("W-墙体"));   // 13.0.1 汉英混用
            doc.Layers.Add(new Layer("轴号文字"));  // 自定义层：scan 报 ambiguous，待人工 map→Axis
            doc.Layers.Add(new Layer("标高标注"));  // 自定义层：scan 报 ambiguous，待人工 map→Annotation
            var wall = doc.Layers.First(l => l.Name == "W-墙体");
            var axisText = doc.Layers.First(l => l.Name == "轴号文字");
            var elevText = doc.Layers.First(l => l.Name == "标高标注");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = wall });
            // 5.0.2：4.2 不在两系列并集内；其余文本取系列值，隔离违规源。
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 500), Value = "设计说明", Height = 4.2 });
            // 8.0.4：I 为禁用字母（非数字，几何推断不会标 Axis——语义只能来自人工 map）。
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(5000, 0), Value = "I", Height = 5, Layer = axisText });
            // 11.8.5：正数标高带 "+"；3 位小数满足 11.8.4。
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(8000, 0), Value = "+3.000", Height = 3.5, Layer = elevText });
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

    private static List<(string RuleId, string Force, string Standard)> Issues(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("issues").EnumerateArray()
            .Select(i => (i.GetProperty("ruleId").GetString()!,
                          i.GetProperty("force").GetString()!,
                          i.GetProperty("standard").GetString()!))
            .ToList();
    }

    private static int MandatoryViolations(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("summary").GetProperty("mandatoryViolations").GetInt32();
    }

    [Fact]
    public void FullLoop_ScanMap_Check_Adjudicate_RunRegression_Audit_Persistence()
    {
        // 1) scan：overlay 落地；轴号/标高自定义层进入 ambiguous 证据清单。
        var (scanCode, scanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        using (var scanDoc = JsonDocument.Parse(scanOut))
        {
            var ambiguous = scanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
                .Select(a => a.GetProperty("layerName").GetString()).ToList();
            Assert.Contains("轴号文字", ambiguous);
            Assert.Contains("标高标注", ambiguous);
        }

        // 2) map：人工确认轴号层→Axis、标高标注层→Annotation（HumanConfirmed 1.0）。
        var (mapAxisCode, _, _) = Run("semantic", "map", "--layer", "轴号文字", "--class", "Axis", "--drawing", _drawing);
        Assert.Equal(0, mapAxisCode);
        var (mapElevCode, _, _) = Run("semantic", "map", "--layer", "标高标注", "--class", "Annotation", "--drawing", _drawing);
        Assert.Equal(0, mapElevCode);

        // 3) check：四类违规全部检出且引用 GB/T 50001-2017；全 recommended → exit 0。
        var (checkCode, checkOut, checkErr) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, checkCode);
        Assert.Equal("", checkErr); // 无跳过、无警告
        var issues = Issues(checkOut);
        Assert.Contains(issues, i => i.RuleId == "R-50001-5.0.2");   // 字高 4.2
        Assert.Contains(issues, i => i.RuleId == "R-50001-13.0.1");  // W-墙体 混用
        Assert.Contains(issues, i => i.RuleId == "R-50001-8.0.4");   // 轴号 I
        Assert.Contains(issues, i => i.RuleId == "R-50001-11.8.5");  // +3.000
        Assert.All(issues, i => Assert.Equal("GB/T 50001-2017", i.Standard)); // 引用义务
        Assert.All(issues, i => Assert.Equal("Recommended", i.Force));        // 默认零拦截
        Assert.Equal(0, MandatoryViolations(checkOut));

        // 4) 裁定升级字高规则→mandatory：check exit 2，该规则 issue 生效强制力覆写。
        var (upCode, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-5.0.2",
            "--force", "mandatory", "--reason", "院标升级：字高强制");
        Assert.Equal(0, upCode);
        var (code2, out2, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(2, code2);
        Assert.True(MandatoryViolations(out2) >= 1);
        Assert.All(Issues(out2).Where(i => i.RuleId == "R-50001-5.0.2"),
            i => Assert.Equal("Mandatory", i.Force));

        // 5) 豁免混用层规则：issue 从 stdout 消失，跳过原因走 stderr（不静默）。
        var (exCode, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-13.0.1",
            "--exempt", "--reason", "本项目图层约定另定");
        Assert.Equal(0, exCode);
        var (code3, out3, err3) = Run("check", "--drawing", _drawing);
        Assert.Equal(2, code3); // 字高 mandatory 仍在
        Assert.DoesNotContain("R-50001-13.0.1", out3);
        Assert.Contains("跳过", err3);
        Assert.Contains("R-50001-13.0.1", err3);

        // 6) run 几何计划：成功（exit 0）+ 回归警告（4.2 字高违规未被 add-line 修复）；
        //    不自动回滚——新线保留，决策权归人。
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        int linesBefore = CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Line>().Count();
        var (runCode, runOut, runErr) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, runCode);
        Assert.Contains("\"ok\":true", runOut.Replace(" ", ""));
        Assert.Contains("警告", runErr);
        Assert.Contains("R-50001-5.0.2", runErr);
        Assert.Contains("不自动回滚", runErr);
        int linesAfter = CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Line>().Count();
        Assert.Equal(linesBefore + 1, linesAfter); // 事务已提交，未被回归检查回滚

        // 7) 审计：链完整；tx-commit 在、无 rollback；run-regression check-run 留痕。
        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
        using (var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db")))
        {
            var records = audit.ReadAll();
            Assert.Contains(records, r => r.Entry.Kind == "tx-commit");
            Assert.DoesNotContain(records, r => r.Entry.Kind == "rollback");
            var regression = Assert.Single(records, r => r.Entry.Source == "run-regression" && r.Entry.Kind == "check-run");
            using var regParams = JsonDocument.Parse(regression.Entry.ParamsJson);
            Assert.True(regParams.RootElement.GetProperty("mandatoryViolations").GetInt32() >= 1);
            Assert.True(audit.VerifyChain(out _));
        }

        // 8) 裁定持久化：磁盘 JSON 含升级与豁免；全新 check 调用仍生效（exit 2 + 豁免移除）。
        string adjJson = File.ReadAllText(Path.Combine(_project, ".cadagent", "adjudications.json"));
        Assert.Contains("R-50001-5.0.2", adjJson);
        Assert.Contains("Mandatory", adjJson);
        Assert.Contains("R-50001-13.0.1", adjJson);
        var (code4, out4, err4) = Run("check", "--drawing", _drawing);
        Assert.Equal(2, code4);
        Assert.All(Issues(out4).Where(i => i.RuleId == "R-50001-5.0.2"),
            i => Assert.Equal("Mandatory", i.Force));
        Assert.DoesNotContain("R-50001-13.0.1", out4);
        Assert.Contains("R-50001-13.0.1", err4);
    }
}

/// <summary>
/// Run-regression (additive to CommandRouter.RunPlan): after a successful tx the
/// builtin rules are evaluated against the post-run drawing; NEW mandatory
/// violations (including pre-existing ones the run did not fix) produce a stderr
/// warning — never an auto-rollback — and a check-run audit record with
/// Source="run-regression". Regression trouble must not change the run result.
/// </summary>
public class RunRegressionTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"runreg-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _plan;
    public RunRegressionTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            DxfFixture.AddSampleLine(doc);
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "字高违规", Height = 4.2 });
        });
        _plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(_plan, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
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
    public void Run_MandatoryViolationAfterTx_WarnsNotRollbacks_Audited()
    {
        // 升级字高规则；fixture 文本 4.2 已违规，add-line 不修复 → 回归警告应触发。
        var (ac, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-5.0.2",
            "--force", "mandatory", "--reason", "院标升级");
        Assert.Equal(0, ac);
        var (code, stdout, stderr) = Run("run", _plan, "--drawing", _drawing);
        Assert.Equal(0, code); // 警告不改退出码——决策权归人
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        Assert.Contains("警告", stderr);
        Assert.Contains("R-50001-5.0.2", stderr);
        Assert.Contains("不自动回滚", stderr);
        // 未回滚：新线仍在图上。
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Line>().Count());
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var records = audit.ReadAll();
        Assert.Contains(records, r => r.Entry.Kind == "tx-commit");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "rollback");
        var regression = Assert.Single(records, r => r.Entry.Source == "run-regression" && r.Entry.Kind == "check-run");
        using var regParams = JsonDocument.Parse(regression.Entry.ParamsJson);
        Assert.True(regParams.RootElement.GetProperty("mandatoryViolations").GetInt32() >= 1);
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_AllRecommended_NoWarning_StillAudited()
    {
        // 无裁定：全 recommended → 无回归警告，但 check-run（run-regression）仍留痕。
        var (code, stdout, stderr) = Run("run", _plan, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        Assert.DoesNotContain("不自动回滚", stderr);
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var regression = Assert.Single(audit.ReadAll(),
            r => r.Entry.Source == "run-regression" && r.Entry.Kind == "check-run");
        using var regParams = JsonDocument.Parse(regression.Entry.ParamsJson);
        Assert.Equal(0, regParams.RootElement.GetProperty("mandatoryViolations").GetInt32());
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_CorruptAdjudications_RegressionRefusedAudited_RunStillSucceeds()
    {
        // 回归检查是加性观察：裁定文件损坏→审计拒绝（Source=run-regression），
        // 但已提交事务不受影响（exit 0、ok:true、不回滚）。
        string adjPath = Path.Combine(_project, ".cadagent", "adjudications.json");
        Directory.CreateDirectory(Path.GetDirectoryName(adjPath)!);
        File.WriteAllText(adjPath, "{corrupt json!!");
        var (code, stdout, stderr) = Run("run", _plan, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        Assert.NotEmpty(stderr);
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Doc.Entities.OfType<Line>().Count());
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var records = audit.ReadAll();
        Assert.Contains(records, r => r.Entry.Kind == "tx-commit");
        Assert.Contains(records, r => r.Entry.Kind == "intake-rejected"
            && r.Entry.Source == "run-regression" && r.Entry.Target == adjPath);
        Assert.True(audit.VerifyChain(out _));
    }
}