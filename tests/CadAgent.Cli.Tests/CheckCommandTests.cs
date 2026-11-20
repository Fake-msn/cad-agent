using System.Text.Json;
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

    // 评审修复 1（控制者裁定）：跳过原因走 stderr（"跳过: " 前缀），stdout JSON 契约不变；
    // T3 引擎豁免跳过原因格式为 "规则 {ruleId} 已豁免"，含 ruleId，无需改引擎。
    [Fact]
    public void Check_SkippedReasons_OnStderr_NotStdout()
    {
        Run("rule", "adjudicate", "--rule", "R-50001-13.0.1", "--exempt", "--reason", "测试豁免");
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "check", "--drawing", _drawing, "--project", _project }, outW, errW);
        Assert.Equal(0, code);
        Assert.Contains("跳过", errW.ToString());
        Assert.Contains("R-50001-13.0.1", errW.ToString());
        Assert.DoesNotContain("R-50001-13.0.1", outW.ToString()); // stdout 契约 intact
    }

    // 最终审查修复 2（控制者裁定， interim 观察性；结构化跳过报告=2b-ii）：无语义标注时
    // overlay 驱动规则（轴号/标高）实际未评估——不得与"已查且干净"不可区分：stderr 提示
    // + 审计 check-run paramsJson 加性字段 overlayPresent=false；引擎计数与 stdout 契约不变。
    [Fact]
    public void Check_NoOverlay_StderrNote_AuditOverlayPresentFalse()
    {
        var (code, stdout, stderr) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("未评估", stderr);
        Assert.Contains("semantic scan", stderr);
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "check" && r.Entry.Kind == "check-run");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.False(p.RootElement.GetProperty("overlayPresent").GetBoolean());
        // stdout summary 契约不变：不含 overlayPresent 字段。
        using var outDoc = JsonDocument.Parse(stdout);
        Assert.False(outDoc.RootElement.GetProperty("summary").TryGetProperty("overlayPresent", out _));
    }

    // 最终审查修复 2（控制者裁定）：有语义标注（scan 命中约定图层"墙体"→LayerInference 标注）
    // → 无"未评估"提示，审计 overlayPresent=true。
    [Fact]
    public void Check_WithScanAnnotations_AuditOverlayPresentTrue_NoNote()
    {
        string drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("墙体"));
            var wall = doc.Layers.First(l => l.Name == "墙体");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(1000, 0), Layer = wall });
        });
        try
        {
            var (sc, _, _) = Run("semantic", "scan", "--drawing", drawing);
            Assert.Equal(0, sc);
            var (code, _, stderr) = Run("check", "--drawing", drawing);
            Assert.Equal(0, code); // 无裁定→全 recommended→exit 0
            Assert.DoesNotContain("未评估", stderr);
            using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "check" && r.Entry.Kind == "check-run");
            using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
            Assert.True(p.RootElement.GetProperty("overlayPresent").GetBoolean());
        }
        finally
        {
            File.Delete(drawing);
        }
    }

    // 评审修复 2（控制者裁定）：损坏 adjudications.json → LoadOrReject 审计拒绝
    // （intake-rejected，target=裁定文件路径）+ stderr + exit 1，审计链保持完整。
    [Fact]
    public void Check_CorruptAdjudications_AuditedRefusal()
    {
        string adjPath = Path.Combine(_project, ".cadagent", "adjudications.json");
        Directory.CreateDirectory(Path.GetDirectoryName(adjPath)!);
        File.WriteAllText(adjPath, "{corrupt json!!");
        var (code, _, stderr) = Run("check", "--drawing", _drawing);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc); // 链完整且含 intake-rejected 记录
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "intake-rejected" && r.Entry.Target == adjPath);
    }
}
