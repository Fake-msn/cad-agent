using System.Text.Json;
using CadAgent.Data.Tests;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan 2b-ii-b Task 8: skill run (template instantiation → StepEngine execution,
/// audited + gated) and run --skill validation wiring.
/// The sample institute skill under examples/skills/sample-institute is a REPOSITORY
/// SAMPLE (not a real institute standard); it is copied into a temp project so tests
/// never mutate the repo asset. Coverage:
/// - skill run lay-line success: exit 0, line added (reload entity count +1), audit
///   chain green with skill-run REQUEST (Source="skill", paramsJson {skill,template,
///   args,planId}) sequenced BEFORE tx-start, plus tx-start/step/tx-commit records.
/// - missing template → exit 1, no execution; missing args key → exit 1 audited
///   (LoadOrReject-style intake-rejected, Source="skill"); invalid args JSON → exit 1.
/// - invalid package / missing drawing → audited refusal; corrupt overlay → audited
///   refusal with no execution (gate parity with run); overlay present → geometric
///   template still succeeds (SemanticGate injection parity).
/// - run --skill: Id collision → exit 1 usage-error (no tx audit, drawing untouched);
///   valid package → tx-start/tx-commit paramsJson carry planId + rulePackVersion;
///   without --skill tx paramsJson stays the raw planId (additive behavior guard).
/// </summary>
public class SkillRunTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"skillrun-{Guid.NewGuid():N}");
    private readonly string _skillCopy;
    private readonly string _drawing;

    public SkillRunTests()
    {
        Directory.CreateDirectory(_project);
        _skillCopy = Path.Combine(_project, "sample-institute");
        CopyDirectory(SampleSkillDir(), _skillCopy);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
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

    private bool AuditChainGreen()
    {
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.VerifyChain(out _);
    }

    private string WritePlan(string planId = "p1")
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"PLANID","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """.Replace("PLANID", planId));
        return planPath;
    }

    private const string LayLineArgs = """{"x1":0,"y1":0,"x2":50,"y2":50}""";

    [Fact]
    public void SkillRun_LayLine_AddsLine_RequestAuditBeforeTx_ChainGreen()
    {
        var (code, stdout, _) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", LayLineArgs);
        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(stdout))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("stepsDone").GetInt32());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("txId").GetString()));
            Assert.Equal("", doc.RootElement.GetProperty("error").GetString());
        }
        // 图纸重载：实体 +1，且新线端点即 args 透传值（数值未被 coerce 成字符串）。
        var session = CadAgent.Data.DrawingSession.Load(_drawing);
        Assert.Equal(2, session.Summarize().Entities.Count);
        Assert.Contains(session.Doc.Entities.OfType<Line>(), l =>
            l.StartPoint.X == 0 && l.StartPoint.Y == 0 && l.EndPoint.X == 50 && l.EndPoint.Y == 50);

        Assert.True(AuditChainGreen());
        var records = AuditRecords();
        // skill-run 请求审计：Source="skill"，paramsJson 四字段，先于任何 tx 记录。
        var request = Assert.Single(records, r => r.Entry.Kind == "skill-run");
        Assert.Equal("skill", request.Entry.Source);
        string planId;
        using (var p = JsonDocument.Parse(request.Entry.ParamsJson))
        {
            Assert.Equal(_skillCopy, p.RootElement.GetProperty("skill").GetString());
            Assert.Equal("lay-line", p.RootElement.GetProperty("template").GetString());
            Assert.Equal(50, p.RootElement.GetProperty("args").GetProperty("x2").GetInt32());
            planId = p.RootElement.GetProperty("planId").GetString()!;
        }
        Assert.StartsWith("lay-line-", planId);
        Assert.Equal(planId, request.Entry.PlanRef);
        var txStart = Assert.Single(records, r => r.Entry.Kind == "tx-start");
        var txCommit = Assert.Single(records, r => r.Entry.Kind == "tx-commit");
        Assert.Single(records, r => r.Entry.Kind == "step");
        Assert.True(request.Seq < txStart.Seq); // 请求审计先于执行
        Assert.Equal(planId, txStart.Entry.PlanRef);
        Assert.Equal(planId, txCommit.Entry.PlanRef);
        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
    }

    [Fact]
    public void SkillRun_MissingTemplate_ExitOne_NoExecution()
    {
        byte[] before = File.ReadAllBytes(_drawing);
        var (code, stdout, stderr) = Run("skill", "run", _skillCopy,
            "--template", "no-such-template", "--drawing", _drawing, "--args", LayLineArgs);
        Assert.Equal(1, code);
        Assert.Contains("no-such-template", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        var records = AuditRecords();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "skill-run");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void SkillRun_MissingArgsKey_ExitOne_AuditedRefusal()
    {
        byte[] before = File.ReadAllBytes(_drawing);
        var (code, _, stderr) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", """{"x1":0,"y1":0,"x2":50}""");
        Assert.Equal(1, code);
        Assert.Contains("y2", stderr);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        Assert.True(AuditChainGreen());
        var records = AuditRecords();
        var refusal = Assert.Single(records, r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("skill", refusal.Entry.Source);
        Assert.Contains("missing args", refusal.Entry.ParamsJson);
        Assert.Contains("y2", refusal.Entry.ParamsJson);
        Assert.DoesNotContain(records, r => r.Entry.Kind == "skill-run");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void SkillRun_InvalidArgsJson_ExitOne_NoExecution()
    {
        byte[] before = File.ReadAllBytes(_drawing);
        var (code, stdout, stderr) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", "{ not valid json !!!");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        var records = AuditRecords();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "skill-run");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void SkillRun_ArgsNotObject_ExitOne()
    {
        var (code, _, stderr) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", "[1,2,3]");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.DoesNotContain(AuditRecords(), r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void SkillRun_InvalidPackage_ExitOne_AuditedRefusal()
    {
        string broken = Path.Combine(_project, "broken-skill");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "skill.yaml"), """
            name: 坏包
            version: abc
            """);
        var (code, _, stderr) = Run("skill", "run", broken,
            "--template", "lay-line", "--drawing", _drawing, "--args", LayLineArgs);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.True(AuditChainGreen());
        var refusal = Assert.Single(AuditRecords(), r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("skill", refusal.Entry.Source);
        Assert.Equal(broken, refusal.Entry.Target);
    }

    [Fact]
    public void SkillRun_MissingDrawing_ExitOne_RequestStillAudited()
    {
        string missing = Path.Combine(_project, "no-such.dxf");
        var (code, _, stderr) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", missing, "--args", LayLineArgs);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.True(AuditChainGreen());
        var records = AuditRecords();
        // 请求审计先于执行：读图失败也不丢失 skill-run 请求留痕。
        Assert.Single(records, r => r.Entry.Kind == "skill-run");
        var refusal = Assert.Single(records, r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("skill", refusal.Entry.Source);
        Assert.Equal(missing, refusal.Entry.Target);
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void SkillRun_CorruptOverlay_AuditedRefusal_NoExecution()
    {
        // 闸口 parity（run 先例）：overlay 存在但损坏→审计拒绝，不静默降级为 null 闸口。
        string agentDir = Path.Combine(_project, ".cadagent");
        Directory.CreateDirectory(agentDir);
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        File.WriteAllText(overlayPath, "{ not valid json !!!");
        byte[] before = File.ReadAllBytes(_drawing);
        var (code, stdout, stderr) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", LayLineArgs);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        Assert.True(AuditChainGreen());
        var records = AuditRecords();
        var refusal = Assert.Single(records, r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("semantic", refusal.Entry.Source);
        Assert.Equal(overlayPath, refusal.Entry.Target);
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void SkillRun_WithOverlayPresent_GeometricTemplateStillSucceeds()
    {
        // 闸口 parity（RunGateTests 先例）：overlay 存在→SemanticGate 注入，几何步骤不受影响。
        var (scanCode, _, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        var (code, stdout, _) = Run("skill", "run", _skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", LayLineArgs);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities.Count);
        Assert.True(AuditChainGreen());
    }

    [Fact]
    public void Run_SkillIdCollision_ExitOne_NoTxAudit_DrawingUntouched()
    {
        // 用法错误先例（check --skill 冲突）：校验先于 OpenAudit/Load——无 tx 审计行。
        string collisionDir = Path.Combine(_project, "collision-skill");
        CopyDirectory(SampleSkillDir(), collisionDir);
        string rulePath = Path.Combine(collisionDir, "rules", "layer-charset.yaml");
        File.WriteAllText(rulePath, File.ReadAllText(rulePath).Replace("SK-SAMPLE-001", "R-50001-13.0.1"));
        string planPath = WritePlan();
        byte[] before = File.ReadAllBytes(_drawing);
        var (code, _, stderr) = Run("run", planPath, "--drawing", _drawing, "--skill", collisionDir);
        Assert.Equal(1, code);
        Assert.Contains("R-50001-13.0.1", stderr);
        Assert.Contains("duplicate", stderr);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        string auditDb = Path.Combine(_project, ".cadagent", "audit.db");
        if (File.Exists(auditDb))
        {
            using var audit = new CadAgent.Core.SqliteAuditStore(auditDb);
            Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "tx-start");
        }
    }

    [Fact]
    public void Run_SkillValid_TxAuditCarriesRulePackVersion()
    {
        string planPath = WritePlan();
        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing, "--skill", _skillCopy);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        Assert.Equal(2, CadAgent.Data.DrawingSession.Load(_drawing).Summarize().Entities.Count);
        Assert.True(AuditChainGreen());
        var records = AuditRecords();
        foreach (string kind in new[] { "tx-start", "tx-commit" })
        {
            var rec = Assert.Single(records, r => r.Entry.Kind == kind);
            using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
            Assert.Equal("p1", p.RootElement.GetProperty("planId").GetString());
            string pack = p.RootElement.GetProperty("rulePackVersion").GetString()!;
            Assert.StartsWith("gb50001-2017.r1|", pack);
            Assert.Contains("示例院标@1.0.0", pack);
        }
    }

    [Fact]
    public void Run_WithoutSkill_TxParamsJsonUnchanged()
    {
        // 加性行为守卫：不带 --skill 的 run，tx-start/tx-commit paramsJson 仍为裸 planId。
        string planPath = WritePlan();
        var (code, _, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        var records = AuditRecords();
        Assert.Equal("p1", Assert.Single(records, r => r.Entry.Kind == "tx-start").Entry.ParamsJson);
        Assert.Equal("p1", Assert.Single(records, r => r.Entry.Kind == "tx-commit").Entry.ParamsJson);
    }
}
