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
/// R10 执行裁剪：夹具两幅各含灯具 Insert（S2 无 Lighting 语义）+ S1 房名文字；
/// ② sheetId=S1 → 仅 S1 编号（S2 bounds 零 AL*）；③ legacy 全图编号保持；
/// ④ layout-lighting sheetId=S1 区域跨 S2 → step-failed 留痕 + Escalated 零落图；
/// ⑤ 区域含于 S1 → Executed（裁剪不过度拒绝）。
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
            // R10：S2 亦含灯具 Insert（同块 E，但无 Lighting 语义属性→不种子 Lighting），
            // 钉住 sheetId=S1 计划不得给 S2 灯具编号（执行裁剪）。
            doc.Entities.Add(new Insert(e) { InsertPoint = new XYZ(2100, 100, 0) });
            // R10：S1 房名文字（ROOM-NAME 层）→ scan 推断 Room 0.6，map 确认 1.0 供 layout-lighting 闸口放行。
            doc.Layers.Add(new Layer("ROOM-NAME"));
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = new XYZ(300, 300, 0),
                Value = "客厅",
                Height = 20,
                Layer = doc.Layers.First(l => l.Name == "ROOM-NAME")
            });
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

        var doc = DrawingSession.Load(_drawing).Doc;
        Assert.Contains(doc.Entities.OfType<TextEntity>(), t => t.Value == "AL1"); // 编号落图
        // R10 执行裁剪钉住：sheetId=S1 → 仅 S1 灯具编号；S2 bounds 内零 AL* 文字
        var alTexts = doc.Entities.OfType<TextEntity>()
            .Where(t => t.Value.StartsWith("AL", StringComparison.Ordinal)).ToList();
        Assert.Single(alTexts); // S2 灯具 Insert 不被编号（全图匹配旧缺陷会产 AL2）
        Assert.All(alTexts, t => Assert.True(t.InsertPoint.X >= 0 && t.InsertPoint.X <= 1000)); // S1 bounds
        Assert.DoesNotContain(alTexts, t => t.InsertPoint.X >= 2000 && t.InsertPoint.X <= 3000); // S2 零编号
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
        var legacyTexts = DrawingSession.Load(_drawing).Doc.Entities.OfType<TextEntity>()
            .Where(t => t.Value.StartsWith("AL", StringComparison.Ordinal)).ToList();
        // R10 legacy 行为逐字保持：无 sheetId → 全图编号（S1+S2 两灯具各得编号）
        Assert.Contains(legacyTexts, t => t.Value == "AL1");
        Assert.Contains(legacyTexts, t => t.Value == "AL2");
        Assert.Equal(2, legacyTexts.Count);
        using var audit = OpenAudit();
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }

    // ---------- ④ R10：layout-lighting sheetId=S1 区域跨 S2 → 拒绝留痕（执行裁剪） ----------

    [Fact]
    public void Agent_LayoutLightingSheetS1_RegionCrossesS2_FailedAudited_NoDrawingChange()
    {
        var (s1, _) = SheetIds();
        SetupProfile($$"""{"steps":[{"kind":"layout-lighting","params":{"x":500,"y":100,"width":2000,"depth":400,"spacing":500,"block":"E","sheetId":"{{s1}}" } } ] }""");
        Run("semantic", "scan", "--drawing", _drawing);
        // Room 人工确认（ROOM-NAME 层→1.0）使闸口放行，隔离出 handler 区域 bounds 裁剪
        var (mapCode, _, mapErr) = Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing);
        Assert.Equal(0, mapCode);
        Assert.Empty(mapErr);

        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "在一层布灯", "--yes");
        Assert.Equal(1, code);
        Assert.Contains("Escalated", stdout); // 区域 [500,2500] 跨 S2 → 拒绝，重规划一次仍拒→升级

        using var audit = OpenAudit();
        var failed = audit.ReadAll().Where(r => r.Entry.Kind == "step-failed").ToList();
        Assert.NotEmpty(failed);
        // ParamsJson 经 System.Text.Json 默认编码非 ASCII 转义→解析 Error 属性再断言中文消息
        Assert.All(failed, r =>
        {
            using var jd = JsonDocument.Parse(r.Entry.ParamsJson);
            string err = jd.RootElement.GetProperty("Error").GetString()!;
            Assert.Contains("区域超出幅面", err); // 引擎失败留痕
            Assert.Contains(s1, err);
        });
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
        // 零落图：E 块 Insert 仍为原 2 灯具（无网格新增）
        Assert.Equal(2, DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>().Count(i => i.Block?.Name == "E"));
        Assert.All(audit.ReadAll(), r => Assert.DoesNotContain("sk-sheetkey12345678", r.Entry.ParamsJson));
    }

    // ---------- ⑤ R10：layout-lighting sheetId=S1 区域含于 S1 → 放行（裁剪不过度拒绝） ----------

    [Fact]
    public void Agent_LayoutLightingSheetS1_RegionWithinS1_Executed()
    {
        var (s1, _) = SheetIds();
        SetupProfile($$"""{"steps":[{"kind":"layout-lighting","params":{"x":100,"y":100,"width":800,"depth":500,"spacing":400,"block":"E","sheetId":"{{s1}}" } } ] }""");
        Run("semantic", "scan", "--drawing", _drawing);
        Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing);

        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "在一层布灯", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        var inserts = DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>()
            .Where(i => i.Block?.Name == "E").ToList();
        // 原 2 灯具 + 网格 2 点（(300,300)/(700,300)）= 4；网格全落 S1，S2 原灯具不动
        Assert.Equal(4, inserts.Count);
        Assert.Equal(3, inserts.Count(i => i.InsertPoint.X >= 0 && i.InsertPoint.X <= 1000)); // S1：原 1 + 网格 2
        Assert.Single(inserts, i => i.InsertPoint.X >= 2000 && i.InsertPoint.X <= 3000);     // S2 原灯具保持
        using var audit = OpenAudit();
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "step-failed");
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
