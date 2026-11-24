using System.Security.Cryptography;
using System.Text.Json;
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

    public NativeLiveAdmissionTests()
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

    private void WriteOverlay(params NativeEntityParam[] records)
    {
        var overlay = new SemanticOverlay();
        foreach (var r in records) overlay.UpsertNative(r);
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

    [Fact]
    public void Yellow_HandleStep_AllPass_ExecutesOps_DeliversLiveDwg()
    {
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-1","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
        Assert.Contains("change-layer:21E00:WALL", _rt.Calls);
        Assert.Equal(_drawing + ".live.dwg", _rt.SavedPath);
        Assert.True(File.Exists(_drawing + ".live.dwg"));
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 源件字节零写入
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
        // 重读 bbox 与 overlay 不符（实体已漂移）→ StaleMismatch 拒绝。
        SeedNative(Wall(bbox: new[] { 0.0, 0.0, 0.0, 200.0, 100.0, 3000.0 }));
        string plan = WritePlan(
            """{"planId":"NL-2","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("change-layer"));
        Assert.DoesNotContain(_rt.Calls, c => c == "saveas");
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        Assert.Contains("StaleMismatch", VerdictsOf(refused));
        using (var p = JsonDocument.Parse(refused.Entry.ParamsJson))
            Assert.Equal("21E00", p.RootElement.GetProperty("verdicts")[0].GetProperty("handle").GetString());
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

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
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

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
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

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
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

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
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
        Assert.Contains("layer", stderr);
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
        // 满 overlay 但 live 重读无该句柄（stale overlay）→ StaleMismatch 拒绝。
        WriteOverlay(Wall());
        string plan = WritePlan(
            """{"planId":"NL-9","steps":[{"kind":"move","params":{"handle":"21E00","dx":1,"dy":2}}]}""");

        var (code, _, _) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.Equal(1, code);
        Assert.DoesNotContain(_rt.Calls, c => c.StartsWith("move"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        Assert.Equal(new[] { "StaleMismatch" }, VerdictsOf(refused));
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

    [Fact]
    public void TArchSessionOrder_AdmissionPathRead_ThenOpen()
    {
        // @T10 会话开路顺序：ReadNativeParams(path≠null) 才经 EnsureAppTArch 以 /p 拉起
        // 天正 profile 并缓存 _app；若先 Open（EnsureApp 默认 profile 先缓存），TArch 永不
        // 加载→真机 TDb 目标全 StaleMismatch。首个运行时调用必须是 path 形态读参。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-12","steps":[{"kind":"change-layer","params":{"handle":"21E00","layer":"WALL"}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
        Assert.True(_rt.Calls.Count > 0, "无运行时调用");
        Assert.Equal("read-native:path", _rt.Calls[0]);
        int openIdx = _rt.Calls.IndexOf("open:" + Path.GetFileName(_drawing));
        Assert.True(openIdx > 0, $"calls={string.Join("|", _rt.Calls)}");
        // 执行窗口在读参之后；执行后读回为 null 形态（活文档），亦不得先于 Open。
        int opIdx = _rt.Calls.IndexOf("change-layer:21E00:WALL");
        Assert.True(opIdx > openIdx, $"calls={string.Join("|", _rt.Calls)}");
        int docReadIdx = _rt.Calls.IndexOf("read-native:doc");
        Assert.True(docReadIdx > opIdx, $"calls={string.Join("|", _rt.Calls)}");
        Assert.Contains("native-live-committed", AuditRecords().Select(r => r.Entry.Kind));
    }

    [Fact]
    public void Yellow_TwoMovesSameHandle_ReadbackAggregated_Committed()
    {
        // 同句柄多步：读回按终态聚合（位移求和 +15x），不得按首快照逐 step 期望误拒。
        WriteOverlay(Wall());
        SeedNative(Wall());
        string plan = WritePlan(
            """{"planId":"NL-13","steps":[{"kind":"move","params":{"handle":"21E00","dx":10,"dy":0}},{"kind":"move","params":{"handle":"21E00","dx":5,"dy":0}}]}""");

        var (code, _, stderr) = Run("run", plan, "--drawing", _drawing, "--live");

        Assert.True(code == 0, $"code={code} stderr={stderr}");
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
        Assert.NotEmpty(stderr);
        Assert.Empty(_rt.Calls);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        var records = AuditRecords();
        var refused = Assert.Single(records, r => r.Entry.Kind == "native-gate-refused");
        using var p = JsonDocument.Parse(refused.Entry.ParamsJson);
        Assert.Equal("MalformedHandle", p.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind.StartsWith("native-live"));
    }

    /// <summary>
    /// Fake 厂商运行时（零 COM）：ReadNativeParams 按形态区分——path≠null=天正自开自关
    /// 副本的只读快照（准入重读事实，独立于执行窗口状态）；null=当前活文档（ops 落状态，
    /// 读回看到执行后事实）。调用串记录形态（read-native:path/doc）供顺序断言；
    /// handle op 记录调用串；SaveAs 落合成 .live.dwg 字节。
    /// </summary>
    private sealed class FakeNativeRuntime : ICadRuntime
    {
        public List<string> Calls { get; } = new();
        public List<NativeEntityParam> PathReadResults { get; } = new();
        public List<NativeEntityParam> ReadResults { get; } = new();
        public string? SavedPath { get; private set; }
        public bool IgnoreHandleOps { get; set; }

        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string path) => Calls.Add("open:" + Path.GetFileName(path));

        public void SaveAs(string path)
        {
            SavedPath = path;
            File.WriteAllBytes(path, new byte[] { 0x4C, 0x49, 0x56, 0x45 }); // 让 liveSha 可计算
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
            if (IgnoreHandleOps) return;
            int i = ReadResults.FindIndex(n => n.Handle == hexHandle);
            if (i >= 0) ReadResults[i] = ReadResults[i] with { Layer = layer };
        }

        public void MoveByHandle(string hexHandle, double dx, double dy)
        {
            Calls.Add($"move:{hexHandle}:{dx}:{dy}");
            if (IgnoreHandleOps) return;
            int i = ReadResults.FindIndex(n => n.Handle == hexHandle);
            if (i >= 0 && ReadResults[i].Bbox is { Length: 6 } b)
                ReadResults[i] = ReadResults[i] with
                {
                    Bbox = new[] { b[0] + dx, b[1] + dy, b[2], b[3] + dx, b[4] + dy, b[5] }
                };
        }

        public void DeleteByHandle(string hexHandle)
        {
            Calls.Add($"delete:{hexHandle}");
            if (IgnoreHandleOps) return;
            ReadResults.RemoveAll(n => n.Handle == hexHandle);
        }

        public void Dispose() { }
    }
}
