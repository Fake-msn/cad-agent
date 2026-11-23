using System.Text.Json;
using ACadSharp;
using ACadSharp.IO;
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
        CommandRouter.IdentityOverride = null; // review 驳回测试注入复位（防御性，与 ReviewCommandTests 同口径）
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

    // ===== R9：陈旧 working 假 Green 旁路闭合（pre-review Fix Round 1）=====
    // 复现：切片前物化的 working（无 TCH 对象）+ 新项目 intake（无 sidecar）→
    // 修复前 persisted=null→census Empty→假 Green（intake exit 0 + run tx-commit）。
    // 修复：persisted Empty/缺 + 源存在→从源 DWG 通知重建 census；源缺失→拒绝留痕。

    /// <summary>在指定路径写一份无 TCH 对象的 working DXF（模拟切片前物化的陈旧副本）。</summary>
    private static void WriteStaleWorking(string workingPath)
    {
        var doc = new CadDocument();
        DxfFixture.AddSampleLine(doc);
        using var fs = File.Create(workingPath);
        DxfWriter.Write(fs, doc);
    }

    [SkippableFact]
    public void RealNativeTArch_StaleWorkingNoSidecar_IntakeRebuildsCensus_RunRefused()
    {
        // 电施标本：旧 working（无 TCH）+ 无 sidecar → census 必须从源重建（5985/TCH_WALL 1592），
        // intake Yellow exit 2（修复前 exit 0 假 Green）；run 拒绝（修复前 tx-commit）；
        // sidecar 回写（LoadWithCensus 既有逻辑）；working/源字节不变（重建只取 census，doc 丢弃）。
        string specimen = CopySpecimen("outputs/tarch-native/电施（强电）-2#厂房电力+普通照明平面图.dwg");
        string working = DrawingSession.WorkingPathFor(specimen);
        WriteStaleWorking(working);
        byte[] workingBefore = File.ReadAllBytes(working);
        byte[] sourceBefore = File.ReadAllBytes(specimen);

        var (code, stdout, _) = Run("intake", specimen);

        Assert.Equal(2, code); // Yellow（修复前：exit 0 假 Green——R9 复现）
        using (var doc = JsonDocument.Parse(stdout))
        {
            Assert.Equal("Yellow", doc.RootElement.GetProperty("level").GetString());
            int total = 0, wall = 0;
            foreach (var c in doc.RootElement.GetProperty("unparsed").EnumerateArray())
            {
                int n = c.GetProperty("Count").GetInt32();
                total += n;
                if (c.GetProperty("CppClassName").GetString() == "TCH_WALL") wall = n;
            }
            Assert.Equal(5985, total); // census == 源首载普查（通知重建，非 Empty）
            Assert.Equal(1592, wall);
        }
        // sidecar 回写：二次加载走 persisted（不再重读源）。
        Assert.Equal(5985, UnparsedCensusStore.Load(_project, specimen).TotalCount);
        Assert.Equal(workingBefore, File.ReadAllBytes(working)); // working 未被重写
        Assert.Equal(sourceBefore, File.ReadAllBytes(specimen)); // 源只读

        // 修改拒绝（修复前此路径 tx-commit）：run-rejected Yellow + 无 tx。
        string planPath = WritePlan();
        var (runCode, runOut, runErr) = Run("run", planPath, "--drawing", specimen);
        Assert.Equal(1, runCode);
        Assert.Contains("只读审查", runErr);
        Assert.DoesNotContain("\"ok\"", runOut);
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(5985, p.RootElement.GetProperty("unparsedCount").GetInt32());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.Equal(workingBefore, File.ReadAllBytes(working));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Intake_WorkingOnlySourceMissing_RejectedExit1_AuditTrail()
    {
        // working-only 无源且无 census → Load 抛 InvalidOperationException →
        // intake 既有 catch 留痕 intake-rejected + exit 1（不得假 Green exit 0）。
        string dwg = Path.Combine(_project, "ghost.dwg"); // 源不存在
        WriteStaleWorking(DrawingSession.WorkingPathFor(dwg));

        var (code, stdout, stderr) = Run("intake", dwg);

        Assert.Equal(1, code);
        Assert.Contains("无法核验完整性", stderr);
        Assert.Empty(stdout);
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("intake", rejected.Entry.Source);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
            Assert.Contains("无法核验完整性", p.RootElement.GetProperty("Error").GetString());
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_WorkingOnlySourceMissing_RejectedExit1_NoTx()
    {
        // run 消费点：Load 抛 → 既有 catch 留痕 run-rejected（level=LoadFailed）+ exit 1，
        // 不执行任何步骤（修复前：假 Green → tx-commit）。
        string dwg = Path.Combine(_project, "ghost.dwg");
        string working = DrawingSession.WorkingPathFor(dwg);
        WriteStaleWorking(working);
        byte[] workingBefore = File.ReadAllBytes(working);
        string planPath = WritePlan();

        var (code, stdout, stderr) = Run("run", planPath, "--drawing", dwg);

        Assert.Equal(1, code);
        Assert.Contains("无法核验完整性", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Equal(workingBefore, File.ReadAllBytes(working));
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("run", rejected.Entry.Source);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("LoadFailed", p.RootElement.GetProperty("level").GetString());
            Assert.Contains("无法核验完整性", p.RootElement.GetProperty("error").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Rollback_WorkingOnlySourceMissing_RejectedExit1_AuditTrail()
    {
        // rollback 消费点（R9 补留痕）：此前 Load 在 try/catch 外——异常经 Run 兜底
        // exit 1 但无审计（spec §9 缺口）。修复后 LoadOrReject 留痕 intake-rejected。
        string dwg = Path.Combine(_project, "ghost.dwg");
        WriteStaleWorking(DrawingSession.WorkingPathFor(dwg));

        var (code, _, stderr) = Run("rollback", "tx-none", "--drawing", dwg);

        Assert.Equal(1, code);
        Assert.Contains("无法核验完整性", stderr);
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("rollback", rejected.Entry.Source);
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
            Assert.Contains("无法核验完整性", p.RootElement.GetProperty("Error").GetString());
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    // ===== R10：sidecar 有效 + 源删除的 CLI 钉住 + rollback LoadWithCensus =====

    /// <summary>在项目内落一份合成 .dwg 源（首载 intake 物化 working；避免临时目录残留）。</summary>
    private string CreateProjectDwg(string name)
    {
        string tmp = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
        string dwg = Path.Combine(_project, name);
        File.Move(tmp, dwg);
        return dwg;
    }

    [Fact]
    public void IntakeRun_SidecarCensusPresent_SourceDeleted_RejectedExit1_AuditTrail()
    {
        // R10 钉住：sidecar 在（census 非空、源哈希曾匹配）+ 源 .dwg 删除 →
        // Store.Load 源缺失诚实 Empty → Load 抛 InvalidOperationException（R9 第三分支）→
        // intake/run 既有 catch 映射拒绝 exit 1 + 留痕（不得拿 sidecar 旧 census 假放行，
        // 也不得静默 Green）。working/sidecar 字节不变（拒绝零副作用）。
        string dwg = CreateProjectDwg("specimen.dwg");
        var (baselineCode, _, _) = Run("intake", dwg);
        Assert.Equal(0, baselineCode); // 合成源无 TCH → Green 基线，working 已物化
        string working = DrawingSession.WorkingPathFor(dwg);
        Assert.True(File.Exists(working));
        UnparsedCensusStore.Save(_project, dwg,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));
        Assert.Equal(2, UnparsedCensusStore.Load(_project, dwg).TotalCount); // sidecar 有效前提
        File.Delete(dwg); // 源删除
        byte[] workingBefore = File.ReadAllBytes(working);
        string sidecarPath = Path.Combine(_project, ".cadagent", "unparsed.json");
        byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
        string planPath = WritePlan();

        var (intakeCode, intakeOut, intakeErr) = Run("intake", dwg);

        Assert.Equal(1, intakeCode);
        Assert.Contains("无法核验完整性", intakeErr);
        Assert.Empty(intakeOut);

        var (runCode, runOut, runErr) = Run("run", planPath, "--drawing", dwg);

        Assert.Equal(1, runCode);
        Assert.Contains("无法核验完整性", runErr);
        Assert.DoesNotContain("\"ok\"", runOut);
        Assert.Equal(workingBefore, File.ReadAllBytes(working)); // working 未被触碰
        Assert.Equal(sidecarBefore, File.ReadAllBytes(sidecarPath)); // census 证据保留
        var records = AuditRecords();
        var intakeRejected = Assert.Single(records, r => r.Entry.Kind == "intake-rejected");
        Assert.Equal("intake", intakeRejected.Entry.Source);
        using (var p = JsonDocument.Parse(intakeRejected.Entry.ParamsJson))
            Assert.Contains("无法核验完整性", p.RootElement.GetProperty("Error").GetString());
        var runRejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
        Assert.Equal("run", runRejected.Entry.Source);
        using (var p = JsonDocument.Parse(runRejected.Entry.ParamsJson))
        {
            Assert.Equal("LoadFailed", p.RootElement.GetProperty("level").GetString());
            Assert.Contains("无法核验完整性", p.RootElement.GetProperty("error").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Rollback_ValidSidecarCensus_DoesNotRereadSource()
    {
        // R10 Minor#2：rollback 裸 Load → LoadWithCensus——sidecar 有效（路径+源哈希匹配）
        // 时用 persisted census，不重读源。构造：Green 提交事务后源损坏 + sidecar 对当前
        // 源字节仍有效（Store 契约即路径+哈希匹配）→ 修复前裸 Load 走 R9 重建分支重读
        // 损坏源 → 抛 → intake-rejected exit 1；修复后 persisted 直载 → 撤回成功。
        // 同时钉住 Yellow census 上 rollback 的 R7 撤回豁免（不加闸）。
        string dwg = CreateProjectDwg("rollback-src.dwg");
        var (intakeCode, _, _) = Run("intake", dwg);
        Assert.Equal(0, intakeCode); // Green 基线 + working 物化
        string planPath = WritePlan();
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", dwg);
        Assert.Equal(0, runCode);
        string txId = JsonDocument.Parse(runOut).RootElement.GetProperty("txId").GetString()!;
        // 源损坏（不可读）+ sidecar 对当前源字节有效（census 非空 → Yellow 态）。
        File.WriteAllBytes(dwg, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        UnparsedCensusStore.Save(_project, dwg,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));
        byte[] sourceBefore = File.ReadAllBytes(dwg);

        var (code, stdout, stderr) = Run("rollback", txId, "--drawing", dwg);

        Assert.Equal(0, code); // 修复前：裸 Load 重读损坏源 → exit 1（RED）
        Assert.Contains("已撤回事务", stdout);
        Assert.Equal(sourceBefore, File.ReadAllBytes(dwg)); // 源只读（不重写不修复）
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "rollback");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "intake-rejected"); // 未走拒绝通道
        // working 已恢复快照（1 条实体）：以 persisted 加载核验（不触发源重读）。
        var session = DrawingSession.Load(dwg, UnparsedCensusStore.Load(_project, dwg));
        Assert.Single(session.Summarize().Entities);
        Assert.Equal(2, session.Unparsed.TotalCount); // Yellow census 保持（豁免不改判定）
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Check_ValidSidecarCensus_DoesNotRereadSource()
    {
        // T5 顺手修②：check 裸 Load → LoadWithCensus——只读路径享受 persisted census
        // 直载（与 run/rollback 同一 LoadWithCensus 等价一致性）。构造镜像 R10 rollback
        // 钉住：Green 基线物化 working 后源损坏 + sidecar 对当前源字节有效 → 修复前
        // 裸 Load 走 R9 重建分支重读损坏源 → 抛 → intake-rejected exit 1（RED）；
        // 修复后 persisted 直载 → check 只读审查照常（Yellow 不拦只读，built-in 无
        // mandatory → exit 0）。check 不改图纸：源/working 字节不变。
        string dwg = CreateProjectDwg("check-src.dwg");
        var (intakeCode, _, _) = Run("intake", dwg);
        Assert.Equal(0, intakeCode); // Green 基线 + working 物化
        string working = DrawingSession.WorkingPathFor(dwg);
        File.WriteAllBytes(dwg, new byte[] { 0x00, 0x01, 0x02, 0x03 }); // 源损坏（不可读）
        UnparsedCensusStore.Save(_project, dwg,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));
        Assert.Equal(2, UnparsedCensusStore.Load(_project, dwg).TotalCount); // sidecar 有效前提
        byte[] sourceBefore = File.ReadAllBytes(dwg);
        byte[] workingBefore = File.ReadAllBytes(working);

        var (code, stdout, stderr) = Run("check", "--drawing", dwg);

        Assert.Equal(0, code); // 修复前：裸 Load 重读损坏源 → exit 1（RED）
        Assert.Contains("issues", stdout);
        Assert.Equal(sourceBefore, File.ReadAllBytes(dwg)); // 源只读
        Assert.Equal(workingBefore, File.ReadAllBytes(working)); // check 不改 working
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "check-run");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "intake-rejected"); // 未走拒绝通道
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void ReviewReject_ValidSidecarCensus_DoesNotRereadSource()
    {
        // T5 顺手修②：review signoff --reject 裸 Load → LoadWithCensus（驳回回滚加载
        // 与 rollback 命令同一 persisted 直载口径）。构造镜像 R10 rollback 钉住：
        // Green 提交事务后源损坏 + sidecar 有效 → 修复前裸 Load 重读损坏源 → 抛 →
        // intake-rejected exit 1（RED）；修复后 persisted 直载 → 驳回回滚成功。
        // Yellow census 上 review 驳回 = R7 撤回处置豁免（快照恢复非新增操作，不加闸）。
        string dwg = CreateProjectDwg("review-src.dwg");
        var (intakeCode, _, _) = Run("intake", dwg);
        Assert.Equal(0, intakeCode); // Green 基线 + working 物化
        string planPath = WritePlan();
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", dwg);
        Assert.Equal(0, runCode);
        string txId = JsonDocument.Parse(runOut).RootElement.GetProperty("txId").GetString()!;
        File.WriteAllBytes(dwg, new byte[] { 0x00, 0x01, 0x02, 0x03 }); // 源损坏（不可读）
        UnparsedCensusStore.Save(_project, dwg,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));
        byte[] sourceBefore = File.ReadAllBytes(dwg);
        CommandRouter.IdentityOverride = new ReviewerId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        try
        {
            var (code, stdout, stderr) = Run("review", "signoff", "--drawing", dwg, "--tx", txId, "--reject", "--comment", "复核");

            Assert.Equal(0, code); // 修复前：裸 Load 重读损坏源 → exit 1（RED）
            Assert.Contains("已驳回事务", stdout);
            Assert.Equal(sourceBefore, File.ReadAllBytes(dwg)); // 源只读（不重写不修复）
            var records = AuditRecords();
            Assert.Contains(records, r => r.Entry.Kind == "review-rejected");
            Assert.Contains(records, r => r.Entry.Kind == "rollback");
            Assert.DoesNotContain(records, r => r.Entry.Kind == "intake-rejected"); // 未走拒绝通道
            // working 已恢复快照（1 条实体）：以 persisted 加载核验（不触发源重读）。
            var session = DrawingSession.Load(dwg, UnparsedCensusStore.Load(_project, dwg));
            Assert.Single(session.Summarize().Entities);
            Assert.Equal(2, session.Unparsed.TotalCount); // Yellow census 保持（豁免不改判定）
            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            Assert.True(audit.VerifyChain(out _));
        }
        finally { CommandRouter.IdentityOverride = null; }
    }

    private sealed class ReviewerId : IIdentityProvider
    {
        public ReviewerId(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
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
