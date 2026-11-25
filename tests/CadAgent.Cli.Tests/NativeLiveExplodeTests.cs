using System.Security.Cryptography;
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;
using Xunit;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Task 9（切片B §4）：run 主闸的炸开门（explode-native）准入与执行。
/// 夹具=合成 TCH DXF 通知（DxfFixture.CreateTempTchDxf）→ census Yellow；overlay 含
/// 21E00（TDbWall，原生参数行**故意陈旧**：LeftWidth=120）与炸开证据（Fully=true、bbox 与
/// 参数一致），21F00（原生参数行与重读逐值一致）——即 21E00 只能凭炸开门放行、21F00 走
/// 参数门，两门各自被真实走到。FakeRuntime（RuntimeFactory 注入）记录调用序、
/// ReadExplodeEvidence→ReadNativeParams(path)→Open 三段开路、ExplodeReplaceByHandle 记录并
/// 从活文档读回集移除句柄（KeepHandleAfterExplode 可关掉该副作用覆盖读回失败面）。
/// 确定性：零真实 COM/厂商进程；temp 目录清理；静态缝（RuntimeFactory/Sha256File/Identity）用毕还原。
/// </summary>
public class NativeLiveExplodeTests : IDisposable
{
    private static readonly byte[] LiveBytes = { 0x4C, 0x49, 0x56, 0x45 };

    private readonly string _project = Path.Combine(Path.GetTempPath(), $"nativelive-explode-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly FakeExplodeNativeRuntime _rt = new();
    private readonly Func<string, string> _origSha256 = CommandRouter.Sha256File;

    public NativeLiveExplodeTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempTchDxf();
        CommandRouter.RuntimeFactory = () => _rt;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = null;
        CommandRouter.IdentityOverride = null;
        CommandRouter.Sha256File = _origSha256;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
        if (File.Exists(_drawing + ".live.dwg")) File.Delete(_drawing + ".live.dwg");
    }

    // ---- 夹具 ----

    private string OutPath => _drawing + ".live.dwg";

    /// <summary>原生读参行（overlay 侧与 live 重读侧共用形状；bbox 与炸开证据一致）。</summary>
    private static NativeEntityParam Wall(string handle, string leftWidth, double x0) =>
        new(handle, "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LeftWidth"] = leftWidth,
                ["TotalWidth"] = "240"
            },
            new[] { x0, 0.0, 0.0, x0 + 100.0, 100.0, 3000.0 },
            DateTimeOffset.UtcNow, ReadOk: true, Layer: "0dt");

    /// <summary>炸开取证行（Fully=true、bbox 与 21E00 原生参数一致——R4 数据契约）。</summary>
    private static ExplodeEvidence ExplodeRow(string handle = "21E00", bool fully = true) =>
        new(handle, "TDbWall", "ImpCurve", "0dt",
            new[] { 0.0, 0.0, 0.0, 100.0, 100.0, 3000.0 }, fully, 4, 0,
            new List<ExplodePrimitive>
            {
                new("line", new[] { 0.0, 0.0, 0.0, 100.0, 0.0, 0.0 }, null, null),
                new("line", new[] { 0.0, 100.0, 0.0, 100.0, 100.0, 0.0 }, null, null)
            },
            new Dictionary<string, string>(), null, DateTimeOffset.UtcNow);

    private void WriteOverlay(NativeEntityParam[] natives, ExplodeEvidence[]? evidences = null)
    {
        var overlay = new SemanticOverlay();
        foreach (var n in natives) overlay.UpsertNative(n);
        foreach (var e in evidences ?? Array.Empty<ExplodeEvidence>()) overlay.UpsertExplode(e);
        overlay.Save(Path.Combine(_project, ".cadagent", "semantic.json"));
    }

    /// <summary>R4 fake 数据契约：path 形态返回**全部**句柄类目标（21E00/21F00）的现行参数。
    /// 21E00 故意与 overlay 陈旧行不同（LeftWidth 140≠120）→ 参数门 StaleMismatch，
    /// 仅炸开门可放行（准入矩阵两门都被真实走到）。</summary>
    private void SeedLive(params NativeEntityParam[] records)
    {
        foreach (var r in records)
        {
            _rt.PathReadResults.Add(r);
            _rt.ReadResults.Add(r);
        }
    }

    private void SeedStandardLive()
    {
        SeedLive(Wall("21E00", "140", 0), Wall("21F00", "120", 200));
        _rt.ExplodeReadResults.Add(ExplodeRow());
    }

    private string WritePlan(string json)
    {
        string path = Path.Combine(_project, "plan.json");
        File.WriteAllText(path, json);
        return path;
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private List<AuditRecord> AuditRecords()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static int IndexOfKind(List<AuditRecord> records, string kind)
    {
        int i = records.FindIndex(r => r.Entry.Kind == kind);
        Assert.True(i >= 0, $"缺审计 {kind}（实际: {string.Join("|", records.Select(r => r.Entry.Kind))}）");
        return i;
    }

    // ---- 用例 ----

    /// <summary>brief 用例 1：全放行（单 explode-native）→ 审计 native-explode-step 四键、
    /// .live.dwg 存在、源件 SHA 不变、native-live-committed；会话开路序三段固定。</summary>
    [Fact]
    public void ExplodeNative_AllPass_Executes_AndAudits()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow() });
        SeedStandardLive();
        string plan = WritePlan(
            """{"planId":"NE-1","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        // 会话开路序（R4/切片B）：读炸开证据 → 参数门重读(path) → Open，顺序不可换。
        Assert.True(_rt.Calls.Count >= 3, $"calls={string.Join("|", _rt.Calls)}");
        Assert.Equal("read-explode", _rt.Calls[0]);
        Assert.Equal("read-native:path", _rt.Calls[1]);
        Assert.Equal("open:" + Path.GetFileName(_drawing), _rt.Calls[2]);
        // 执行与交付：桥替换落到 .live.dwg 候选路径，唯一写出是交付件。
        Assert.Contains("explode:21E00", _rt.Calls);
        Assert.Equal(OutPath, _rt.SavedPath);
        Assert.True(File.Exists(OutPath));
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 源件字节零写入

        var records = AuditRecords();
        var explodeStep = Assert.Single(records, r => r.Entry.Kind == "native-explode-step");
        Assert.Equal("run", explodeStep.Entry.Source);
        Assert.Equal("NE-1", explodeStep.Entry.PlanRef);
        Assert.Equal("21E00", explodeStep.Entry.Target);
        using (var p = JsonDocument.Parse(explodeStep.Entry.ParamsJson))
        {
            Assert.Equal("21E00", p.RootElement.GetProperty("hex").GetString());
            Assert.Equal(4, p.RootElement.GetProperty("newCount").GetInt32());
            Assert.Equal(OutPath, p.RootElement.GetProperty("savedPath").GetString());
            Assert.True(p.RootElement.GetProperty("bboxMatch").GetBoolean());
        }
        // 准入审计：21E00 只凭炸开门放行（参数门陈旧），加性披露 explodeGateHandles；
        // 准入结论不得被读成"参数已读全"。
        var gatePass = Assert.Single(records, r => r.Entry.Kind == "native-gate-pass");
        using (var p = JsonDocument.Parse(gatePass.Entry.ParamsJson))
        {
            Assert.Equal("21E00", p.RootElement.GetProperty("handles")[0].GetString());
            Assert.Equal(ShaOf(before), p.RootElement.GetProperty("sourceSha").GetString());
            Assert.Matches("^[0-9A-F]{64}$", p.RootElement.GetProperty("rereadHash").GetString()!);
            Assert.Equal(new[] { "21E00" },
                p.RootElement.GetProperty("explodeGateHandles").EnumerateArray()
                    .Select(h => h.GetString()).ToArray());
        }
        var committed = Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
        using (var p = JsonDocument.Parse(committed.Entry.ParamsJson))
        {
            Assert.Equal(OutPath, p.RootElement.GetProperty("outPath").GetString());
            Assert.Equal(ShaOf(before), p.RootElement.GetProperty("sourceSha").GetString());
            Assert.Equal(ShaOf(File.ReadAllBytes(OutPath)),
                p.RootElement.GetProperty("liveSha").GetString());
        }
        // 纯炸开计划不落句柄步审计；不落 tx / run-rejected。
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-step");
        Assert.DoesNotContain(records, r => r.Entry.Kind is "run-rejected" or "tx-start" or "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    /// <summary>brief 用例 2：混合顺序 [explode-native 21E00, change-layer 21F00→L] →
    /// 审计顺序 native-explode-step 先于 native-live-step；两操作都真的执行；交付成功。</summary>
    [Fact]
    public void ExplodeNative_MixedWithChangeLayer_OrderPreserved()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0), Wall("21F00", "120", 200) },
            new[] { ExplodeRow() });
        SeedStandardLive();
        string plan = WritePlan(
            """{"planId":"NE-2","steps":[{"kind":"explode-native","params":{"handle":"21E00"}},{"kind":"change-layer","params":{"handle":"21F00","layer":"L"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        // 执行顺序（运行时调用面）：先桥替换，后句柄 op。
        int explodeCall = _rt.Calls.IndexOf("explode:21E00");
        int layerCall = _rt.Calls.IndexOf("change-layer:21F00:L");
        Assert.True(explodeCall >= 0, $"calls={string.Join("|", _rt.Calls)}");
        Assert.True(layerCall > explodeCall, $"calls={string.Join("|", _rt.Calls)}");
        // 审计顺序（计划顺序）：native-explode-step → native-live-step。
        var records = AuditRecords();
        Assert.True(IndexOfKind(records, "native-explode-step") < IndexOfKind(records, "native-live-step"),
            string.Join("|", records.Select(r => r.Entry.Kind)));
        var liveStep = Assert.Single(records, r => r.Entry.Kind == "native-live-step");
        using (var p = JsonDocument.Parse(liveStep.Entry.ParamsJson))
        {
            Assert.Equal("change-layer", p.RootElement.GetProperty("kind").GetString());
            Assert.Equal("21F00", p.RootElement.GetProperty("handle").GetString());
        }
        // 炸开步不参与参数面聚合（否则会期待 21E00 仍在 → 假拒）。
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
        Assert.True(File.Exists(OutPath));
        // 21F00 走参数门（与 overlay 逐值一致）——不列 explodeGateHandles。
        var gatePass = Assert.Single(records, r => r.Entry.Kind == "native-gate-pass");
        using (var p = JsonDocument.Parse(gatePass.Entry.ParamsJson))
            Assert.Equal(new[] { "21E00" },
                p.RootElement.GetProperty("explodeGateHandles").EnumerateArray()
                    .Select(h => h.GetString()).ToArray());
    }

    /// <summary>brief 用例 3：炸开证据 Fully=false → 合并判据 NotFullyExplodable（炸开门拒因
    /// 优先），payload 同时带参数门 verdict；无 ops、不交付、exit 1。</summary>
    [Fact]
    public void ExplodeNative_NotFully_Refused()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow(fully: false) });
        SeedStandardLive();
        string plan = WritePlan(
            """{"planId":"NE-3","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("NotFullyExplodable", stderr);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("explode:") || c.StartsWith("change-layer")
            || c == "saveas");
        Assert.False(File.Exists(OutPath));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
        {
            var v = p.RootElement.GetProperty("verdicts")[0];
            Assert.Equal("21E00", v.GetProperty("handle").GetString());
            Assert.Equal("NotFullyExplodable", v.GetProperty("verdict").GetString());
            // R14：合并判据不返回参数门理由，审计并行披露参数门独立结论。
            Assert.Equal("StaleMismatch", v.GetProperty("paramsVerdict").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "run-rejected");
    }

    /// <summary>brief 用例 4：桥替换自报成功但 (null) 读回仍含原句柄 → 读回闸
    /// native-live-failed{readback:"explode-native"}，不 SaveAs 不交付，exit 1。</summary>
    [Fact]
    public void ExplodeNative_RereadGoneFail_ReadbackFails()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow() });
        SeedStandardLive();
        _rt.KeepHandleAfterExplode = true; // 替换"成功"但活文档状态未变（缺陷/失败运行时）
        string plan = WritePlan(
            """{"planId":"NE-4","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("炸开", stderr);
        Assert.Contains("explode:21E00", _rt.Calls); // 替换确实执行过
        Assert.DoesNotContain("saveas", _rt.Calls);
        Assert.Null(_rt.SavedPath);
        Assert.False(File.Exists(OutPath));
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
        {
            Assert.Equal("explode-native", p.RootElement.GetProperty("readback").GetString());
            Assert.Equal("21E00", p.RootElement.GetProperty("handle").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    /// <summary>用例 5（补充等价类）：目标句柄既无 live 参数行、也无炸开证据行 →
    /// 合并判据 ExplodeNotRead（炸开门拒因优先于参数门 StaleMismatch），payload 双 verdict 齐全。</summary>
    [Fact]
    public void ExplodeNative_AbsentFromParamsAndEvidence_ExplodeNotRead()
    {
        // overlay 有 21E00 原生行（R11 匹配前提=可定位），但既无炸开证据、live 也无其参数行。
        WriteOverlay(new[] { Wall("21E00", "120", 0) });
        SeedLive(Wall("21F00", "120", 200)); // 21E00 无现行参数行；无炸开证据行
        string plan = WritePlan(
            """{"planId":"NE-5","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("ExplodeNotRead", stderr);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("explode:") || c == "saveas");
        Assert.False(File.Exists(OutPath));
        var refused = Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-gate-refused");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        var v = p.RootElement.GetProperty("verdicts")[0];
        Assert.Equal("ExplodeNotRead", v.GetProperty("verdict").GetString());
        Assert.Equal("StaleMismatch", v.GetProperty("paramsVerdict").GetString());
    }

    /// <summary>R16 返工用例 a：move **仅凭炸开门放行**（参数门重读无 21E00 行、炸开证据
    /// Fully=true 且 bbox 对齐）→ 执行，并按准入快照炸开证据 bbox 做位移读回（参数面
    /// before-bbox 缺失时的基准回退，R16 子裁定）→ 交付。准入逐字披露 explodeGateHandles。</summary>
    [Fact]
    public void Move_ExplodeGateOnly_NoParamsRow_Executes_ReadbackUsesEvidenceBbox()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow() });
        // 参数门重读无 21E00（StaleMismatch）；执行会话可见 21E00（move 可施加）。
        _rt.PathReadResults.Add(Wall("21F00", "120", 200));
        _rt.ReadResults.Add(Wall("21E00", "120", 0));
        _rt.ExplodeReadResults.Add(ExplodeRow());
        string plan = WritePlan(
            """{"planId":"NE-7","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":20}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("move:21E00:10:20", _rt.Calls);
        Assert.True(File.Exists(OutPath));
        var records = AuditRecords();
        var gatePass = Assert.Single(records, r => r.Entry.Kind == "native-gate-pass");
        using (var p = JsonDocument.Parse(gatePass.Entry.ParamsJson))
            Assert.Equal(new[] { "21E00" },
                p.RootElement.GetProperty("explodeGateHandles").EnumerateArray()
                    .Select(h => h.GetString()).ToArray());
        // 读回按炸开证据 bbox 基准平移通过（after bbox 可得 → 不落降级 readback 键）。
        var step = Assert.Single(records, r => r.Entry.Kind == "native-live-step");
        using (var p = JsonDocument.Parse(step.Entry.ParamsJson))
        {
            Assert.Equal("move", p.RootElement.GetProperty("kind").GetString());
            Assert.Equal("21E00", p.RootElement.GetProperty("handle").GetString());
            Assert.False(p.RootElement.TryGetProperty("readback", out _));
        }
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
    }

    /// <summary>R16 返工用例 b：change-layer **仅凭炸开门**（参数门重读无该句柄行、炸开证据
    /// 齐全）→ 合并判据本身 Pass，但层名读回无参数面记录可对账 → 准入 fail-closed 拒绝
    /// reason="change-layer-requires-params"（R16 子裁定）；verdict 双结论照实披露；无 op、不交付。</summary>
    [Fact]
    public void ChangeLayer_ExplodeGateOnly_Refused_RequiresParams()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow() });
        _rt.PathReadResults.Add(Wall("21F00", "120", 200));
        _rt.ReadResults.Add(Wall("21E00", "120", 0));
        _rt.ExplodeReadResults.Add(ExplodeRow());
        string plan = WritePlan(
            """{"planId":"NE-8","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"L"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("change-layer", stderr);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("change-layer") || c == "saveas");
        Assert.False(File.Exists(OutPath));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
        {
            Assert.Equal("change-layer-requires-params", p.RootElement.GetProperty("reason").GetString());
            var v = p.RootElement.GetProperty("verdicts")[0];
            Assert.Equal("21E00", v.GetProperty("handle").GetString());
            // 合并判据 Pass（炸开门满足）；拒绝来自 change-layer 专属政策（参数门未过）。
            Assert.Equal("Pass", v.GetProperty("verdict").GetString());
            Assert.Equal("StaleMismatch", v.GetProperty("paramsVerdict").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-gate-pass");
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
    }

    /// <summary>R16 返工用例 c：delete **仅凭炸开门**（参数门重读无该句柄行）→ 放行并执行；
    /// 读回只验句柄消失（无需 bbox/层名基准，故不受 change-layer 政策约束）→ 交付。</summary>
    [Fact]
    public void Delete_ExplodeGateOnly_Executes_AbsenceReadback()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow() });
        _rt.PathReadResults.Add(Wall("21F00", "120", 200));
        _rt.ReadResults.Add(Wall("21E00", "120", 0));
        _rt.ExplodeReadResults.Add(ExplodeRow());
        string plan = WritePlan(
            """{"planId":"NE-9","steps":[{"kind":"delete","params":{"handle":"21E00"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("delete:21E00", _rt.Calls);
        Assert.Empty(_rt.ReadResults); // 句柄已删除（读回缺失即 Pass）
        Assert.True(File.Exists(OutPath));
        var records = AuditRecords();
        var gatePass = Assert.Single(records, r => r.Entry.Kind == "native-gate-pass");
        using (var p = JsonDocument.Parse(gatePass.Entry.ParamsJson))
            Assert.Equal(new[] { "21E00" },
                p.RootElement.GetProperty("explodeGateHandles").EnumerateArray()
                    .Select(h => h.GetString()).ToArray());
        var step = Assert.Single(records, r => r.Entry.Kind == "native-live-step");
        using (var p = JsonDocument.Parse(step.Entry.ParamsJson))
            Assert.Equal("delete", p.RootElement.GetProperty("kind").GetString());
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
    }

    /// <summary>R16 返工用例 d（补充等价类）：move 仅凭炸开门放行，且炸开证据 bbox 也为 null
    /// （参数门无行 + 炸开无 bbox）→ 读回无任何可核基准 → native-live-failed
    /// {readback:"no-bbox-baseline"}，不 SaveAs 不交付（不得退化为"无从比对即通过"）。</summary>
    [Fact]
    public void Move_ExplodeGateOnly_NoBboxBaseline_FailsFailClosed()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) },
            new[] { ExplodeRow() with { Bbox = null } });
        _rt.PathReadResults.Add(Wall("21F00", "120", 200));
        _rt.ReadResults.Add(Wall("21E00", "120", 0));
        _rt.ExplodeReadResults.Add(ExplodeRow() with { Bbox = null });
        string plan = WritePlan(
            """{"planId":"NE-10","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":20}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("读回无基准", stderr);
        Assert.Contains("move:21E00:10:20", _rt.Calls); // op 已执行，失败在读回闸
        Assert.DoesNotContain("saveas", _rt.Calls);
        Assert.False(File.Exists(OutPath));
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
        {
            Assert.Equal("no-bbox-baseline", p.RootElement.GetProperty("readback").GetString());
            Assert.Equal("21E00", p.RootElement.GetProperty("handle").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    /// <summary>白名单扩宽的守卫（保持切片A 契约）：含 explode-native 但缺 handle 参数 →
    /// 不属句柄寻址准入，回落既有 run-rejected；无任何 native-* 审计、无运行时调用。</summary>
    [Fact]
    public void ExplodeNativeWithoutHandle_FallsBackToRunRejected()
    {
        WriteOverlay(new[] { Wall("21E00", "120", 0) }, new[] { ExplodeRow() });
        SeedStandardLive();
        string plan = WritePlan(
            """{"planId":"NE-6","steps":[{"kind":"explode-native","params":{}}]}""");

        var (code, stdout, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(OutPath));
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "run-rejected");
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    /// <summary>
    /// Fake 厂商运行时（零 COM）：ReadExplodeEvidence 记录 "read-explode" 并对"已有打开文档"
    /// 抛 CadRuntimeException（顺序守卫：真实契约要求无打开文档）；ReadNativeParams 按形态区分
    /// path（准入重读事实）/null（活文档读回）；ExplodeReplaceByHandle 记录调用、默认从读回集
    /// 移除原句柄（KeepHandleAfterExplode=true 时保留以覆盖读回失败面）并自报 NewCount=4+
    /// BboxMatch=true；MoveByHandle 把位移落到读回 bbox（读回事实镜像执行）；SaveAs 落合成
    /// .live.dwg 字节（让 liveSha 可计算）。
    /// </summary>
    private sealed class FakeExplodeNativeRuntime : ICadRuntime
    {
        public List<string> Calls { get; } = new();
        public List<NativeEntityParam> PathReadResults { get; } = new();
        public List<NativeEntityParam> ReadResults { get; } = new();
        public List<ExplodeEvidence> ExplodeReadResults { get; } = new();
        public string? SavedPath { get; private set; }
        public bool KeepHandleAfterExplode { get; set; }
        private string? _openedPath;

        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string path)
        {
            _openedPath = path;
            Calls.Add("open:" + Path.GetFileName(path));
        }

        public void SaveAs(string path)
        {
            SavedPath = path;
            File.WriteAllBytes(path, LiveBytes);
            Calls.Add("saveas");
        }

        public void Close(bool save) => Calls.Add("close");
        public void Restart() { }
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => Calls.Add("add-line");
        public void AddText(double x, double y, string text, double height, string? layer = null) => Calls.Add("add-text");
        public void AddCircle(double x, double y, double radius, string? layer = null) => Calls.Add("add-circle");
        public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null)
            => Calls.Add("add-insert");
        public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) => Calls.Add("ensure-block");
        public int EntityCount() => ReadResults.Count;

        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath)
        {
            Calls.Add("read-explode");
            if (_openedPath != null)
                throw new CadRuntimeException("已有打开的图纸；ReadExplodeEvidence(path) 须在无打开文档时调用");
            return ExplodeReadResults.ToList();
        }

        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null)
        {
            Calls.Add(drawingPath == null ? "read-native:doc" : "read-native:path");
            return drawingPath == null ? ReadResults.ToList() : PathReadResults.ToList();
        }

        public ExplodeReplaceResult ExplodeReplaceByHandle(string hexHandle, string liveOutPath)
        {
            Calls.Add("explode:" + hexHandle);
            if (!KeepHandleAfterExplode) ReadResults.RemoveAll(n => n.Handle == hexHandle);
            return new ExplodeReplaceResult(hexHandle, 4, liveOutPath, true, 1e-3);
        }

        public void ChangeLayerByHandle(string hexHandle, string layer)
        {
            Calls.Add($"change-layer:{hexHandle}:{layer}");
            int i = ReadResults.FindIndex(n => n.Handle == hexHandle);
            if (i >= 0) ReadResults[i] = ReadResults[i] with { Layer = layer };
        }

        public void MoveByHandle(string hexHandle, double dx, double dy)
        {
            Calls.Add($"move:{hexHandle}:{dx}:{dy}");
            // 读回事实镜像执行：位移落 bbox（仅凭炸开门放行的 move 以炸开证据 bbox 为基准核对）。
            int i = ReadResults.FindIndex(n => n.Handle == hexHandle);
            if (i < 0) return;
            ReadResults[i] = ReadResults[i].Bbox is { Length: 6 } b
                ? ReadResults[i] with { Bbox = new[] { b[0] + dx, b[1] + dy, b[2], b[3] + dx, b[4] + dy, b[5] } }
                : ReadResults[i];
        }

        public void DeleteByHandle(string hexHandle)
        {
            Calls.Add("delete:" + hexHandle);
            ReadResults.RemoveAll(n => n.Handle == hexHandle);
        }

        public void Dispose() { }
    }
}
