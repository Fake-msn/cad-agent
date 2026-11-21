using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 2b-ii-a T6：孤儿裁定拒绝 + 审计复现证据字段。
/// 契约：adjudicate 的 ruleId 必须在加载集（builtin + --skill 包，--skill 可重复）；
/// 未知 Id→stderr + exit 1，不留 rule-adjudicated 审计、不落盘（校验先于任何副作用；
/// 顺序：互斥→reason 必填→孤儿）。check/run-regression 审计 paramsJson 加性：
/// adjudicationHash（Fingerprint，64 位大写 hex，空集=SHA256("[]")）+
/// rulePackVersion（无 skill 时 builtin 常量 "gb50001-2017.r1"）；stdout 契约不变。
/// </summary>
public class OrphanAdjudicationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"orphan-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public OrphanAdjudicationTests()
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

    private string AdjPath => Path.Combine(_project, ".cadagent", "adjudications.json");
    private string AuditDbPath => Path.Combine(_project, ".cadagent", "audit.db");

    /// <summary>断言无 rule-adjudicated 审计记录（db 尚未创建亦满足）。</summary>
    private void AssertNoAdjudicationAudit()
    {
        if (!File.Exists(AuditDbPath)) return;
        using var audit = new CadAgent.Core.SqliteAuditStore(AuditDbPath);
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "rule-adjudicated");
    }

    /// <summary>落盘一个最小 skill 包（1 条 recommended 图层规则），返回显式目录路径。</summary>
    private string CreateSkill(string name, string ruleId)
    {
        string dir = Path.Combine(_project, "skills", name);
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        File.WriteAllText(Path.Combine(dir, "skill.yaml"),
            $"name: {name}\nversion: \"1.0.0\"\ndiscipline: architecture\ndrawingType: any\n");
        File.WriteAllText(Path.Combine(dir, "rules", "r1.yaml"), $$"""
            id: {{ruleId}}
            source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
            modal: "应"
            force: recommended
            severity: high
            scope: { discipline: architecture, drawingType: any }
            check: { type: layer-name, params: { mode: charset } }
            suggestion: "测试"
            """);
        return dir;
    }

    private static string EmptySetHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("[]")));

    // ── 孤儿裁定拒绝 ────────────────────────────────────────────────────────

    [Fact]
    public void Adjudicate_UnknownRuleId_ExitOne_NoAudit_NoPersistence()
    {
        var (code, _, stderr) = Run("rule", "adjudicate", "--rule", "R-NOPE",
            "--force", "mandatory", "--reason", "孤儿裁定");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Contains("R-NOPE", stderr);
        Assert.False(File.Exists(AdjPath)); // 不落盘
        AssertNoAdjudicationAudit();        // 不留审计
    }

    [Fact]
    public void Adjudicate_KnownBuiltinId_ExitZero_Persisted()
    {
        var (code, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-5.0.2",
            "--force", "mandatory", "--reason", "院标升级");
        Assert.Equal(0, code);
        Assert.True(File.Exists(AdjPath));
        Assert.Contains("R-50001-5.0.2", File.ReadAllText(AdjPath));
    }

    [Fact]
    public void Adjudicate_SkillRuleId_WithoutSkillOption_Rejected()
    {
        CreateSkill("示例院标", "SK-ORPH-001");
        var (code, _, stderr) = Run("rule", "adjudicate", "--rule", "SK-ORPH-001",
            "--exempt", "--reason", "未加载该 skill");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.False(File.Exists(AdjPath));
        AssertNoAdjudicationAudit();
    }

    [Fact]
    public void Adjudicate_SkillRuleId_WithSkillOption_ExitZero()
    {
        string skillDir = CreateSkill("示例院标", "SK-ORPH-001");
        var (code, _, _) = Run("rule", "adjudicate", "--rule", "SK-ORPH-001",
            "--skill", skillDir, "--exempt", "--reason", "本院标豁免");
        Assert.Equal(0, code);
        Assert.Contains("SK-ORPH-001", File.ReadAllText(AdjPath));
    }

    [Fact]
    public void Adjudicate_RepeatableSkillOptions_SecondPackageRuleAccepted()
    {
        string dirA = CreateSkill("院标A", "SK-A-001");
        string dirB = CreateSkill("院标B", "SK-B-001");
        var (code, _, _) = Run("rule", "adjudicate", "--rule", "SK-B-001",
            "--skill", dirA, "--skill", dirB, "--force", "mandatory", "--reason", "可重复 --skill");
        Assert.Equal(0, code);
        Assert.Contains("SK-B-001", File.ReadAllText(AdjPath));
    }

    [Fact]
    public void Adjudicate_UnknownRuleId_WithSkill_StillRejected()
    {
        string skillDir = CreateSkill("示例院标", "SK-ORPH-001");
        var (code, _, stderr) = Run("rule", "adjudicate", "--rule", "R-NOPE",
            "--skill", skillDir, "--exempt", "--reason", "x");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.False(File.Exists(AdjPath));
        AssertNoAdjudicationAudit();
    }

    [Fact]
    public void Adjudicate_InvalidSkillDir_ExitOne_NoPersistence()
    {
        string bogus = Path.Combine(_project, "no-such-skill");
        var (code, _, stderr) = Run("rule", "adjudicate", "--rule", "R-50001-5.0.2",
            "--skill", bogus, "--force", "mandatory", "--reason", "x");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.False(File.Exists(AdjPath));
        AssertNoAdjudicationAudit();
    }

    // ── 审计复现证据字段（加性，stdout 契约不变） ─────────────────────────────

    [Fact]
    public void Check_Audit_ContainsAdjudicationHashAndRulePackVersion()
    {
        var (code, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, code);
        using var audit = new CadAgent.Core.SqliteAuditStore(AuditDbPath);
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "check" && r.Entry.Kind == "check-run");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        string hash = p.RootElement.GetProperty("adjudicationHash").GetString()!;
        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.Equal(EmptySetHash, hash); // 无裁定→空集固定值
        Assert.Contains("gb50001-2017.r1",
            p.RootElement.GetProperty("rulePackVersion").GetString());
        // stdout 契约不变：summary 不含新字段。
        using var outDoc = JsonDocument.Parse(stdout);
        Assert.False(outDoc.RootElement.GetProperty("summary").TryGetProperty("adjudicationHash", out _));
        Assert.False(outDoc.RootElement.GetProperty("summary").TryGetProperty("rulePackVersion", out _));
    }

    [Fact]
    public void Check_AdjudicationHash_StableAcrossRuns_ChangesAfterAdjudication()
    {
        var (c1, _, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, c1);
        var (c2, _, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, c2);
        using (var audit = new CadAgent.Core.SqliteAuditStore(AuditDbPath))
        {
            var hashes = audit.ReadAll()
                .Where(r => r.Entry.Source == "check" && r.Entry.Kind == "check-run")
                .Select(r => JsonDocument.Parse(r.Entry.ParamsJson).RootElement
                    .GetProperty("adjudicationHash").GetString())
                .ToList();
            Assert.Equal(2, hashes.Count);
            Assert.Equal(hashes[0], hashes[1]); // 同裁定集重跑→指纹一致（复现证据）
        }
        var (ac, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-13.0.1",
            "--exempt", "--reason", "本项目图层约定另定");
        Assert.Equal(0, ac);
        var (c3, _, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, c3);
        using (var audit = new CadAgent.Core.SqliteAuditStore(AuditDbPath))
        {
            var last = audit.ReadAll()
                .Where(r => r.Entry.Source == "check" && r.Entry.Kind == "check-run")
                .Select(r => JsonDocument.Parse(r.Entry.ParamsJson).RootElement
                    .GetProperty("adjudicationHash").GetString())
                .Last();
            Assert.NotEqual(EmptySetHash, last); // 裁定变更→指纹变化
        }
    }

    [Fact]
    public void RunRegression_Audit_ContainsAdjudicationHashAndRulePackVersion()
    {
        string plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(plan, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (code, stdout, _) = Run("run", plan, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        using var audit = new CadAgent.Core.SqliteAuditStore(AuditDbPath);
        var rec = Assert.Single(audit.ReadAll(),
            r => r.Entry.Source == "run-regression" && r.Entry.Kind == "check-run");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        string hash = p.RootElement.GetProperty("adjudicationHash").GetString()!;
        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.Equal(EmptySetHash, hash);
        Assert.Contains("gb50001-2017.r1",
            p.RootElement.GetProperty("rulePackVersion").GetString());
    }
}
