using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace CadAgent.Cli.Tests;

/// <summary>
/// T7：幅面感知 agent 联动 E2E（幅面切片收尾）。
/// 双幅面合成夹具（两图框块+图号属性+幅面名文字+框内设备属性 Insert）：
/// fake LLM（LlmClientFactory seam）产 label-luminaires 计划——
/// ① params sheetId=S2 而 Lighting 确认仅 S1 → 跨幅跑偏拒绝（tx failed +
///    semantic-gate-refused 审计含 sheetId，重规划一次仍拒→Escalated，零落图）；
/// ② sheetId=S1 同幅 → Executed + tx-commit + agent-verify 绿 + 验链；
/// ③ 无 sheetId legacy 计划 → 全局 overlay 放行（行为保持）。
/// semantic scan stdout 加性 sheets 摘要数组 {sheetId,name,number}（T7 新契约）。
/// </summary>
public class SheetAwareAgentE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"sheetagent-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public SheetAwareAgentE2ETests()
    {
        Directory.CreateDirectory(_project);
        // 双幅面：S1 x∈[0,1000]（框内灯具 E，A=深照型灯→Lighting NativeObject 0.95 stamp S1）；
        // S2 x∈[2000,3000]（无灯具语义确认）。图框块名含"图框"+图号属性双触发登记。
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            var frame = new BlockRecord("院标准图框_A0");
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
            frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
            doc.BlockRecords.Add(frame);
            for (int i = 0; i < 2; i++)
            {
                var ins = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
                ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
                doc.Entities.Add(ins);
                doc.Entities.Add(new TextEntity
                {
                    InsertPoint = new XYZ(i * 2000 + 500, 650, 0),
                    Value = i == 0 ? "一层电力平面图" : "二层电力平面图",
                    Height = 30,
                    Layer = new Layer("图框")
                });
            }
            var e = new BlockRecord("E");
            e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
            doc.BlockRecords.Add(e);
            var lum = new Insert(e) { InsertPoint = new XYZ(100, 100, 0) };
            lum.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
            doc.Entities.Add(lum);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    /// <summary>幅面 ID=图框 Insert 句柄（DXF 往返稳定）；S1=一层（低 x），S2=二层。</summary>
    private (string S1, string S2) SheetIds()
    {
        var sheets = DrawingSession.Load(_drawing).Sheets;
        Assert.Equal(2, sheets.Count);
        return (sheets[0].SheetId, sheets[1].SheetId);
    }

    private void SetupProfile(string planJson)
    {
        Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1", "--model", "m", "--key", "sk-sheetkey12345678");
        Run("llm", "use", "--name", "p");
        CommandRouter.LlmClientFactory = (p, k) => new SheetPlanClient(planJson);
    }

    /// <summary>fake LLM：规划/重规划均回同一计划（跨幅拒绝重规划后仍拒→Escalated）；验证回 verdict ok。</summary>
    private sealed class SheetPlanClient : ILlmClient
    {
        private readonly string _planJson;
        public SheetPlanClient(string planJson) => _planJson = planJson;
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(
                request.Messages[0].Content.Contains("验证智能体") ? "{\"verdict\":\"ok\"}" : _planJson,
                new TokenUsage(10, 5), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private SqliteAuditStore OpenAudit() =>
        new(Path.Combine(_project, ".cadagent", "audit.db"));

    // ---------- ① 跨幅跑偏：sheetId=S2 而 Lighting 确认仅 S1 ----------

    [Fact]
    public void Agent_PlanSheetS2_LightingOnlyS1_RefusedAuditedWithSheetId_NoDrawingChange()
    {
        var (_, s2) = SheetIds();
        SetupProfile($$"""{"steps":[{"kind":"label-luminaires","params":{"prefix":"AL","block":"E","sheetId":"{{s2}}" } } ] }""");
        var (scanCode, _, scanErr) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        Assert.Empty(scanErr);
        // 前提钉住：Lighting 确认（NativeObject 0.95）stamp 在 S1，S2 无 Lighting
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        var (s1, _) = SheetIds();
        Assert.All(overlay.Annotations.Where(a => a.Class == SemanticClass.Lighting), a => Assert.Equal(s1, a.SheetId));

        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "给二层灯具编号", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Escalated", stdout); // 重规划一次仍拒→升级人工（不信任完成）

        using var audit = OpenAudit();
        var refused = audit.ReadAll().Where(r => r.Entry.Kind == "semantic-gate-refused").ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, r => Assert.Contains(s2, r.Entry.ParamsJson)); // 拒绝留痕含目标幅面
        Assert.Contains(refused, r => r.Entry.ParamsJson.Contains("Lighting"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "tx-commit"); // tx failed 无提交
        Assert.True(audit.VerifyChain(out _));
        // 零落图（无审计旁路）：编号文字未产生
        Assert.DoesNotContain(DrawingSession.Load(_drawing).Doc.Entities.OfType<TextEntity>(),
            t => t.Value.StartsWith("AL", StringComparison.Ordinal));
        Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-sheetkey12345678", r.Entry.ParamsJson));
    }

    // ---------- ② 正确幅面：sheetId=S1 同幅放行 ----------

    [Fact]
    public void Agent_PlanSheetS1_SameSheetConfirmation_Executed_VerifyGreen()
    {
        var (s1, _) = SheetIds();
        SetupProfile($$"""{"steps":[{"kind":"label-luminaires","params":{"prefix":"AL","block":"E","sheetId":"{{s1}}" } } ] }""");
        Run("semantic", "scan", "--drawing", _drawing);

        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "给一层灯具编号", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        Assert.Contains("\"verificationRounds\": 1", stdout); // 读回验证环走通

        Assert.Contains(DrawingSession.Load(_drawing).Doc.Entities.OfType<TextEntity>(),
            t => t.Value == "AL1"); // 编号落图
        using var audit = OpenAudit();
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "agent-verify" && r.Entry.ParamsJson.Contains("ok"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "semantic-gate-refused");
        Assert.True(audit.VerifyChain(out _));
    }

    // ---------- ③ legacy：无 sheetId 计划行为保持（全局 overlay 放行） ----------

    [Fact]
    public void Agent_LegacyPlanWithoutSheetId_GlobalOverlayAllowed()
    {
        SetupProfile("""{"steps":[{"kind":"label-luminaires","params":{"prefix":"AL","block":"E"}}]}""");
        Run("semantic", "scan", "--drawing", _drawing);

        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "给灯具编号", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        Assert.Contains(DrawingSession.Load(_drawing).Doc.Entities.OfType<TextEntity>(),
            t => t.Value == "AL1");
        using var audit = OpenAudit();
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }

    // ---------- semantic scan stdout 加性 sheets 摘要（T7 新契约，RED→GREEN） ----------

    [Fact]
    public void SemanticScan_Stdout_IncludesSheetsSummary_TwoSheets()
    {
        var (s1, s2) = SheetIds();
        var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Empty(stderr);
        using var json = JsonDocument.Parse(stdout);
        var sheets = json.RootElement.GetProperty("sheets").EnumerateArray().ToList();
        Assert.Equal(2, sheets.Count);
        Assert.Equal(s1, sheets[0].GetProperty("sheetId").GetString());
        Assert.Equal("一层电力平面图", sheets[0].GetProperty("name").GetString());
        Assert.Equal("Z-E01", sheets[0].GetProperty("number").GetString());
        Assert.Equal(s2, sheets[1].GetProperty("sheetId").GetString());
        Assert.Equal("二层电力平面图", sheets[1].GetProperty("name").GetString());
        Assert.Equal("Z-E02", sheets[1].GetProperty("number").GetString());
        // 既有契约保持：version/annotations/ambiguous 仍在
        Assert.True(json.RootElement.TryGetProperty("annotations", out _));
        Assert.True(json.RootElement.TryGetProperty("ambiguous", out _));
    }

    [Fact]
    public void SemanticScan_Stdout_SheetsSummary_LegacySingleSheet()
    {
        // 无图框 legacy 图纸 → 单默认幅面 S0（加性字段不破坏 legacy 口径）
        string plain = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", plain);
            Assert.Equal(0, code);
            using var json = JsonDocument.Parse(stdout);
            var sheet = Assert.Single(json.RootElement.GetProperty("sheets").EnumerateArray());
            Assert.Equal("S0", sheet.GetProperty("sheetId").GetString());
            Assert.Equal("(单幅面)", sheet.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, sheet.GetProperty("number").ValueKind);
        }
        finally { File.Delete(plain); }
    }
}
