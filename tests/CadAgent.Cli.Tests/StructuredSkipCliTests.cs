using System.Text.Json;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 2b-ii-a T5 结构化跳过报告 CLI 接线：check/run-regression 无 overlay →
/// 审计 paramsJson skippedReasons 为结构化数组 [{category, ruleId, message}]，
/// 含 axis-letter/elevation-format 两检查器族（R-50001-8.0.4/11.8.4/11.8.5 三规则）
/// 的 NotEvaluable 条目；stderr 跳过行 "跳过: [{Category}] {RuleId}: {Message}"；
/// 2b-i interim 的 overlayPresent stderr 提示保留（既有 Check_NoOverlay 兼容）。
/// </summary>
public class StructuredSkipCliTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"sskip-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public StructuredSkipCliTests()
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

    private static List<JsonElement> SkipEntries(string paramsJson)
    {
        using var p = JsonDocument.Parse(paramsJson);
        return p.RootElement.GetProperty("skippedReasons").EnumerateArray()
            .Select(e => e.Clone()).ToList();
    }

    [Fact]
    public void Check_NoOverlay_AuditStructuredNotEvaluable_StderrCategory_InterimNoteKept()
    {
        var (code, stdout, stderr) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, code); // 全 recommended
        // 结构化 stderr 跳过行（含旧子串 "跳过" + ruleId）。
        Assert.Contains("跳过: [NotEvaluable] R-50001-8.0.4", stderr);
        Assert.Contains("[NotEvaluable]", stderr);
        // 2b-i interim 提示行保留（既有 Check_NoOverlay 测试兼容）。
        Assert.Contains("未评估", stderr);
        Assert.Contains("semantic scan", stderr);
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "check" && r.Entry.Kind == "check-run");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.False(p.RootElement.GetProperty("overlayPresent").GetBoolean()); // interim 审计字段保留
        var skips = SkipEntries(rec.Entry.ParamsJson);
        // axis-letter + elevation-format 两检查器族共 3 条内置规则 → 3 条 NotEvaluable。
        Assert.Equal(3, skips.Count);
        foreach (string ruleId in new[] { "R-50001-8.0.4", "R-50001-11.8.4", "R-50001-11.8.5" })
        {
            Assert.Contains(skips, s => s.GetProperty("category").GetString() == "NotEvaluable"
                && s.GetProperty("ruleId").GetString() == ruleId
                && s.GetProperty("message").GetString()!.Contains("未评估"));
        }
        // stdout 契约不变：跳过信息不入 stdout。
        Assert.DoesNotContain("NotEvaluable", stdout);
        Assert.DoesNotContain("R-50001-8.0.4", stdout);
    }

    [Fact]
    public void Check_Exempt_StderrStructuredLine_AuditExemptEntry_OldWording()
    {
        Run("rule", "adjudicate", "--rule", "R-50001-13.0.1", "--exempt", "--reason", "测试豁免");
        var (code, stdout, stderr) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("跳过: [Exempt] R-50001-13.0.1", stderr);
        Assert.Contains("已豁免", stderr); // 旧文案子串兼容
        Assert.DoesNotContain("R-50001-13.0.1", stdout); // 豁免展示移除契约 intact
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "check" && r.Entry.Kind == "check-run");
        var skips = SkipEntries(rec.Entry.ParamsJson);
        Assert.Contains(skips, s => s.GetProperty("category").GetString() == "Exempt"
            && s.GetProperty("ruleId").GetString() == "R-50001-13.0.1"
            && s.GetProperty("message").GetString()!.Contains("已豁免"));
    }

    [Fact]
    public void Run_NoOverlay_RegressionAuditStructuredNotEvaluable_StderrCategory()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (code, stdout, stderr) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code); // 观察性检查不改 run 结果
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        Assert.Contains("跳过: [NotEvaluable] R-50001-8.0.4", stderr);
        Assert.Contains("未评估", stderr); // interim 提示保留
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Source == "run-regression" && r.Entry.Kind == "check-run");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.False(p.RootElement.GetProperty("overlayPresent").GetBoolean());
        var skips = SkipEntries(rec.Entry.ParamsJson);
        Assert.Contains(skips, s => s.GetProperty("category").GetString() == "NotEvaluable"
            && s.GetProperty("ruleId").GetString() == "R-50001-8.0.4");
        Assert.Contains(skips, s => s.GetProperty("category").GetString() == "NotEvaluable"
            && s.GetProperty("ruleId").GetString() == "R-50001-11.8.5");
        Assert.True(audit.VerifyChain(out _));
    }
}
