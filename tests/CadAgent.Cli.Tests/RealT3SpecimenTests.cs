using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 真实 T3 标本 🟢 闭环重取（用户天正电气 5.0 转换件，skip-gated）——诚实钉住实测口径。
///
/// 背景：长期搁置项"真实 🟢 待用户 T3 转换"。此前 🟢 闭环跑在 ACadSharp UnknownEntity
/// 盲子集上（census 切片作废该证据，spec §12 注记）。用户提供转换件
/// outputs/tarch-t3/1#配套用房0928_t3.dwg（源 D: 零触碰副本），预期 T3=plain 实体 census 0 → Green。
///
/// 实测结论（2026-11-23，诚实记录非预期）：该转换件**仅重存未炸开**——对象图 3636 实体句柄
/// 与 native2 原图逐一一致，census 残留 3257/30 类（TCH_WALL 968），与 native2 重普查
/// （outputs/tarch-gate-recombat/intake-s2.json）逐类一致 → intake **Yellow**（exit 2），非 Green。
/// 故本测试按实测口径钉住 Yellow + census 残留（不赌 Green）；🟢 闭环重取仍待真正炸开的 T3 件。
///
/// 实现先行口径（诚实记录）：census 感知准入闸口已于防假 Green 切片（UG-T1..T5）交付，
/// 本测试为真实标本实证钉住（非驱动新实现），首跑即绿——镜像 SheetTilerTests/UnparsedGateCliTests
/// 真实标本测试"实现先行、实证钉住非驱动"先例。
///
/// 标本只读硬约束：.dwg 一律复制到临时项目再 intake（working/sidecar 全落临时目录，
/// 源目录零触碰）；副本字节前后一致；缺标本显式 Skipped 不假装。
/// </summary>
public class RealT3SpecimenTests : IDisposable
{
    private const string SpecimenRelative = "outputs/tarch-t3/1#配套用房0928_t3.dwg";

    private readonly string _project = Path.Combine(Path.GetTempPath(), $"realt3-{Guid.NewGuid():N}");

    public RealT3SpecimenTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    /// <summary>复制真实 T3 标本到临时项目（只读源零副作用）；缺标本→Skip。</summary>
    private string CopySpecimen()
    {
        string src = Path.Combine(RepoRoot(), SpecimenRelative);
        Skip.If(!File.Exists(src), $"真实 T3 标本不在本机，跳过: {SpecimenRelative}");
        string dst = Path.Combine(_project, Path.GetFileName(src));
        File.Copy(src, dst);
        return dst;
    }

    private List<AuditRecord> AuditRecords()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    [SkippableFact]
    public void RealT3Specimen_IntakeYellow_CensusResidualMatchesNative()
    {
        // 实测钉住：转换件未炸开 TCH 对象 → census 残留 → Yellow（非预期 Green）。
        string specimen = CopySpecimen();
        byte[] sourceBefore = File.ReadAllBytes(specimen);

        var (code, stdout, _) = Run("intake", specimen);

        // T2 三分：有原生实体（Entities>0）+ census 非空 → Yellow（exit 2），非 Red 非假 Green。
        Assert.Equal(2, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Yellow", doc.RootElement.GetProperty("level").GetString());
        string guidance = doc.RootElement.GetProperty("guidance").GetString()!;
        Assert.Contains("只读审查", guidance);
        Assert.Contains("图形导出", guidance); // T3 指引文案
        Assert.Contains("T3", guidance);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3636, summary.GetProperty("Entities").GetArrayLength()); // 与 native2 对象图一致
        Assert.Equal(0, summary.GetProperty("Unknowns").GetArrayLength()); // 对象图盲区实证
        // census 残留钉住（类×计数口径，与 native2 重普查逐类一致）。
        var classes = doc.RootElement.GetProperty("unparsed");
        Assert.Equal(30, classes.GetArrayLength());
        int total = 0, wall = 0;
        foreach (var c in classes.EnumerateArray())
        {
            int n = c.GetProperty("Count").GetInt32();
            total += n;
            if (c.GetProperty("CppClassName").GetString() == "TCH_WALL") wall = n;
        }
        Assert.Equal(3257, total);
        Assert.Equal(968, wall);
        // sidecar 回写（LoadWithCensus）：二次加载 census 仍可恢复。
        Assert.Equal(3257, UnparsedCensusStore.Load(_project, specimen).TotalCount);
        // 标本只读：副本字节前后一致（intake 不改源 .dwg）。
        Assert.Equal(sourceBefore, File.ReadAllBytes(specimen));
    }

    [SkippableFact]
    public void RealT3Specimen_RunRefused_YellowGateBeforeSemantics_ReadOnlyAudit()
    {
        // tx 闭环判定实证：census 残留 → Yellow → gate-at-run 在语义闸之前全图级拦截修改。
        // 旧用户批准 7 层映射层名虽在 T3 保留（映射可复用），但 Yellow 闸先于语义拒绝任何 tx，
        // 故 tx 闭环被 census 阻断（非映射缺失）。add-line 拒绝 exit 1 + run-rejected Yellow
        // + working/副本字节不变 + 验链绿。
        string specimen = CopySpecimen();
        var (intakeCode, _, _) = Run("intake", specimen);
        Assert.Equal(2, intakeCode); // Yellow 前提
        string working = DrawingSession.WorkingPathFor(specimen);
        Assert.True(File.Exists(working)); // 首载物化 working（临时目录内）
        byte[] workingBefore = File.ReadAllBytes(working);
        byte[] sourceBefore = File.ReadAllBytes(specimen);
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"t3-tx-probe","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);

        var (code, stdout, stderr) = Run("run", planPath, "--drawing", specimen);

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.Contains("图形导出", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Equal(workingBefore, File.ReadAllBytes(working)); // working 未被触碰
        Assert.Equal(sourceBefore, File.ReadAllBytes(specimen)); // 源副本未被触碰
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("run", rejected.Entry.Source);
        Assert.Equal(rejected.Entry.BeforeHash, rejected.Entry.AfterHash);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(3257, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
