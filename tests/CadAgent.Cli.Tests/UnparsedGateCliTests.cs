using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 防假 Green 切片 T3：修改操作拒绝（四消费点 intake/run/skill run/agent）。
/// census 经 sidecar 注入（普通 DXF + .cadagent/unparsed.json，源哈希匹配——无需真实天正标本）：
/// 有原生实体 + 未解析对象 → Yellow（T2 三分），修改路径一律 exit 1 + run-rejected 留痕
/// （params 含 level/unparsedCount/guidance，spec ⑥/§9 拒绝必留痕）。
/// agent 闸先于 LLM 调用（fake factory 若被调测试失败）。无 census Green 路径逐字保持。
/// </summary>
public class UnparsedGateCliTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"unparsedgate-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public UnparsedGateCliTests()
    {
        Directory.CreateDirectory(_project);
        // Green 基线：普通 DXF（一条原生 Line，图内 Unknowns=0）。
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        CommandRouter.RuntimeFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    /// <summary>sidecar 注入 census（TCH_WALL×2，源哈希匹配）→ Yellow（有原生+未解析）。</summary>
    private void InjectCensus() =>
        UnparsedCensusStore.Save(_project, _drawing,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private string WritePlan()
    {
        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        return planPath;
    }

    private List<AuditRecord> AuditRecords()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    [Fact]
    public void Run_YellowUnparsed_RefusedExit1_AuditRunRejected()
    {
        InjectCensus();
        string planPath = WritePlan();
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, stdout, stderr) = Run("run", planPath, "--drawing", _drawing);

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr); // Yellow guidance（T2 YellowReadOnlyGuidance）
        Assert.DoesNotContain("\"ok\"", stdout); // 未执行任何步骤
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 图纸未被触碰
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("run", rejected.Entry.Source);
        Assert.Equal(_drawing, rejected.Entry.Target);
        Assert.Equal(rejected.Entry.BeforeHash, rejected.Entry.AfterHash); // 拒绝不改图
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(2, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Agent_YellowUnparsed_RefusedBeforeLlm()
    {
        InjectCensus();
        Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1", "--model", "m", "--key", "sk-agentkey12345678");
        Run("llm", "use", "--name", "p");
        bool llmCalled = false;
        CommandRouter.LlmClientFactory = (p, k) =>
        {
            llmCalled = true;
            throw new InvalidOperationException("LLM 不应被调用（准入闸须先于 LLM）");
        };
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, _, stderr) = Run("agent", "--drawing", _drawing, "--intent", "画一条线", "--yes");

        Assert.Equal(1, code);
        Assert.False(llmCalled); // 闸口先于 LLM factory（缺口闭合）
        Assert.Contains("只读审查", stderr);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("agent", rejected.Entry.Source);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(2, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "llm-call");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void Intake_YellowUnparsed_StdoutCensus()
    {
        InjectCensus();
        var (code, stdout, _) = Run("intake", _drawing);
        Assert.Equal(2, code); // Yellow（只读审查允许，修改拒绝）
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Yellow", doc.RootElement.GetProperty("level").GetString());
        Assert.Contains("只读审查", doc.RootElement.GetProperty("guidance").GetString());
        // census 数组（类×计数口径，区别于 summary.Unparsed 的逐对象展开）
        var unparsed = doc.RootElement.GetProperty("unparsed");
        Assert.Equal(JsonValueKind.Array, unparsed.ValueKind);
        var wall = Assert.Single(unparsed.EnumerateArray());
        Assert.Equal("TCH_WALL", wall.GetProperty("CppClassName").GetString());
        Assert.Equal(2, wall.GetProperty("Count").GetInt32());
    }

    [Fact]
    public void SkillRun_YellowUnparsed_RefusedExit1_AuditRunRejected()
    {
        InjectCensus();
        string skillCopy = Path.Combine(_project, "sample-institute");
        CopyDirectory(Path.Combine(RepoRoot(), "examples", "skills", "sample-institute"), skillCopy);
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, stdout, stderr) = Run("skill", "run", skillCopy,
            "--template", "lay-line", "--drawing", _drawing, "--args", """{"x1":0,"y1":0,"x2":50,"y2":50}""");

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        var records = AuditRecords();
        // skill-run 请求审计先行（既有契约），随后 run-rejected 拒绝留痕。
        var request = Assert.Single(records, r => r.Entry.Kind == "skill-run");
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("skill", rejected.Entry.Source);
        Assert.True(request.Seq < rejected.Seq);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(2, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void Run_NoCensus_GreenLegacy_Commits()
    {
        // 无 sidecar → Green legacy：add-line 照常 tx-commit（行为逐字保持）。
        string planPath = WritePlan();
        var (code, stdout, stderr) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        // Green 无闸口警示（run-regression 的"提示: 无语义标注"属既有观察性通道，不在此断言面）。
        Assert.DoesNotContain("只读审查", stderr);
        Assert.DoesNotContain("Yellow", stderr);
        var records = AuditRecords();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "run-rejected");
        Assert.Contains(records, r => r.Entry.Kind == "tx-commit");
        // intake stdout 逐字保持：无 census 不得多出 unparsed 字段（T1 Fix Round 1 契约）。
        var (intakeCode, intakeOut, _) = Run("intake", _drawing);
        Assert.Equal(0, intakeCode);
        Assert.DoesNotContain("unparsed", intakeOut);
        Assert.DoesNotContain("Unparsed", intakeOut);
    }

    // ===== T4：真实原生天正标本实战（skip-gated）=====
    // 标本只读硬约束：.dwg 一律复制到临时项目再 intake（working 副本/sidecar 全落临时目录，
    // 源目录零副作用——不删除/不改写 outputs 既有 working 证据）。缺标本显式 Skipped 不假装。
    // census 期望值钉住通知普查实证（outputs/tarch-gate-recombat/census-summary.txt，
    // 与 outputs/api-probe 探针交叉一致）：TCH_WALL 1592/968/1594、TCH_PIPE 994。

    /// <summary>复制真实标本到临时项目（只读源零副作用）；缺标本→Skip。</summary>
    private string CopySpecimen(string relativePath)
    {
        string src = Path.Combine(RepoRoot(), relativePath);
        Skip.If(!File.Exists(src), $"真实原生天正标本不在本机，跳过: {relativePath}");
        string dst = Path.Combine(_project, Path.GetFileName(src));
        File.Copy(src, dst);
        return dst;
    }

    [SkippableTheory]
    [InlineData("outputs/tarch-native/电施（强电）-2#厂房电力+普通照明平面图.dwg", 1592, 30, 5985, 32)]
    [InlineData("outputs/tarch-native2/1#配套用房0928.dwg", 968, 4, 3257, 30)]
    [InlineData("outputs/tarch-native3/2号厂房1021_t9.dwg", 1594, 30, 5707, 30)]
    [InlineData("outputs/tarch-native4/S-1#配套用房_t8.dwg", 401, 994, 3806, 28)]
    public void RealNativeTArch_IntakeYellow_CensusMatchesProbe(
        string relativePath, int expectedWall, int expectedPipe, int expectedTotal, int expectedClasses)
    {
        string specimen = CopySpecimen(relativePath);

        var (code, stdout, _) = Run("intake", specimen);

        // T2 三分：有原生实体（Entities>0）+ census 非空 → Yellow（exit 2），非 Red 非假 Green。
        Assert.Equal(2, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Yellow", doc.RootElement.GetProperty("level").GetString());
        string guidance = doc.RootElement.GetProperty("guidance").GetString()!;
        Assert.Contains("只读审查", guidance);
        Assert.Contains("图形导出", guidance); // T3 指引文案核对
        Assert.Contains("T3", guidance);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.True(summary.GetProperty("Entities").GetArrayLength() > 0); // 原生实体存在
        Assert.Equal(0, summary.GetProperty("Unknowns").GetArrayLength()); // 对象图盲区实证
        // census 头部类计数钉住（类×计数口径，通知普查实证）。
        var classes = doc.RootElement.GetProperty("unparsed");
        Assert.Equal(expectedClasses, classes.GetArrayLength());
        int total = 0, wall = 0, pipe = 0;
        foreach (var c in classes.EnumerateArray())
        {
            int n = c.GetProperty("Count").GetInt32();
            total += n;
            switch (c.GetProperty("CppClassName").GetString())
            {
                case "TCH_WALL": wall = n; break;
                case "TCH_PIPE": pipe = n; break;
            }
        }
        Assert.Equal(expectedTotal, total);
        Assert.Equal(expectedWall, wall);
        Assert.Equal(expectedPipe, pipe);
        // sidecar 回写（T3 LoadWithCensus）：二次加载（working 分支）census 仍可恢复。
        Assert.Equal(expectedTotal, UnparsedCensusStore.Load(_project, specimen).TotalCount);
    }

    [SkippableFact]
    public void RealNativeTArch_RunRefused_ReadOnlyAudit()
    {
        // 电施标本（TCH_WALL 1592/总 5985）：add-line 拒绝 exit 1 + run-rejected level=Yellow
        // + working 哈希（字节）不变 + 源 .dwg 副本不变 + 验链绿。
        string specimen = CopySpecimen("outputs/tarch-native/电施（强电）-2#厂房电力+普通照明平面图.dwg");
        var (intakeCode, _, _) = Run("intake", specimen);
        Assert.Equal(2, intakeCode); // Yellow 前提
        string working = DrawingSession.WorkingPathFor(specimen);
        Assert.True(File.Exists(working)); // 首载物化 working（临时目录内）
        byte[] workingBefore = File.ReadAllBytes(working);
        byte[] sourceBefore = File.ReadAllBytes(specimen);
        string planPath = WritePlan();

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
            Assert.Equal(5985, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [SkippableFact]
    public void RealNativeTArch_ExportRefused_NoDerivedOutput()
    {
        // 电施标本 export（T3 R8）：闸先于厂商运行时（factory 若被调测试失败），
        // exit 1 + export-rejected level=Yellow + 零衍生产物（DWG/DXF 均不得产出）。
        string specimen = CopySpecimen("outputs/tarch-native/电施（强电）-2#厂房电力+普通照明平面图.dwg");
        var (intakeCode, _, _) = Run("intake", specimen);
        Assert.Equal(2, intakeCode);
        CommandRouter.RuntimeFactory = () =>
            throw new InvalidOperationException("export 准入闸须先于厂商运行时（R8）");
        string outPath = Path.Combine(_project, "derived.dwg");

        var (code, _, stderr) = Run("export", "--drawing", specimen, "--out", outPath);

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.False(File.Exists(outPath)); // 无衍生产物
        Assert.False(File.Exists(Path.ChangeExtension(outPath, ".dxf")));
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "export-rejected");
        Assert.Equal("export", rejected.Entry.Source);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(5985, p.RootElement.GetProperty("unparsedCount").GetInt32());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "export-committed");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "export-degraded");
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

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }
}
