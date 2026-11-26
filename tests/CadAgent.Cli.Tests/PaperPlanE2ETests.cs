using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CSMath;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan-2 Task 3：CLI 纸面计划端到端钉住（处理器在 T1/T2 已注册 → 本类为集成钉住，
/// 首跑即绿属「实件先行」，非测试先行）。
/// 夹具：模型线 + 模型文字；布局"布局1"（先 AddViewport 再挂纸面实体）：纸面线、
/// 纸面文字「待删纸面字」（删除目标）、纸面文字「旧图名」（改字目标）、标题块
/// Insert(Tag=图号, Value=OLD)。断言口径：纸面改动落盘回读正确、模型语义逐字不变、
/// tx-start/tx-commit 哈希可辨、audit verify 绿。
/// 已知边界（无代码改动）：--live 经 ComReplayService 重放——set-text/set-attribute 未映射 →
/// 响亮「COM 重放不支持步骤类型」；纸面 add-* 经总数读回闸口「读回分歧」；纸面 change-layer+space 未证 fail-closed（待跟踪）。
/// </summary>
public class PaperPlanE2ETests : IDisposable
{
    private const string LayoutName = "布局1";
    private const string ModelTextValue = "模型说明";
    private const string DeleteTextValue = "待删纸面字";
    private const string SetTextValue = "旧图名";
    private const string AttrTag = "图号";
    private const string AttrOldValue = "OLD";

    private readonly string _project = Path.Combine(Path.GetTempPath(), $"paperplan-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public PaperPlanE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            var layout = new Layout(LayoutName);
            doc.Layouts.Add(layout);
            // 既有夹具口径：先 AddViewport 再取 AssociatedBlock（纸面块此时方可用）
            layout.AddViewport(new Viewport
            {
                Center = new XYZ(500, 350, 0),
                Width = 1000,
                Height = 700,
                ViewTarget = new XYZ(0, 0, 0),
                ViewHeight = 200
            });
            // 模型空间：与纸面坐标刻意区分，保证"模型未变"断言可辨
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 200, 0), Value = ModelTextValue, Height = 3.5 });
            // 纸面：线 + 待删文字 + 待改文字
            layout.AssociatedBlock!.Entities.Add(new Line
            {
                StartPoint = new XYZ(10, 10, 0),
                EndPoint = new XYZ(60, 10, 0)
            });
            layout.AssociatedBlock!.Entities.Add(new TextEntity
            {
                InsertPoint = new XYZ(10, 20, 0),
                Value = DeleteTextValue,
                Height = 3.5
            });
            layout.AssociatedBlock!.Entities.Add(new TextEntity
            {
                InsertPoint = new XYZ(10, 30, 0),
                Value = SetTextValue,
                Height = 3.5
            });
            // 标题块 Insert + 图号实例属性（属性在纸面实例上）
            var titleBlock = new BlockRecord("TITLE_A1");
            titleBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(841, 0, 0) });
            doc.BlockRecords.Add(titleBlock);
            var title = new Insert(titleBlock) { InsertPoint = new XYZ(420, 297, 0) };
            var attr = new AttributeEntity { Tag = AttrTag, Value = AttrOldValue };
            attr.InsertPoint = new XYZ(430, 280, 0);
            attr.AlignmentPoint = new XYZ(430, 280, 0);
            title.Attributes.Add(attr);
            layout.AssociatedBlock!.Entities.Add(title);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_project, true); } catch (IOException) { }
        try { File.Delete(_drawing); } catch (IOException) { }
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private SqliteAuditStore OpenAudit() =>
        new(Path.Combine(_project, ".cadagent", "audit.db"));

    private static object Step(string kind, object @params) => new { kind, @params };

    private string WritePlan(string planId, object[] steps, string fileName = "plan.json")
    {
        string path = Path.Combine(_project, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new { planId, steps }));
        return path;
    }

    /// <summary>落盘回读探针（与 run 同源）：句柄与实体状态均以磁盘文件为准。</summary>
    private sealed record Probe(
        Layout Paper, Line PaperLine, Insert Title, AttributeEntity Attr,
        Line ModelLine, TextEntity ModelText)
    {
        public static Probe Read(string path)
        {
            var doc = DrawingSession.Load(path).Doc;
            var paper = doc.Layouts.Single(l => l.Name == LayoutName && l.IsPaperSpace);
            var block = paper.AssociatedBlock!;
            var title = block.Entities.OfType<Insert>().Single(i => i.Block?.Name == "TITLE_A1");
            var paperLines = block.Entities.OfType<Line>().ToList();
            var modelLines = doc.Entities.OfType<Line>().ToList();
            var paperTexts = block.Entities.OfType<TextEntity>().ToList();
            var modelTexts = doc.Entities.OfType<TextEntity>().ToList();
            if (modelLines.Count != 1 || paperLines.Count(l => l.EndPoint.X < 100) != 1
                || modelTexts.Count != 1)
                throw new InvalidOperationException(
                    $"夹具往返异常：paperLines={paperLines.Count} modelLines={modelLines.Count} " +
                    $"paperTexts={paperTexts.Count} modelTexts={modelTexts.Count} " +
                    $"paperTextValues=[{string.Join(",", paperTexts.Select(t => t.Value))}]");
            return new Probe(
                paper,
                paperLines.Single(l => l.EndPoint.X < 100), // 夹具线（新增线端点 300）
                title,
                title.Attributes.Single(a => a.Tag == AttrTag),
                modelLines.Single(),
                modelTexts.Single());
        }
    }

    [Fact]
    public void PaperPlan_MoveDeleteAddSetAttributeSetText_CommittedAudited_ModelUntouched()
    {
        var before = Probe.Read(_drawing);
        string preHash = DrawingSession.Load(_drawing).ComputeHash();
        byte[] preBytes = File.ReadAllBytes(_drawing);
        // 夹具前提：纸面 1 线 2 文字（落盘回读后仍成立）
        Assert.Single(before.Paper.AssociatedBlock!.Entities.OfType<Line>());
        Assert.Equal(2, before.Paper.AssociatedBlock!.Entities.OfType<TextEntity>().Count());

        string planPath = WritePlan("paper-1", new[]
        {
            // 纸面线移动（显式 space/layout）
            Step("move-entity", new
            {
                handle = before.PaperLine.Handle.ToString(), dx = 5, dy = 7,
                space = "paper", layout = LayoutName
            }),
            // 纸面文字删除
            Step("delete-entity", new
            {
                handle = before.Paper.AssociatedBlock!.Entities.OfType<TextEntity>()
                    .Single(t => t.Value == DeleteTextValue).Handle.ToString(),
                space = "paper", layout = LayoutName
            }),
            // 纸面新增线（纸面坐标）
            Step("add-line", new
            {
                x1 = 200, y1 = 100, x2 = 300, y2 = 100,
                space = "paper", layout = LayoutName
            }),
            // 标题栏实例属性改值（句柄形态）
            Step("set-attribute", new { handle = before.Attr.Handle.ToString(), value = "NEW" }),
            // 纸面文字改字（space/layout 限定，第二次纸面文字）
            Step("set-text", new
            {
                handle = before.Paper.AssociatedBlock!.Entities.OfType<TextEntity>()
                    .Single(t => t.Value == SetTextValue).Handle.ToString(),
                text = "新图名",
                space = "paper", layout = LayoutName
            }),
        });

        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));

        // 落盘回读：纸面改动全部生效，模型语义逐字不变
        var after = Probe.Read(_drawing);
        Assert.NotEqual(preBytes, File.ReadAllBytes(_drawing)); // 确实落盘
        Assert.Equal(15, after.PaperLine.StartPoint.X, 6);
        Assert.Equal(17, after.PaperLine.StartPoint.Y, 6);
        // 夹具纸面线 (10,10)-(60,10) + (dx,dy)=(5,7)：端点同样平移
        Assert.Equal(65, after.PaperLine.EndPoint.X, 6);
        Assert.Equal(17, after.PaperLine.EndPoint.Y, 6);
        Assert.Equal(2, after.Paper.AssociatedBlock!.Entities.OfType<Line>().Count()); // 新增线
        Assert.Single(after.Paper.AssociatedBlock!.Entities.OfType<TextEntity>());     // 删除后剩 1 条
        var paperTexts = after.Paper.AssociatedBlock!.Entities.OfType<TextEntity>()
            .Select(t => t.Value).ToList();
        Assert.DoesNotContain(DeleteTextValue, paperTexts);
        Assert.Contains("新图名", paperTexts);
        Assert.Contains(after.Paper.AssociatedBlock!.Entities.OfType<Line>(),
            l => Math.Abs(l.StartPoint.X - 200) < 1e-6 && Math.Abs(l.EndPoint.X - 300) < 1e-6);
        Assert.Equal("NEW", after.Attr.Value);
        Assert.Equal(AttrTag, after.Attr.Tag); // 改值不改标签
        // 模型空间：坐标/文字逐字不变
        Assert.Equal(0, after.ModelLine.StartPoint.X, 6);
        Assert.Equal(1000, after.ModelLine.EndPoint.X, 6);
        Assert.Equal(ModelTextValue, after.ModelText.Value);
        Assert.Equal(2, DrawingSession.Load(_drawing).Doc.Entities.Count);

        // 审计：tx-start/tx-commit 哈希可辨（ComputeHash 覆盖纸面/属性），链完整
        string postHash = DrawingSession.Load(_drawing).ComputeHash();
        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var txStart = Assert.Single(records, r => r.Entry.Kind == "tx-start");
        var txCommit = Assert.Single(records, r => r.Entry.Kind == "tx-commit");
        Assert.Equal(preHash, txStart.Entry.BeforeHash);
        Assert.Equal(preHash, txStart.Entry.AfterHash);
        Assert.NotEqual(txStart.Entry.AfterHash, txCommit.Entry.AfterHash);
        Assert.Equal(postHash, txCommit.Entry.AfterHash); // 审计哈希=真实落盘状态
        Assert.Equal(5, records.Count(r => r.Entry.Kind == "step"));
        // spec §3.2：审计 params 逐字含 space/layout（非 ASCII 经默认编码器转义 → 解析后比较原文）
        var moveStep = Assert.Single(records,
            r => r.Entry.Kind == "step" && r.Entry.ParamsJson.Contains("\"move-entity\""));
        using (var stepParams = JsonDocument.Parse(moveStep.Entry.ParamsJson))
        {
            var stepArgs = stepParams.RootElement.GetProperty("Params");
            Assert.Equal("paper", stepArgs.GetProperty("space").GetString());
            Assert.Equal(LayoutName, stepArgs.GetProperty("layout").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind is "step-failed" or "tx-failed");
        Assert.True(audit.VerifyChain(out _));

        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
    }

    [Fact]
    public void LegacyModelPlan_NoSpaceLayout_BehaviorUnchanged_PaperUntouched()
    {
        var before = Probe.Read(_drawing);
        double paperStartX = before.PaperLine.StartPoint.X;

        string planPath = WritePlan("legacy-1", new[]
        {
            // 历史风格：无 space/layout → 仅句柄寻址，模型优先
            Step("move-entity", new { handle = before.ModelLine.Handle.ToString(), dx = 1, dy = 2 }),
            Step("change-layer", new { handle = before.ModelLine.Handle.ToString(), layer = "M-模型" })
        });

        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));

        var after = Probe.Read(_drawing);
        Assert.Equal(1, after.ModelLine.StartPoint.X, 6);
        Assert.Equal(2, after.ModelLine.StartPoint.Y, 6);
        Assert.Equal("M-模型", after.ModelLine.Layer.Name);
        Assert.Equal(paperStartX, after.PaperLine.StartPoint.X, 6); // 纸面未波及
        Assert.Equal(AttrOldValue, after.Attr.Value);

        using var audit = OpenAudit();
        Assert.True(audit.VerifyChain(out _));
        Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
    }

    [Fact]
    public void InvalidSpace_StepFails_SpaceOnlyPaperAudited_NoCommitNoWrite()
    {
        var before = Probe.Read(_drawing);
        string preHash = DrawingSession.Load(_drawing).ComputeHash();
        byte[] preBytes = File.ReadAllBytes(_drawing);

        string planPath = WritePlan("bad-space", new[]
        {
            Step("add-line", new
            {
                x1 = 1, y1 = 2, x2 = 3, y2 = 4,
                space = "foo", layout = LayoutName
            })
        }, "bad-plan.json");

        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.NotEqual(0, code);
        Assert.Contains("\"ok\":false", stdout.Replace(" ", ""));

        // 不提交、不落盘
        Assert.Equal(preBytes, File.ReadAllBytes(_drawing));
        Assert.Equal(preHash, DrawingSession.Load(_drawing).ComputeHash());

        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var failed = Assert.Single(records, r => r.Entry.Kind == "step-failed");
        // paramsJson 经默认编码器转义非 ASCII → 解析后比较原文
        using (var failedParams = JsonDocument.Parse(failed.Entry.ParamsJson))
        {
            Assert.Equal("add-line", failedParams.RootElement.GetProperty("Kind").GetString());
            Assert.Contains("space 仅支持 paper",
                failedParams.RootElement.GetProperty("Error").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }
}
