using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia.Headless.XUnit;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 视觉接线 T3：--vision-scan headless 入口编排（fake ILlmClient/profile 注入 seam，不触网）。
/// 夹具镜像 VisionCrossCheckerTests（图框块+设备属性 Insert A=深照型灯）；census 经 sidecar
/// 注入（普通 DXF + .cadagent/unparsed.json 源哈希匹配）→ Yellow（有原生+未解析，只读审查继续）；
/// 空文档+census → Red（拒绝留痕，零渲染零 llm）。
/// 闸口/出境/审计契约：Red→run-rejected+exit 1；NeedsFirstCloudConsent 无 --confirm-egress→
/// egress-blocked+exit 1（不静默发送）；有→ConfirmCloudConsent+egress-consent 留痕；
/// llm-call 每 tile（镜像 AgentOrchestrator.AuditLlmCall payload：model/usage/attempt）；
/// 无 seeded 不写 overlay（防空覆盖）。
/// R3：测试内 --confirm-egress=行使显式确认通道（用户先前授权云端测试）；生产须人工 flag（T4 协议注记）。
/// </summary>
public class VisionScanModeTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"visionscan-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public VisionScanModeTests()
    {
        Directory.CreateDirectory(_project);
        // 单幅面视觉夹具：图框 (0,0)-(1000,700) + 图号属性 + 幅面名文字 + 设备 Insert（A=深照型灯）。
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            var frame = new BlockRecord("院标准图框_A0");
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
            doc.BlockRecords.Add(frame);
            var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
            frameIns.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
            doc.Entities.Add(frameIns);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(500, 650, 0), Value = "一层电力平面图", Height = 30, Layer = new Layer("图框") });
            var e = new BlockRecord("E");
            e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
            doc.BlockRecords.Add(e);
            var layer = new Layer("EQUIP-照明");
            doc.Layers.Add(layer);
            var ins = new Insert(e) { InsertPoint = new XYZ(100, 100, 0), Layer = layer };
            ins.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
            doc.Entities.Add(ins);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    /// <summary>sidecar 注入 census（TCH_WALL×2，源哈希匹配）→ Yellow（有原生+未解析）。</summary>
    private void InjectCensus(string? drawing = null) =>
        UnparsedCensusStore.Save(_project, drawing ?? _drawing,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));

    private static ProviderProfile FakeProfile() =>
        new("fake-vl", "https://api.test/v1", "fake-vl", VisionCapable: true, DataPolicy.CloudAllowed, new ProfileLimits());

    private (int code, string stdout, string stderr) RunWith(ILlmClient client, params string[] extraArgs)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        var args = new List<string> { "--vision-scan", "--drawing", _drawing, "--project", _project };
        args.AddRange(extraArgs);
        int code = VisionScanMode.Run(args.ToArray(), outW, errW, client, FakeProfile());
        return (code, outW.ToString(), errW.ToString());
    }

    private List<AuditRecord> AuditRecords()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    /// <summary>fake VL 客户端：同步完成（Task.FromResult，仓库既有 fake 口径）；计次供"零 llm"断言。</summary>
    private sealed class FakeVisionClient : ILlmClient
    {
        private readonly string _content;
        public int Calls;
        public FakeVisionClient(string content) => _content = content;
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(_content, new TokenUsage(100, 50), "fake-vl"));
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "fake-vl" });
    }

    private const string MatchingClaims = """{"claims":[{"text":"深照型灯","region":"tile-1","confidence":0.8}]}""";

    [AvaloniaFact]
    public void VisionScan_YellowSpecimen_FakeClient_SeedsAndAudits()
    {
        InjectCensus();
        var client = new FakeVisionClient(MatchingClaims);

        var (code, stdout, stderr) = RunWith(client, "--confirm-egress");

        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(stdout))
        {
            var r = doc.RootElement;
            Assert.True(r.GetProperty("ok").GetBoolean());
            Assert.Equal("Yellow", r.GetProperty("level").GetString()); // Yellow 只读审查继续（不拒）
            int tiles = r.GetProperty("tiles").GetInt32();
            Assert.True(tiles >= 1);
            Assert.True(r.GetProperty("claims").GetInt32() >= 1);
            Assert.True(r.GetProperty("seeded").GetInt32() >= 1);
            // usage 合计 = tiles × (100,50)
            Assert.Equal(tiles * 100, r.GetProperty("usage").GetProperty("promptTokens").GetInt32());
            Assert.Equal(tiles * 50, r.GetProperty("usage").GetProperty("completionTokens").GetInt32());
            Assert.Equal(tiles, client.Calls); // 每 tile 一次读图
        }
        // overlay semantic.json 含 LlmInference 标注（视觉声称经矢量佐证种子）
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Contains(overlay.Annotations, a => a.Provenance == Provenance.LlmInference && a.Class == SemanticClass.Lighting);
        // 审计：llm-call/egress-consent/vision-scan + 链完整
        var records = AuditRecords();
        Assert.Contains(records, x => x.Entry.Kind == "llm-call" && x.Entry.Source == "llm");
        Assert.Contains(records, x => x.Entry.Kind == "egress-consent");
        Assert.Contains(records, x => x.Entry.Kind == "vision-scan");
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "cloud-consent.json")));
        // llm-call payload 镜像 AuditLlmCall：model/usage/attempt（无图纸内容/密钥）
        var llm = records.First(x => x.Entry.Kind == "llm-call");
        using (var p = JsonDocument.Parse(llm.Entry.ParamsJson))
        {
            Assert.Equal("fake-vl", p.RootElement.GetProperty("model").GetString());
            Assert.Equal(100, p.RootElement.GetProperty("PromptTokens").GetInt32());
            Assert.Equal(50, p.RootElement.GetProperty("CompletionTokens").GetInt32());
            Assert.True(p.RootElement.GetProperty("attempt").GetInt32() >= 1);
        }
        Assert.DoesNotContain("api.test", stdout); // stdout 不含 endpoint/密钥面
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [AvaloniaFact]
    public void VisionScan_NoConsentFlag_RefusesEgress()
    {
        // Green 标本 + CloudAllowed 云端 profile + 无 --confirm-egress → NeedsFirstCloudConsent 拒绝
        var client = new FakeVisionClient(MatchingClaims);

        var (code, stdout, stderr) = RunWith(client); // 无 --confirm-egress

        Assert.Equal(1, code);
        Assert.Contains("--confirm-egress", stderr); // 指引明确确认通道
        Assert.Equal(0, client.Calls); // 零 llm（不静默发送）
        var records = AuditRecords();
        Assert.Contains(records, x => x.Entry.Kind == "egress-blocked");
        Assert.DoesNotContain(records, x => x.Entry.Kind == "llm-call");
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "cloud-consent.json")));
    }

    [AvaloniaFact]
    public void VisionScan_RedSpecimen_Refused()
    {
        // proxy-only Red：空文档（零原生实体）+ census sidecar → Red 拒绝（零渲染零 llm）
        string redDrawing = DxfFixture.CreateTempDxf(_ => { });
        try
        {
            InjectCensus(redDrawing);
            var client = new FakeVisionClient(MatchingClaims);
            var outW = new StringWriter(); var errW = new StringWriter();
            int code = VisionScanMode.Run(
                new[] { "--vision-scan", "--drawing", redDrawing, "--project", _project, "--confirm-egress" },
                outW, errW, client, FakeProfile());

            Assert.Equal(1, code);
            Assert.Contains("图形导出", errW.ToString()); // T3Guidance
            Assert.Equal(0, client.Calls);
            var records = AuditRecords();
            var rejected = Assert.Single(records, r => r.Entry.Kind == "run-rejected");
            using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
                Assert.Equal("Red", p.RootElement.GetProperty("level").GetString());
            Assert.DoesNotContain(records, r => r.Entry.Kind == "llm-call");
            Assert.DoesNotContain(records, r => r.Entry.Kind == "vision-scan");
            Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        }
        finally { File.Delete(redDrawing); }
    }

    [AvaloniaFact]
    public void VisionScan_NoSeeded_DoesNotWriteOverlay()
    {
        // 无矢量佐证 claim → seeded=0 → 既有 overlay 逐字不动（防空覆盖，Version 不++）
        string overlayPath = Path.Combine(_project, ".cadagent", "semantic.json");
        var pre = new SemanticOverlay();
        pre.UpsertAnnotation(new SemanticAnnotation("H1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工确认"));
        pre.Save(overlayPath);
        byte[] before = File.ReadAllBytes(overlayPath);
        var client = new FakeVisionClient("""{"claims":[{"text":"碳素波纹管","region":"说明区","confidence":0.9}]}""");

        var (code, stdout, _) = RunWith(client, "--confirm-egress");

        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(stdout))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("seeded").GetInt32());
            Assert.True(doc.RootElement.GetProperty("ambiguous").GetArrayLength() >= 1); // 歧义不静默
        }
        Assert.Equal(before, File.ReadAllBytes(overlayPath));
    }

    [AvaloniaFact]
    public void VisionScan_LocalOnlyProject_Refused()
    {
        // local-only.txt 硬约束（.cadagent 内，与 agent/llm 命令同口径）→ BlockedLocalOnly 拒绝
        Directory.CreateDirectory(Path.Combine(_project, ".cadagent"));
        File.WriteAllText(Path.Combine(_project, ".cadagent", "local-only.txt"), "");
        var client = new FakeVisionClient(MatchingClaims);

        var (code, _, stderr) = RunWith(client, "--confirm-egress");

        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Equal(0, client.Calls);
        Assert.Contains(AuditRecords(), x => x.Entry.Kind == "egress-blocked");
    }

    [AvaloniaFact]
    public void VisionScan_NoProfile_GuidanceExit1()
    {
        // 生产解析路径：无 override + 无 llm-profiles.json → stderr 指引 exit 1（零 llm）
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = VisionScanMode.Run(
            new[] { "--vision-scan", "--drawing", _drawing, "--project", _project }, outW, errW);
        Assert.Equal(1, code);
        Assert.Contains("profile", errW.ToString());
        Assert.DoesNotContain(AuditRecords(), x => x.Entry.Kind == "llm-call");
    }

    [Fact]
    public void VisionScan_MissingDrawing_UsageExit1()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = VisionScanMode.Run(new[] { "--vision-scan" }, outW, errW);
        Assert.Equal(1, code);
        Assert.Contains("--drawing", errW.ToString());
    }

    // ===== T4：--max-tiles 瓦片预算（真实云端成本控制；截断必须披露，不静默）=====

    /// <summary>多瓦夹具：图框 (0,0)-(1000,700) + 小字高文字（Height=2 → regionW≈233/regionH≈146
    /// 远小于幅面 → overview+6 tiles=7 瓦）+ 设备 Insert（A=深照型灯，GT 佐证面）。</summary>
    private static string CreateMultiTileDrawing() => DxfFixture.CreateTempDxf(doc =>
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        frameIns.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
        doc.Entities.Add(frameIns);
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(500, 650, 0), Value = "一层电力平面图", Height = 2, Layer = new Layer("图框") });
        var e = new BlockRecord("E");
        e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
        doc.BlockRecords.Add(e);
        var layer = new Layer("EQUIP-照明");
        doc.Layers.Add(layer);
        var ins = new Insert(e) { InsertPoint = new XYZ(100, 100, 0), Layer = layer };
        ins.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
        doc.Entities.Add(ins);
    });

    [AvaloniaFact]
    public void VisionScan_MaxTiles_CapsReads_DisclosesTruncation()
    {
        string drawing = CreateMultiTileDrawing();
        try
        {
            InjectCensus(drawing);
            var client = new FakeVisionClient(MatchingClaims);
            var outW = new StringWriter(); var errW = new StringWriter();
            int code = VisionScanMode.Run(
                new[] { "--vision-scan", "--drawing", drawing, "--project", _project, "--confirm-egress", "--max-tiles", "2" },
                outW, errW, client, FakeProfile());

            Assert.Equal(0, code);
            Assert.Equal(2, client.Calls); // 预算闸：仅读前 2 瓦（云端成本上限）
            using (var doc = JsonDocument.Parse(outW.ToString()))
            {
                var r = doc.RootElement;
                Assert.Equal(2, r.GetProperty("tiles").GetInt32());
                Assert.Equal(7, r.GetProperty("tilesTotal").GetInt32()); // 截断披露：预算前总瓦数
                Assert.Equal(2, r.GetProperty("maxTiles").GetInt32());
                Assert.Equal(2 * 100, r.GetProperty("usage").GetProperty("promptTokens").GetInt32());
            }
            Assert.Contains("--max-tiles", errW.ToString()); // 截断不静默（stderr 披露）
            Assert.Contains("7", errW.ToString());
            // 审计 vision-scan payload 同步披露（tilesTotal/maxTiles）
            var scan = AuditRecords().Single(r => r.Entry.Kind == "vision-scan");
            using (var p = JsonDocument.Parse(scan.Entry.ParamsJson))
            {
                Assert.Equal(2, p.RootElement.GetProperty("tiles").GetInt32());
                Assert.Equal(7, p.RootElement.GetProperty("tilesTotal").GetInt32());
                Assert.Equal(2, p.RootElement.GetProperty("maxTiles").GetInt32());
            }
            // llm-call 恰 2 条（每瓦一次，预算外零调用）
            Assert.Equal(2, AuditRecords().Count(r => r.Entry.Kind == "llm-call"));
        }
        finally { File.Delete(drawing); }
    }

    [AvaloniaTheory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("abc")]
    public void VisionScan_MaxTilesInvalid_UsageExit1_BeforeSideEffects(string value)
    {
        var client = new FakeVisionClient(MatchingClaims);
        var (code, stdout, stderr) = RunWith(client, "--confirm-egress", "--max-tiles", value);
        Assert.Equal(1, code);
        Assert.Contains("--max-tiles", stderr);
        Assert.Empty(stdout);
        Assert.Equal(0, client.Calls);
        // 用法错误先于一切副作用：不建 .cadagent/audit.db（check --skill 冲突先例）
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
    }

    [AvaloniaFact]
    public void VisionScan_NoMaxTiles_StdoutShapeUnchanged()
    {
        // 无 --max-tiles → 生产全量行为保持：stdout 不得多出预算字段（T3 契约逐字）
        InjectCensus();
        var client = new FakeVisionClient(MatchingClaims);
        var (code, stdout, _) = RunWith(client, "--confirm-egress");
        Assert.Equal(0, code);
        Assert.DoesNotContain("tilesTotal", stdout);
        Assert.DoesNotContain("maxTiles", stdout);
    }

    // ===== R11②：consent 后异常 → 终态失败审计（审计者区分崩溃与完成，§9 缺口闭合）=====

    /// <summary>恒抛 fake：模拟 consent 后网络失败（HttpRequestException 经 ReadTilesAsync 逸出）。</summary>
    private sealed class ThrowingVisionClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            throw new HttpRequestException("模拟网络失败");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "fake-vl" });
    }

    /// <summary>首瓦成功后抛：模拟读图中途 timeout（部分 llm-call 已留痕的失败形态）。</summary>
    private sealed class FailAfterFirstClient : ILlmClient
    {
        private readonly FakeVisionClient _inner = new(MatchingClaims);
        public int Calls;
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (Calls > 1) throw new TimeoutException("模拟超时");
            return _inner.ChatAsync(request, ct);
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "fake-vl" });
    }

    [AvaloniaFact]
    public void VisionScan_ClientThrows_FailureAudited_ChainGreen()
    {
        InjectCensus();
        var (code, stdout, stderr) = RunWith(new ThrowingVisionClient(), "--confirm-egress");

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("模拟网络失败", stderr);
        var records = AuditRecords();
        Assert.Contains(records, x => x.Entry.Kind == "egress-consent"); // 失败发生于 consent 后
        // 终态失败条目：kind vision-scan + status=failed（成功 payload 无 status，加性区分）
        var failed = Assert.Single(records, r => r.Entry.Kind == "vision-scan");
        using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
        {
            Assert.Equal("failed", p.RootElement.GetProperty("status").GetString());
            Assert.Contains("模拟网络失败", p.RootElement.GetProperty("error").GetString());
            Assert.Equal(0, p.RootElement.GetProperty("tilesDone").GetInt32());
            Assert.Equal(0, p.RootElement.GetProperty("claimsSoFar").GetInt32());
        }
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _)); // 失败留痕后链仍完整
    }

    [AvaloniaFact]
    public void VisionScan_MidReadFailure_ProgressAudited()
    {
        // 多瓦夹具首瓦读成功后 timeout：失败条目携带进度（tilesDone/claimsSoFar），
        // 与已留痕 llm-call 计数一致——审计者可重建中断点，不假装完成。
        string drawing = CreateMultiTileDrawing();
        try
        {
            InjectCensus(drawing);
            var client = new FailAfterFirstClient();
            var outW = new StringWriter(); var errW = new StringWriter();
            int code = VisionScanMode.Run(
                new[] { "--vision-scan", "--drawing", drawing, "--project", _project, "--confirm-egress" },
                outW, errW, client, FakeProfile());

            Assert.Equal(1, code);
            Assert.Equal(2, client.Calls); // 首瓦成功 + 次瓦抛
            Assert.Contains("模拟超时", errW.ToString());
            var records = AuditRecords();
            Assert.Single(records, r => r.Entry.Kind == "llm-call"); // 首瓦已留痕
            var failed = Assert.Single(records, r => r.Entry.Kind == "vision-scan");
            using (var p = JsonDocument.Parse(failed.Entry.ParamsJson))
            {
                Assert.Equal("failed", p.RootElement.GetProperty("status").GetString());
                Assert.Equal(1, p.RootElement.GetProperty("tilesDone").GetInt32());
                Assert.Equal(1, p.RootElement.GetProperty("claimsSoFar").GetInt32());
            }
            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            Assert.True(audit.VerifyChain(out _));
        }
        finally { File.Delete(drawing); }
    }
}
