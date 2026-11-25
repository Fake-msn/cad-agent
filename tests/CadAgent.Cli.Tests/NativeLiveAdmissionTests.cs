using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadAgent.Adapters.Acad;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;
using Xunit;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Task 9（切片A §4.2）：run 主闸逐实体松绑准入 + native-live 独执行。
/// 夹具=合成 TCH DXF 通知（DxfFixture.CreateTempTchDxf，镜像 UnparsedGateCliTests 的
/// 通知夹具技法）→ Yellow；overlay 含 21E00（TDbWall）→ 计划全为句柄寻址步骤时逐实体判据；
/// FakeRuntime（RuntimeFactory 注入）提供 ReadNativeParams/handle ops/SaveAs 记录——
/// 全 Pass 才执行并交付 .live.dwg（源件字节零写入），任一非 Pass 拒绝且无 ops。
/// 确定性：不触真实 COM/厂商进程；temp 目录清理；RuntimeFactory 在 Dispose 复位。
/// </summary>
public class NativeLiveAdmissionTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"nativelive-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly FakeNativeRuntime _rt = new();
    // 哈希缝（finding 2d/5c）：注入空串/定向空值覆盖 fail-closed 分支，用毕还原。
    private readonly Func<string, string> _origSha256 = CommandRouter.Sha256File;
    // 桥缝（复审修复波 I1/I2/I3）：深读用例需要"桥可用"事实——占位桥文件含真桥命令标记，
    // 信任位置指向其目录（生产=AcadRuntime.CheckBridgeAvailability 的同一路径）。
    private readonly string? _origBridgeDll = AcadRuntime.BridgeDllOverride;
    private readonly Func<string, string?> _origTrustedSource = AcadRuntime.TrustedPathsSource;
    private readonly Func<string, bool> _origStubScan = AcadRuntime.StubScan;
    private readonly string _bridgeDir;

    public NativeLiveAdmissionTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempTchDxf();
        CommandRouter.RuntimeFactory = () => _rt;
        _bridgeDir = Path.Combine(_project, "bridge");
        Directory.CreateDirectory(_bridgeDir);
        string bridgeDll = Path.Combine(_bridgeDir, AcadRuntime.BridgeDllName);
        File.WriteAllBytes(bridgeDll, Encoding.ASCII.GetBytes("CADAGENT_TARCH_READ"));
        AcadRuntime.BridgeDllOverride = bridgeDll;
        AcadRuntime.TrustedPathsSource = _ => _bridgeDir;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = null;
        CommandRouter.IdentityOverride = null;
        CommandRouter.Sha256File = _origSha256;
        AcadRuntime.BridgeDllOverride = _origBridgeDll;
        AcadRuntime.TrustedPathsSource = _origTrustedSource;
        AcadRuntime.StubScan = _origStubScan;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
        if (File.Exists(_drawing + ".live.dwg")) File.Delete(_drawing + ".live.dwg");
    }

    // ---- 夹具 ----

    /// <summary>overlay 原生读参记录（Params/Bbox 与 FakeRuntime 重读逐值一致才 Pass）。</summary>
    private static NativeEntityParam Wall(string handle = "21E00", string layer = "0dt", double[]? bbox = null) =>
        new(handle, "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LeftWidth"] = "120",
                ["TotalWidth"] = "240"
            },
            bbox ?? new[] { 0.0, 0.0, 0.0, 100.0, 100.0, 3000.0 },
            DateTimeOffset.UtcNow, ReadOk: true, Layer: layer);

    /// <summary>炸开取证行（Fully=true；bbox 与 21E00 原生参数/执行会话一致）——供"参数门漂移
    /// 仅凭炸开门放行"的深读用例。</summary>
    private static ExplodeEvidence ExplodeRow(string handle = "21E00") =>
        new(handle, "TDbWall", "ImpCurve", "0dt",
            new[] { 0.0, 0.0, 0.0, 100.0, 100.0, 3000.0 }, true, 2, 0,
            new List<ExplodePrimitive>
            {
                new("line", new[] { 0.0, 0.0, 0.0, 100.0, 0.0, 0.0 }, null, null)
            },
            new Dictionary<string, string>(), null, DateTimeOffset.UtcNow);

    private void WriteOverlay(params NativeEntityParam[] records) => WriteOverlay(records, null);

    private void WriteOverlay(NativeEntityParam[] records, ExplodeEvidence[]? evidences)
    {
        var overlay = new SemanticOverlay();
        foreach (var r in records) overlay.UpsertNative(r);
        foreach (var e in evidences ?? Array.Empty<ExplodeEvidence>()) overlay.UpsertExplode(e);
        overlay.Save(Path.Combine(_project, ".cadagent", "semantic.json"));
    }

    /// <summary>原生读参种子：path 形态（准入重读=独立副本快照）与 null 形态（执行会话
    /// 初始事实）各投一份——failsafe：漏投 path 形态则准入必然 StaleMismatch。</summary>
    private void SeedNative(params NativeEntityParam[] records)
    {
        foreach (var r in records)
        {
            _rt.PathReadResults.Add(r);
            _rt.ReadResults.Add(r);
        }
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

    private static string[] VerdictsOf(AuditRecord record)
    {
        using var p = JsonDocument.Parse(record.Entry.ParamsJson);
        return p.RootElement.GetProperty("verdicts").EnumerateArray()
            .Select(v => v.GetProperty("verdict").GetString()!).ToArray();
    }

    // ---- 用例 ----

    /// <summary>overlay 文件路径（Diagnostics 断言用）。</summary>
    private string OverlayPath => Path.Combine(_project, ".cadagent", "semantic.json");

    [Fact]
    public void Yellow_HandleStep_AllPass_ExecutesOps_DeliversLiveDwg()
    {
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-1","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, stdout, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        Assert.Equal(_drawing + ".live.dwg", _rt.SavedPath);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 源件字节零写入
        // 成功收据（finding 11）：stdout 单条 JSON {"ok":true,outPath,handles}。
        using (var receipt = JsonDocument.Parse(stdout))
        {
            Assert.True(receipt.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(_drawing + ".live.dwg", receipt.RootElement.GetProperty("outPath").GetString());
            Assert.Equal(new[] { "21E00" },
                receipt.RootElement.GetProperty("handles").EnumerateArray()
                    .Select(h => h.GetString()).ToArray());
        }
        var records = AuditRecords();
        var gatePass = Assert.Single(records, r => r.Entry.Kind == "native-gate-pass");
        Assert.Equal("run", gatePass.Entry.Source);
        Assert.Equal("NL-1", gatePass.Entry.PlanRef);
        using (var p = JsonDocument.Parse(gatePass.Entry.ParamsJson))
        {
            Assert.Equal("21E00", p.RootElement.GetProperty("handles")[0].GetString());
            Assert.Equal(ShaOf(before), p.RootElement.GetProperty("sourceSha").GetString());
            // 台账 T7@T9：准入重读指纹加性落 native-gate-pass（64 位大写 hex，确定性）。
            string rereadHash = p.RootElement.GetProperty("rereadHash").GetString()!;
            Assert.Matches("^[0-9A-F]{64}$", rereadHash);
        }
        Assert.Single(records, r => r.Entry.Kind == "native-live-step");
        var committed = Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
        using (var p = JsonDocument.Parse(committed.Entry.ParamsJson))
        {
            Assert.Equal(_drawing + ".live.dwg", p.RootElement.GetProperty("outPath").GetString());
            Assert.Equal(ShaOf(before), p.RootElement.GetProperty("sourceSha").GetString());
            Assert.Equal(ShaOf(File.ReadAllBytes(_drawing + ".live.dwg")),
                p.RootElement.GetProperty("liveSha").GetString());
        }
        // native-live 独执行：不落 tx（无 StepEngine）、不走既有 run-rejected。
        Assert.DoesNotContain(records, r => r.Entry.Kind is "run-rejected" or "tx-start" or "tx-commit");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Yellow_HandleStep_RereadMismatch_Refuses_NoOps()
    {
        WriteOverlay(Wall());
        // 重读 bbox 与 overlay 不符（实体已漂移）→ 参数门 StaleMismatch；R16：全部步骤走合并
        // 判据（参数门 ∨ 炸开门），overlay 无该句柄炸开证据 → 合并判据 ExplodeNotRead（炸开门
        // 拒因优先，R14）——dual verdict 逐字钉死。
        SeedNative(Wall(bbox: new[] { 0.0, 0.0, 0.0, 200.0, 100.0, 3000.0 }));
        string plan = WritePlan(
            """{"planId":"NL-2","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("StaleMismatch", stderr);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("change-layer"));
        Assert.DoesNotContain(_rt.Calls, c => c == "saveas");
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
        {
            var v = p.RootElement.GetProperty("verdicts")[0];
            Assert.Equal("21E00", v.GetProperty("handle").GetString());
            Assert.Equal("ExplodeNotRead", v.GetProperty("verdict").GetString());
            Assert.Equal("StaleMismatch", v.GetProperty("paramsVerdict").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "run-rejected");
    }

    [Fact]
    public void Yellow_GenerationStep_StillRunRejected()
    {
        // 松绑不放开全图级修改：无 handle 的 add-line 回落既有 run-rejected（R5）。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-3","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}""");

        var (code, stdout, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "run-rejected");
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void Yellow_MoveStep_AllPass_ShiftsBbox_ExecutesAndDelivers()
    {
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-4","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":20}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("move:21E00:10:20", _rt.Calls);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void Yellow_DeleteStep_AllPass_HandleGone_ExecutesAndDelivers()
    {
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-5","steps":[{"kind":"delete","params":{"handle":"21E00"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("delete:21E00", _rt.Calls);
        Assert.Empty(_rt.ReadResults); // 句柄已删除（读回缺失即 Pass）
        Assert.True(File.Exists(_drawing + ".live.dwg"));
    }

    [Fact]
    public void Yellow_NumericDecimalHandle_MatchedToHexOverlay_OpSeesHex()
    {
        // R11：计划句柄 "138752"（21E00 的十进制）=候选1未命中→十进制转 hex→命中 overlay。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-6","steps":[{"kind":"change-layer","params":{"handle":"138752","layer":"WALL"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        // 运行时 op 与 Evaluate 同用命中的 overlay 形态（21E00），不得被 HandleToHex 二转。
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var pass = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "native-gate-pass");
        using (var p = JsonDocument.Parse(pass.Entry.ParamsJson))
            Assert.Equal("21E00", p.RootElement.GetProperty("handles")[0].GetString());
    }

    [Fact]
    public void Yellow_AllNumericHexHandle_MatchedAsHex_NotConvertedTo5208()
    {
        // R11 歧义防护：overlay 句柄本身是全数字 hex "21000"——原串大写精确命中即用
        // （不得按十进制解析成 5208 改错实体）。
        WriteOverlay(Wall(handle: "21000"));
        SeedNative(Wall(handle: "21000"));
        string plan = WritePlan(
            """{"planId":"NL-7","steps":[{"kind":"change-layer","params":{"handle":"21000","layer":"WALL"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("change-layer:21000:WALL", _rt.Calls);
        Assert.DoesNotContain(_rt.Calls, c => c.Contains("5208"));
    }

    [Fact]
    public void Yellow_EmptyLayer_RefusedAtAdmission_InvalidLayer()
    {
        // R12：change-layer 空/空白 layer 显式拒绝（Layers.Add("") 抛错且语义分歧）。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-8","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"   "}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("InvalidLayer", stderr);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("change-layer"));
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        Assert.Equal(new[] { "InvalidLayer" }, VerdictsOf(refused));
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
            Assert.Equal("21E00", p.RootElement.GetProperty("verdicts")[0].GetProperty("handle").GetString());
    }

    [Fact]
    public void Yellow_RereadHandleAbsent_StaleMismatch_Refused()
    {
        // 满 overlay 但 live 重读无该句柄（stale overlay）→ 参数门 StaleMismatch；R16 合并判据
        // （参数门 ∨ 炸开门）：无炸开证据 → ExplodeNotRead，dual verdict 逐字钉死。
        WriteOverlay(Wall());
        string plan = WritePlan(
            """{"planId":"NL-9","steps":[{"kind":"move","params":{"handle":"21E00","dx":1,"dy":2}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("move"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
        {
            var v = p.RootElement.GetProperty("verdicts")[0];
            Assert.Equal("21E00", v.GetProperty("handle").GetString());
            Assert.Equal("ExplodeNotRead", v.GetProperty("verdict").GetString());
            Assert.Equal("StaleMismatch", v.GetProperty("paramsVerdict").GetString());
        }
        Assert.False(File.Exists(_drawing + ".live.dwg"));
    }

    [Fact]
    public void Yellow_ReadbackMismatch_FailsNoDelivery()
    {
        // 执行成功但读回不符（fake 不落层变更）→ native-live-failed{readback}，不 SaveAs。
        WriteOverlay(Wall());
        SeedNative(Wall());
        _rt.IgnoreHandleOps = true;
        string plan = WritePlan(
            """{"planId":"NL-10","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        Assert.Null(_rt.SavedPath);
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
        Assert.Contains("readback", p.RootElement.EnumerateObject().Select(o => o.Name));
    }

    [Fact]
    public void Yellow_GenerationStepWithHandle_StillRunRejected()
    {
        // kind 白名单：生成类 add-line 即便带 handle 也不得绕过准入（回落既有 run-rejected）。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-11","steps":[{"kind":"add-line","params":{"handle":"21E00","x1":0,"y1":0,"x2":5,"y2":5}}]}""");

        var (code, stdout, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        Assert.Empty(_rt.Calls); // 准入分类即回落，未触任何运行时调用
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "run-rejected");
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    // ---- I5（复审修复波）：会话开路序=按需深读；两项场景分别钉住 ----

    /// <summary>
    /// I5(i) 全参数门放行：会话序=ReadNativeParams(path) → Open → op → null 形态读回；
    /// **不触桥**（无 read-explode）。fake 的 ReadExplodeEvidence 被置为"一调即抛"——
    /// 若生产仍无条件深读，本用例必炸（旧断言被接口默认实现掩盖的正是这一点，I5 根因）。
    /// </summary>
    [Fact]
    public void TArchSessionOrder_AllParamsPass_NoDeepReadReadNativeThenOpen()
    {
        WriteOverlay(Wall());
        SeedNative(Wall());
        _rt.ThrowOnExplodeRead = true;   // 断言"从未深读"的强判据（非仅断言未记录）
        string plan = WritePlan(
            """{"planId":"NL-12","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.True(_rt.Calls.Count > 0, "无运行时调用");
        // @T10：path 形态读参最先（EnsureAppTArch 以 /p 拉起 TArch 并缓存 _app）。
        Assert.Equal("read-native:path", _rt.Calls[0]);
        Assert.DoesNotContain("read-explode", _rt.Calls);
        int openIdx = _rt.Calls.IndexOf("open:" + Path.GetFileName(_drawing));
        Assert.True(openIdx > 0, $"calls={string.Join("|", _rt.Calls)}");
        // 执行窗口在读参之后；执行后读回为 null 形态（活文档），亦不得先于 Open。
        int opIdx = _rt.Calls.IndexOf("change-layer:21E00:WALL");
        Assert.True(opIdx > openIdx, $"calls={string.Join("|", _rt.Calls)}");
        int docReadIdx = _rt.Calls.IndexOf("read-native:doc");
        Assert.True(docReadIdx > opIdx, $"calls={string.Join("|", _rt.Calls)}");
        Assert.Contains("native-live-committed", AuditRecords().Select(r => r.Entry.Kind));
        // I1：native-gate-pass 加性披露 deepRead=false（纯参数门结论、未触桥）。
        var pass = Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-gate-pass");
        using var p = JsonDocument.Parse(pass.Entry.ParamsJson);
        Assert.False(p.RootElement.GetProperty("deepRead").GetBoolean());
        Assert.Equal(JsonValueKind.Null, p.RootElement.GetProperty("trustedPaths").ValueKind);
    }

    /// <summary>
    /// I5(ii) 参数门未放行（bbox 漂移）→ 需要合并判据 → 深读在**读参之后、Open 之前**插入
    /// （取代旧"深读最先"的无条件顺序）；move 仅凭炸开门放行后正常执行交付，deepRead=true
    /// 且 explodeGateHandles 逐字披露。
    /// </summary>
    [Fact]
    public void TArchSessionOrder_ParamsFailDeepRead_AfterParamsReadBeforeOpen()
    {
        WriteOverlay(new[] { Wall() }, new[] { ExplodeRow() });
        // 参数门重读与 overlay 漂移（bbox 不同）→ StaleMismatch → 触发深读。
        // 活会话起始事实与参数门重读一致（真实世界同源）：move 读回以参数面 before-bbox
        // 为基准，比较的是"同一实体被平移"，不得让夹具自己造出基准漂移。
        var liveBbox = new[] { 0.0, 0.0, 0.0, 200.0, 100.0, 3000.0 };
        _rt.PathReadResults.Add(Wall(bbox: liveBbox));
        _rt.ReadResults.Add(Wall(bbox: liveBbox));
        _rt.ExplodeReadResults.Add(ExplodeRow());
        string plan = WritePlan(
            """{"planId":"NL-30b","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":20}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.True(_rt.Calls.Count >= 3, $"calls={string.Join("|", _rt.Calls)}");
        Assert.Equal("read-native:path", _rt.Calls[0]);
        Assert.Equal("read-explode", _rt.Calls[1]);   // 深读紧随读参（契约=无打开文档）
        Assert.Equal("open:" + Path.GetFileName(_drawing), _rt.Calls[2]);
        var records = AuditRecords();
        var pass = Assert.Single(records, r => r.Entry.Kind == "native-gate-pass");
        using (var p = JsonDocument.Parse(pass.Entry.ParamsJson))
        {
            Assert.True(p.RootElement.GetProperty("deepRead").GetBoolean());
            // I4：深读会话披露信任位置快照（本夹具=桥目录）。
            Assert.Equal(_bridgeDir, p.RootElement.GetProperty("trustedPaths").GetString());
            Assert.Equal(new[] { "21E00" },
                p.RootElement.GetProperty("explodeGateHandles").EnumerateArray()
                    .Select(h => h.GetString()).ToArray());
        }
        Assert.Contains("native-live-committed", records.Select(r => r.Entry.Kind));
    }

    /// <summary>
    /// I1(b)：计划含 explode-native（深读需求静态确定）+ 桥不可用（覆盖缝指向不存在文件）→
    /// 起会话前 fail-fast：native-bridge-unavailable{reason:"dll-missing"} + stderr 指引 + exit 1，
    /// **运行时零调用**（不建实例/不 Open/不 NETLOAD——缺失/不受信桥绝不进 3600s 安全框）。
    /// </summary>
    [Fact]
    public void ExplodeNativePlan_BridgeUnavailable_RefusedBeforeSession_NoOps()
    {
        WriteOverlay(new[] { Wall() }, new[] { ExplodeRow() });
        SeedNative(Wall());
        AcadRuntime.BridgeDllOverride =
            Path.Combine(_project, "missing", AcadRuntime.BridgeDllName);   // 不存在的桥
        string plan = WritePlan(
            """{"planId":"NL-31","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("桥不可用", stderr);
        Assert.Empty(_rt.Calls);                       // 起会话前拒绝：零运行时副作用
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-bridge-unavailable");
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
        {
            Assert.Equal("dll-missing", p.RootElement.GetProperty("reason").GetString());
            Assert.Contains("CADAGENT_BRIDGE_DLL", p.RootElement.GetProperty("guidance").GetString());
            Assert.EndsWith(AcadRuntime.BridgeDllName, p.RootElement.GetProperty("dllPath").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-gate-pass");
    }

    /// <summary>I2：桥文件存在但是空桩产物 → native-bridge-unavailable{reason:"stub"}（不 NETLOAD）。</summary>
    [Fact]
    public void ExplodeNativePlan_StubBridge_RefusedWithStubReason()
    {
        WriteOverlay(new[] { Wall() }, new[] { ExplodeRow() });
        SeedNative(Wall());
        string stub = Path.Combine(_bridgeDir, "stub-" + AcadRuntime.BridgeDllName);
        File.WriteAllBytes(stub, Encoding.ASCII.GetBytes("NoAcadRefsStub"));
        AcadRuntime.BridgeDllOverride = stub;
        string plan = WritePlan(
            """{"planId":"NL-32","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("空桩", stderr);
        Assert.Empty(_rt.Calls);
        var refused = Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-bridge-unavailable");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        Assert.Equal("stub", p.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>I1：桥可用但目录不受信（TRUSTEDPATHS 不含）→ native-bridge-unavailable{untrusted}。</summary>
    [Fact]
    public void ExplodeNativePlan_UntrustedBridgeDir_RefusedWithUntrustedReason()
    {
        WriteOverlay(new[] { Wall() }, new[] { ExplodeRow() });
        SeedNative(Wall());
        AcadRuntime.TrustedPathsSource = _ => @"C:\not-trusted";
        string plan = WritePlan(
            """{"planId":"NL-33","steps":[{"kind":"explode-native","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("TRUSTEDPATHS", stderr);
        Assert.Empty(_rt.Calls);
        var refused = Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-bridge-unavailable");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        Assert.Equal("untrusted", p.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Yellow_TwoMovesSameHandle_ReadbackAggregated_Committed()
    {
        // 同句柄多步：读回按终态聚合（位移求和 +15x），不得按首快照逐 step 期望误拒。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-13","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":0}},{"kind":"move","params":{"handle":"21E00","dx":5,"dy":0}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("move:21E00:10:0", _rt.Calls);
        Assert.Contains("move:21E00:5:0", _rt.Calls);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        Assert.Equal(2, records.Count(r => r.Entry.Kind == "native-live-step"));
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void MalformedHandle_RefusedWithAudit_NoRuntime()
    {
        // 句柄形态非法（bool 等非字符串/非数值）→ 准入分类异常须收口为审计拒绝，不得未捕获。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-14","steps":[{"kind":"delete","params":{"handle":true}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("句柄参数形态非法", stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        Assert.Equal("MalformedHandle", p.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
    }

    // ---- 复审 finding 1：产品计划 kind 词汇（move-entity/delete-entity）到达运行时 op ----

    [Fact]
    public void Yellow_ProductKindMoveEntity_NormalizedToMoveOp_Committed()
    {
        // 生成侧计划 kind=move-entity（HandlerRegistry 词汇）→ 准入全收 + Replay 克隆归一为
        // move，运行时 op 前缀 "move:" 可见，读回聚合按运行时语义（bbox 位移和）。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-15","steps":[{"kind":"move-entity","params":{"handle":"21E00","dx":10,"dy":20}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("move:21E00:10:20", _rt.Calls);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
        // 审计按运行时口径（归一后 kind），不把产品词汇原样带进执行证据。
        var step = Assert.Single(records, r => r.Entry.Kind == "native-live-step");
        using (var p = JsonDocument.Parse(step.Entry.ParamsJson))
            Assert.Equal("move", p.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Yellow_ProductKindDeleteEntity_NormalizedToDeleteOp_Committed()
    {
        // 生成侧计划 kind=delete-entity → 归一 delete；句柄消失读回通过并交付。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-16","steps":[{"kind":"delete-entity","params":{"handle":"21E00"}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("delete:21E00", _rt.Calls);
        Assert.Empty(_rt.ReadResults);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-live-committed");
    }

    // ---- 复审 finding 5：拒绝面（overlay 缺失/损坏、ReadIncomplete、SHA 不可验证）----

    [Fact]
    public void OverlayMissing_RefusedWithReason_Exit1_NoRuntime()
    {
        // 缺 overlay 不得静默降级为"无 overlay 放行"：审计 native-gate-refused{reason}。
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-17","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("语义叠加层", stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        Assert.Equal("overlay-missing", p.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void OverlayCorrupt_RefusedWithReason_Exit1_NoRuntime()
    {
        // overlay 损坏（非法 JSON）→ refused{overlay-load-failed}，不执行不交付。
        Directory.CreateDirectory(Path.Combine(_project, ".cadagent"));
        File.WriteAllText(OverlayPath, "{ this is not json");
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-18","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("语义叠加层损坏", stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var refused = Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-gate-refused");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        Assert.Equal("overlay-load-failed", p.RootElement.GetProperty("reason").GetString());
        Assert.True(p.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void OverlayReadIncomplete_RefusedVerdict_NoHandleOps()
    {
        // overlay 行 ReadOk=false（读参不完整）→ 参数门 ReadIncomplete；R16 合并判据无炸开
        // 证据 → ExplodeNotRead（炸开门拒因优先），dual verdict 逐字钉死。
        WriteOverlay(Wall() with { ReadOk = false });
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-19","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("change-layer"));
        Assert.DoesNotContain(_rt.Calls, c => c == "saveas");
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
        {
            var v = p.RootElement.GetProperty("verdicts")[0];
            Assert.Equal("21E00", v.GetProperty("handle").GetString());
            Assert.Equal("ExplodeNotRead", v.GetProperty("verdict").GetString());
            Assert.Equal("ReadIncomplete", v.GetProperty("paramsVerdict").GetString());
        }
    }

    [Fact]
    public void SourceShaSeamEmpty_FailsFailClosed_Exit1()
    {
        // finding 2d/5c：哈希缝注入空串（源件不可哈希）→ fail-closed，绝不 ""=="" 假通过；
        // 失败先于任何运行时副作用（不起会话、无 ops）。
        CommandRouter.Sha256File = _ => "";
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-20","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("源图 SHA 不可验证", stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var failed = Assert.Single(AuditRecords(), r => r.Entry.Kind == "native-live-failed");
        using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
        Assert.Equal("sourcesha-unverifiable", p.RootElement.GetProperty("reason").GetString());
    }

    // ---- 复审 finding 2：零写入证据窗口闭合（恶意/缺陷 SaveAs 与 liveSha 不可验证）----

    [Fact]
    public void SaveAsWritesSourcePath_ZeroWriteBreachCaughtAfterDispose_Exit1()
    {
        // 恶意/缺陷运行时在 SaveAs 中回写源图 → 证据哈希（SaveAs/Close/Dispose 之后复核）
        // 必须捕获字节漂移并按失败退出，绝不把被改写的源图当成功交付依据。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-21","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");
        byte[] before = File.ReadAllBytes(_drawing);
        _rt.SabotageSourcePath = _drawing; // SaveAs 时向源件写入破坏字节

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("原图零写入契约破坏", stderr);
        Assert.NotEqual(before, File.ReadAllBytes(_drawing)); // 破坏确实发生（证明检测有效）
        // 已执行到交付（SaveAs+Close）之后才复核：失败发生在证据窗口闭合处，非前置拒绝。
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        Assert.Contains("saveas", _rt.Calls);
        Assert.Equal("close", _rt.Calls[^1]);
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
        Assert.Contains("源图字节被改动", p.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void LiveShaUnverifiable_FailsExit1_KeepsArtifactNoCommitted()
    {
        // finding 2b：交付件 SHA 不可验证 → 按失败退出（保留 .live.dwg 作证据，但不报成功）。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-22","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");
        var real = _origSha256;
        CommandRouter.Sha256File = path => path.EndsWith(".live.dwg", StringComparison.Ordinal) ? "" : real(path);

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("交付件", stderr);
        Assert.True(File.Exists(_drawing + ".live.dwg")); // 证据保留
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using var p = JsonDocument.Parse(failed.Entry.ParamsJson);
        Assert.Equal("liveSha-unverifiable", p.RootElement.GetProperty("reason").GetString());
        Assert.Equal(_drawing + ".live.dwg", p.RootElement.GetProperty("outPath").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    // ---- ISTQB 合并前审查修复波：准入拒绝面补全（I1）/零写入复核时序（I3）/聚合组合（Minor 6）----

    [Fact]
    public void Yellow_HandleNotInOverlay_NotRead_Refused_NoOps()
    {
        // I1a：overlay 只含**另一**句柄（目标实体未读参）→ NotRead 拒绝；运行时从不触达。
        WriteOverlay(Wall(handle: "21F00"));
        SeedNative(Wall(handle: "21F00"));
        string plan = WritePlan(
            """{"planId":"NL-24","steps":[{"kind":"delete","params":{"handle":"21E00"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("NotRead", stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        Assert.Equal(new[] { "NotRead" }, VerdictsOf(refused));
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
            Assert.Equal("21E00", p.RootElement.GetProperty("verdicts")[0].GetProperty("handle").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
    }

    [Fact]
    public void HandleOpFails_NativeLiveFailedStep_NoSaveAs_NoCommitted()
    {
        // I1b：句柄 op 抛 CadRuntimeException("op-fail") → native-live-failed{step,error}，
        // 不 SaveAs、不交付、committed 不落。
        WriteOverlay(Wall());
        SeedNative(Wall());
        _rt.FailOps = true;
        string plan = WritePlan(
            """{"planId":"NL-25","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("op-fail", stderr);
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        Assert.DoesNotContain("saveas", _rt.Calls);
        Assert.Null(_rt.SavedPath);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
        {
            Assert.Equal("change-layer", p.RootElement.GetProperty("step").GetString());
            Assert.Equal("21E00", p.RootElement.GetProperty("handle").GetString());
            Assert.Contains("op-fail", p.RootElement.GetProperty("error").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void NoAvailableRuntime_NativeLiveFailed_Exit1_NoDelivery()
    {
        // I1c：RuntimeFactory=null 返回（无可用厂商运行时）→ 响亮失败，无会话副作用。
        WriteOverlay(Wall());
        SeedNative(Wall());
        CommandRouter.RuntimeFactory = () => null;
        string plan = WritePlan(
            """{"planId":"NL-26","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("无可用厂商运行时", stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
            Assert.Contains("无可用厂商运行时", p.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void WhitelistedKindWithoutHandle_FallsBackToRunRejected_NoNativeAudit()
    {
        // I1d：白名单 kind（delete）但无 handle 参数 → 不属句柄寻址准入 → 回落既有
        // run-rejected（exit 1、run-rejected 审计），无任何 native-* 审计、无运行时调用。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-27","steps":[{"kind":"delete","params":{}}]}""");

        var (code, stdout, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("只读审查", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "run-rejected");
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-"));
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
    }

    [Fact]
    public void DisposeWritesSourcePath_ZeroWriteRecheckAfterDispose_Exit1()
    {
        // I3：缺陷运行时在 Dispose（SaveAs/Close 之后）回写源图——零写入复核必须在 Dispose
        // 之后重算才能捕获；失败退出、无 committed、.live.dwg 保留口径不变。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-28","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");
        byte[] before = File.ReadAllBytes(_drawing);
        _rt.SabotageOnDispose = true; // Dispose 时向 Open 打开的源图回写破坏字节

        var (code, stdout, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.Contains("原图零写入契约破坏", stderr);
        Assert.DoesNotContain("\"ok\"", stdout);
        Assert.NotEqual(before, File.ReadAllBytes(_drawing)); // 破坏确实发生（证明复核有效）
        // 已执行到交付（SaveAs+Close）之后才复核：破坏在 Dispose 阶段，前置窗口抓不到。
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        Assert.Contains("saveas", _rt.Calls);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var failed = Assert.Single(records, r => r.Entry.Kind == "native-live-failed");
        using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
        {
            Assert.Equal("sourceSha-changed", p.RootElement.GetProperty("reason").GetString());
            Assert.Equal(ShaOf(before), p.RootElement.GetProperty("sourceSha").GetString());
            Assert.Contains("源图字节被改动", p.RootElement.GetProperty("error").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void Yellow_MoveWithBboxNullAfter_ReadbackDegrades_Committed()
    {
        // Minor 6a：move 后读回 bbox=null 且 params 不变 → 诚实降级读回通过并交付；
        // native-live-step 审计如实披露 readback="reread-bbox-null"。
        WriteOverlay(Wall());
        SeedNative(Wall());
        _rt.MoveNullsBbox = true;
        string plan = WritePlan(
            """{"planId":"NL-29","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":20}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("move:21E00:10:20", _rt.Calls);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var step = Assert.Single(records, r => r.Entry.Kind == "native-live-step");
        using (var p = JsonDocument.Parse(step.Entry.ParamsJson))
        {
            Assert.Equal("move", p.RootElement.GetProperty("kind").GetString());
            Assert.Equal("reread-bbox-null", p.RootElement.GetProperty("readback").GetString());
        }
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
    }

    [Fact]
    public void Yellow_ChangeLayerThenMoveSameHandle_Aggregated_Committed()
    {
        // Minor 6b：同句柄 change-layer→move 组合，读回按终态聚合（layer=末步+bbox 位移）一次通过。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-30","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"LAYER-A"}},{"kind":"move","params":{"handle":"21E00","dx":10,"dy":0}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(0, code);
        Assert.Contains("change-layer:21E00:LAYER-A", _rt.Calls);
        Assert.Contains("move:21E00:10:0", _rt.Calls);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        Assert.Equal(2, records.Count(r => r.Entry.Kind == "native-live-step"));
        Assert.Single(records, r => r.Entry.Kind == "native-live-committed");
    }

    /// <summary>
    /// Fake 厂商运行时（零 COM）：ReadNativeParams 按形态区分——path≠null=天正自开自关
    /// 副本的只读快照（准入重读事实，独立于执行窗口状态）；null=当前活文档（ops 落状态，
    /// 读回看到执行后事实）。调用串记录形态（read-native:path/doc）供顺序断言；
    /// handle op 记录调用串；SaveAs 落合成 .live.dwg 字节；SabotageSourcePath 置位时
    /// SaveAs 还向该路径回写破坏字节（finding 2c 恶意/缺陷运行时回归）；
    /// FailOps=True 时 handle op 抛 CadRuntimeException("op-fail")（I1b 执行失败面）；
    /// SabotageOnDispose=True 时 Dispose 向 Open 打开的源图路径回写破坏字节（I3：证明零写入
    /// 复核发生在 Dispose 之后——SaveAs/Close 之后才写，前置窗口抓不到）；MoveNullsBbox=True
    /// 时 move 落位移但把读回 bbox 置 null（Minor 6a bbox 不可得降级面）。
    /// </summary>
    private sealed class FakeNativeRuntime : ICadRuntime
    {
        public List<string> Calls { get; } = new();
        public List<NativeEntityParam> PathReadResults { get; } = new();
        public List<NativeEntityParam> ReadResults { get; } = new();
        public List<ExplodeEvidence> ExplodeReadResults { get; } = new();
        public string? SavedPath { get; private set; }
        public bool IgnoreHandleOps { get; set; }
        public bool FailOps { get; set; }
        public string? SabotageSourcePath { get; set; }
        public bool SabotageOnDispose { get; set; }
        public bool MoveNullsBbox { get; set; }
        /// <summary>I5：ReadExplodeEvidence 被调到即抛——"从未深读"的强判据（非仅断言未记录）。</summary>
        public bool ThrowOnExplodeRead { get; set; }
        private string? _openedPath;

        // I5：接口默认实现会静默返回空表，掩盖"生产是否真的深读"——fake 必须如实记录该调用。
        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath, bool includeBlockDefs = false)
        {
            Calls.Add("read-explode");
            if (ThrowOnExplodeRead)
                throw new CadRuntimeException("不应深读（本用例计划全参数门放行）");
            return ExplodeReadResults.ToList();
        }

        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string path)
        {
            _openedPath = path; // Dispose 破坏缝写入的"源图"= 本会话打开的图纸
            Calls.Add("open:" + Path.GetFileName(path));
        }

        public void SaveAs(string path)
        {
            SavedPath = path;
            File.WriteAllBytes(path, new byte[] { 0x4C, 0x49, 0x56, 0x45 }); // 让 liveSha 可计算
            if (SabotageSourcePath != null)
                File.WriteAllBytes(SabotageSourcePath, new byte[] { 0x42, 0x41, 0x44 }); // 源件零写入破坏
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

        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null)
        {
            Calls.Add(drawingPath == null ? "read-native:doc" : "read-native:path");
            return drawingPath == null ? ReadResults.ToList() : PathReadResults.ToList();
        }

        public void ChangeLayerByHandle(string hexHandle, string layer)
        {
            Calls.Add($"change-layer:{hexHandle}:{layer}");
            if (FailOps) throw new CadRuntimeException("op-fail");
            if (IgnoreHandleOps) return;
            int i = ReadResults.FindIndex(n => n.Handle == hexHandle);
            if (i >= 0) ReadResults[i] = ReadResults[i] with { Layer = layer };
        }

        public void MoveByHandle(string hexHandle, double dx, double dy)
        {
            Calls.Add($"move:{hexHandle}:{dx}:{dy}");
            if (FailOps) throw new CadRuntimeException("op-fail");
            if (IgnoreHandleOps) return;
            int i = ReadResults.FindIndex(n => n.Handle == hexHandle);
            if (i < 0) return;
            ReadResults[i] = MoveNullsBbox
                ? ReadResults[i] with { Bbox = null }  // 位移后读回 bbox 不可得（params 不变）
                : ReadResults[i].Bbox is { Length: 6 } b
                    ? ReadResults[i] with
                    {
                        Bbox = new[] { b[0] + dx, b[1] + dy, b[2], b[3] + dx, b[4] + dy, b[5] }
                    }
                    : ReadResults[i];
        }

        public void DeleteByHandle(string hexHandle)
        {
            Calls.Add($"delete:{hexHandle}");
            if (FailOps) throw new CadRuntimeException("op-fail");
            if (IgnoreHandleOps) return;
            ReadResults.RemoveAll(n => n.Handle == hexHandle);
        }

        public void Dispose()
        {
            // I3：模拟缺陷运行时在 Dispose（Close+Quit 之后）回写源图——零写入复核若早于
            // Dispose 或只在成功会话的最后读一次（而非 Dispose 后）都抓不到此漂移。
            if (SabotageOnDispose && _openedPath != null)
                File.WriteAllBytes(_openedPath, new byte[] { 0x44, 0x49, 0x53 });
        }
    }
}
