using System.Text.Json;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Tests;
using CSMath;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan-3 Task 7：CLI 块编辑计划端到端钉住（处理器 T5 已注册、run 经 HandlerRegistry 自动装配 →
/// 本类为集成钉住，首跑即绿属「实件先行」，非测试先行）。
/// 夹具：命名块 TITLE_E2E 内含 Line + TextEntity + AttributeDefinition + Circle（删除目标）；
/// 模型两条 Insert 引用同块（全实例语义）+ 模型线；布局"布局1"含一条纸面线（未波及断言）。
/// 断言口径：落盘回读块定义被改、两实例几何同步（共享 BlockRecord + 插入点合成）、ADEF 原样、
/// 模型/纸面不动、tx-start≠tx-commit（B| 行变化）、tx-commit 哈希=磁盘状态、step params 含 block、
/// audit verify 绿；负例（未知块/互斥/add-line+block/匿名块）拒绝先于落盘且无 tx-commit。
/// </summary>
public class BlockPlanE2ETests : IDisposable
{
    private const string TitleName = "TITLE_E2E";
    private const string LayoutName = "布局1";
    private const string BlockTextValue = "块图名";
    private const string AdefTag = "图号";
    private const string AdefValue = "JS-02";
    // add-* 拒绝文案单一来源（Data 内部常量，InternalsVisibleTo CadAgent.Cli.Tests）。
    private const string AddBlockUnsupportedMessage = HandlerHelpers.AddBlockUnsupportedMessage;

    private readonly string _project = Path.Combine(Path.GetTempPath(), $"blockplan-{Guid.NewGuid():N}");
    private readonly List<string> _drawings = new();
    private readonly string _drawing;

    /// <summary>
    /// 成功 run 的既定 stderr：夹具无语义标注 → 回归检查输出 1 条提示 + 3 条 NotEvaluable 跳过。
    /// 逐字钉住（替代旧「不含 未注册」弱断言）：任何新增/变化输出（真正的异常/警告）即为契约漂移。
    /// </summary>
    private const string ExpectedRegressionStderr =
        "提示: 无语义标注，轴号/标高规则未评估（请先 semantic scan/map）\n" +
        "跳过: [NotEvaluable] R-50001-11.8.4: 规则 R-50001-11.8.4 无语义标注，未评估\n" +
        "跳过: [NotEvaluable] R-50001-11.8.5: 规则 R-50001-11.8.5 无语义标注，未评估\n" +
        "跳过: [NotEvaluable] R-50001-8.0.4: 规则 R-50001-8.0.4 无语义标注，未评估";

    /// <summary>stderr 逐字等于既定回归提示（换行归一化；容忍末尾空行）。</summary>
    private static void AssertExpectedStderr(string stderr) =>
        Assert.Equal(ExpectedRegressionStderr, stderr.Replace("\r\n", "\n").TrimEnd('\n'));

    public BlockPlanE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = CreateDrawing();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_project, true); } catch (IOException) { }
        foreach (string path in _drawings)
        {
            try { File.Delete(path); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    /// <summary>块夹具落盘（blockSetup 在写盘前施加：匿名标志等须随 DXF 持久化）。</summary>
    private string CreateDrawing(Action<BlockRecord>? blockSetup = null)
    {
        string path = DxfFixture.CreateTempDxf(doc =>
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
            layout.AssociatedBlock!.Entities.Add(new Line
            {
                StartPoint = new XYZ(10, 10, 0),
                EndPoint = new XYZ(60, 10, 0)
            });

            var block = new BlockRecord(TitleName);
            block.Entities.Add(new Line
            {
                StartPoint = new XYZ(0, 0, 0),
                EndPoint = new XYZ(10, 0, 0)
            });
            block.Entities.Add(new TextEntity
            {
                InsertPoint = new XYZ(2, 2, 0),
                Value = BlockTextValue,
                Height = 3.5
            });
            block.Entities.Add(new AttributeDefinition
            {
                Tag = AdefTag,
                Value = AdefValue,
                InsertPoint = new XYZ(4, 2, 0),
                Height = 3.5
            });
            block.Entities.Add(new Circle { Center = new XYZ(20, 0, 0), Radius = 2 }); // 删除目标
            doc.BlockRecords.Add(block);
            blockSetup?.Invoke(block);

            doc.Entities.Add(new Line
            {
                StartPoint = new XYZ(0, 500, 0),
                EndPoint = new XYZ(10, 500, 0)
            });
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0) });
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(100, 0, 0) });
        });
        _drawings.Add(path);
        return path;
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
    private sealed record Probe(BlockRecord Block, Insert First, Insert Second, Line ModelLine, Line PaperLine)
    {
        public Line BlockLine => Block.Entities.OfType<Line>().Single();
        // ADEF : TextEntity——块内文字须排除属性定义，否则 Single 双命中。
        public TextEntity BlockText => Block.Entities.OfType<TextEntity>()
            .Single(t => t is not AttributeDefinition);
        public AttributeDefinition Adef => Block.Entities.OfType<AttributeDefinition>().Single();

        public static Probe Read(string path)
        {
            var doc = DrawingSession.Load(path).Doc;
            var block = doc.BlockRecords.Single(b => b.Name == TitleName && b.Layout is null);
            var inserts = doc.Entities.OfType<Insert>()
                .OrderBy(i => i.InsertPoint.X).ToList();
            var paper = doc.Layouts.Single(l => l.Name == LayoutName && l.IsPaperSpace);
            if (inserts.Count != 2)
                throw new InvalidOperationException($"夹具往返异常：inserts={inserts.Count}");
            return new Probe(block, inserts[0], inserts[1],
                doc.Entities.OfType<Line>().Single(),
                paper.AssociatedBlock!.Entities.OfType<Line>().Single());
        }
    }

    [Fact]
    public void BlockPlan_MoveChangeLayerSetTextDelete_Committed_AllInstancesFollow()
    {
        var before = Probe.Read(_drawing);
        Assert.NotEqual(0UL, before.BlockLine.Handle); // 反空断言：块内实体落盘后必须有句柄
        string preHash = DrawingSession.Load(_drawing).ComputeHash();
        byte[] preBytes = File.ReadAllBytes(_drawing);

        string planPath = WritePlan("block-1", new[]
        {
            // 块局部增量（dx/dy 直接作用于块定义坐标）
            Step("move-entity", new
            {
                block = TitleName, handle = before.BlockLine.Handle.ToString(), dx = 1.5, dy = -2
            }),
            Step("change-layer", new
            {
                block = TitleName, handle = before.BlockText.Handle.ToString(), layer = "T-块文字"
            }),
            Step("set-text", new
            {
                block = TitleName, handle = before.BlockText.Handle.ToString(), text = "新块图名"
            }),
            Step("delete-entity", new
            {
                block = TitleName,
                handle = before.Block.Entities.OfType<Circle>().Single().Handle.ToString()
            }),
        });

        var (code, stdout, stderr) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        AssertExpectedStderr(stderr); // 仅既定回归提示（无异常/未注册等意外输出）

        // 落盘回读：块定义改动生效
        var after = Probe.Read(_drawing);
        Assert.NotEqual(preBytes, File.ReadAllBytes(_drawing)); // 确实落盘
        Assert.Equal(1.5, after.BlockLine.StartPoint.X, 6);
        Assert.Equal(-2, after.BlockLine.StartPoint.Y, 6);
        Assert.Equal(11.5, after.BlockLine.EndPoint.X, 6);
        Assert.Equal(-2, after.BlockLine.EndPoint.Y, 6);
        Assert.Equal("新块图名", after.BlockText.Value);
        Assert.Equal("T-块文字", after.BlockText.Layer.Name);
        Assert.Empty(after.Block.Entities.OfType<Circle>()); // delete 生效
        // ADEF 模板原样（delete 不删属性定义，块内文字编辑不触默认值）
        Assert.Equal(AdefTag, after.Adef.Tag);
        Assert.Equal(AdefValue, after.Adef.Value);
        Assert.Equal(4, after.Adef.InsertPoint.X, 6);

        // 两实例同步：同一 BlockRecord + 实例插入点合成后的世界几何均位移（无旋转/缩放，平移可加）
        Assert.Equal(TitleName, after.First.Block!.Name);
        Assert.Same(after.First.Block, after.Second.Block);
        var sharedLine = after.First.Block!.Entities.OfType<Line>().Single();
        Assert.Same(sharedLine, after.Second.Block!.Entities.OfType<Line>().Single());
        Assert.Equal(1.5, after.First.InsertPoint.X + sharedLine.StartPoint.X, 6);
        Assert.Equal(-2, after.First.InsertPoint.Y + sharedLine.StartPoint.Y, 6);
        Assert.Equal(101.5, after.Second.InsertPoint.X + sharedLine.StartPoint.X, 6);
        Assert.Equal(-2, after.Second.InsertPoint.Y + sharedLine.StartPoint.Y, 6);
        // 实例自身位置不动（块局部增量只改定义）
        Assert.Equal(0, after.First.InsertPoint.X, 6);
        Assert.Equal(100, after.Second.InsertPoint.X, 6);

        // 模型/纸面其余不变
        Assert.Equal(500, after.ModelLine.StartPoint.Y, 6);
        Assert.Equal(10, after.PaperLine.StartPoint.X, 6);
        Assert.Equal(60, after.PaperLine.EndPoint.X, 6);
        Assert.Equal(3, DrawingSession.Load(_drawing).Doc.Entities.Count); // 模型线 + 两 Insert

        // 审计：单事务提交、前后哈希可辨、commit 哈希=磁盘状态、step params 含 block
        string postHash = DrawingSession.Load(_drawing).ComputeHash();
        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var txStart = Assert.Single(records, r => r.Entry.Kind == "tx-start");
        var txCommit = Assert.Single(records, r => r.Entry.Kind == "tx-commit");
        Assert.Equal(preHash, txStart.Entry.BeforeHash);
        Assert.Equal(preHash, txStart.Entry.AfterHash);
        Assert.NotEqual(txStart.Entry.AfterHash, txCommit.Entry.AfterHash);
        Assert.Equal(postHash, txCommit.Entry.AfterHash);
        Assert.Equal(4, records.Count(r => r.Entry.Kind == "step"));
        Assert.DoesNotContain(records, r => r.Entry.Kind is "step-failed" or "tx-failed");
        Assert.True(audit.VerifyChain(out _));

        var steps = records.Where(r => r.Entry.Kind == "step").ToList();
        foreach (var step in steps)
        {
            // 审计 params 逐字含 block（非 ASCII 经默认编码器转义 → 解析后比较原文）
            using var stepParams = JsonDocument.Parse(step.Entry.ParamsJson);
            var stepArgs = stepParams.RootElement.GetProperty("Params");
            Assert.Equal(TitleName, stepArgs.GetProperty("block").GetString());
        }
        var moveStep = Assert.Single(steps,
            r => r.Entry.ParamsJson.Contains("\"move-entity\""));
        Assert.Equal(before.BlockLine.Handle.ToString(), moveStep.Entry.Target); // 审计目标=块内实体句柄

        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);
    }

    [Fact]
    public void UnknownBlock_StepFails_UnknownBlockName_NoCommitNoWrite()
    {
        var before = Probe.Read(_drawing);
        byte[] preBytes = File.ReadAllBytes(_drawing);
        string planPath = WritePlan("blk-unknown", new[]
        {
            Step("move-entity", new
            {
                block = "不存在的块", handle = before.BlockLine.Handle.ToString(), dx = 1, dy = 1
            })
        }, "unknown.json");

        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.NotEqual(0, code);
        Assert.Contains("\"ok\":false", stdout.Replace(" ", ""));
        Assert.Equal(preBytes, File.ReadAllBytes(_drawing)); // 不落盘

        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var failed = Assert.Single(records, r => r.Entry.Kind == "step-failed");
        using (var failedParams = JsonDocument.Parse(failed.Entry.ParamsJson))
            Assert.Contains("未知块名", failedParams.RootElement.GetProperty("Error").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
        Assert.Equal(0, Assert.Single(Probe.Read(_drawing).Block.Entities.OfType<Line>()).StartPoint.X, 6);
    }

    [Fact]
    public void TwoStepPlan_FirstStepValidSecondUnknownBlock_NoPartialWriteNoCommit()
    {
        // 两步部分写入：首步（change-layer）处理器已改内存，次步未知块失败 → 单事务整体失败，
        // 不得半写落盘（RunPlan 失败分支 Reload 回滚内存），审计仅一条 step-failed、无 tx-commit。
        var before = Probe.Read(_drawing);
        string preLayer = before.BlockText.Layer.Name;
        byte[] preBytes = File.ReadAllBytes(_drawing);
        string planPath = WritePlan("blk-partial", new[]
        {
            Step("change-layer", new
            {
                block = TitleName, handle = before.BlockText.Handle.ToString(), layer = "T-半写"
            }),
            Step("move-entity", new
            {
                block = "不存在的块", handle = before.BlockLine.Handle.ToString(), dx = 1, dy = 1
            }),
        }, "partial.json");

        var (code, stdout, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.NotEqual(0, code);
        Assert.Contains("\"ok\":false", stdout.Replace(" ", ""));
        Assert.Equal(preBytes, File.ReadAllBytes(_drawing)); // 首步内存变更不得落盘

        var after = Probe.Read(_drawing);
        Assert.Equal(preLayer, after.BlockText.Layer.Name); // 首步改动随事务失败丢弃

        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var failed = Assert.Single(records, r => r.Entry.Kind == "step-failed");
        using (var failedParams = JsonDocument.Parse(failed.Entry.ParamsJson))
            Assert.Contains("未知块名", failedParams.RootElement.GetProperty("Error").GetString());
        Assert.Single(records, r => r.Entry.Kind == "step"); // 首步在失败前确已执行（非未跑）
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void BlockWithSpace_ConflictRejected_NoCommitNoWrite()
    {
        var before = Probe.Read(_drawing);
        byte[] preBytes = File.ReadAllBytes(_drawing);
        string planPath = WritePlan("blk-conflict", new[]
        {
            Step("move-entity", new
            {
                block = TitleName, handle = before.BlockLine.Handle.ToString(), dx = 1, dy = 1,
                space = "paper", layout = LayoutName
            })
        }, "conflict.json");

        var (code, _, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.NotEqual(0, code);
        Assert.Equal(preBytes, File.ReadAllBytes(_drawing));

        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var failed = Assert.Single(records, r => r.Entry.Kind == "step-failed");
        using (var failedParams = JsonDocument.Parse(failed.Entry.ParamsJson))
            Assert.Contains("block 不能与 space/layout/attribute",
                failedParams.RootElement.GetProperty("Error").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void AddLineWithBlock_UnsupportedMessage_NoCommitNoWrite()
    {
        byte[] preBytes = File.ReadAllBytes(_drawing);
        string planPath = WritePlan("add-block", new[]
        {
            Step("add-line", new { block = TitleName, x1 = 1, y1 = 2, x2 = 3, y2 = 4 })
        }, "add-block.json");

        var (code, _, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.NotEqual(0, code);
        Assert.Equal(preBytes, File.ReadAllBytes(_drawing));

        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var failed = Assert.Single(records, r => r.Entry.Kind == "step-failed");
        using (var failedParams = JsonDocument.Parse(failed.Entry.ParamsJson))
            Assert.Equal(AddBlockUnsupportedMessage,
                failedParams.RootElement.GetProperty("Error").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void AnonymousBlock_Rejected_NoCommitNoWrite()
    {
        // 匿名标志须随 DXF 持久化（写盘前施加）；反空断言先钉住夹具前提。
        string anonymous = CreateDrawing(block => block.Flags |= BlockTypeFlags.Anonymous);
        var before = Probe.Read(anonymous);
        Assert.True(before.Block.IsAnonymous);
        byte[] preBytes = File.ReadAllBytes(anonymous);
        string planPath = WritePlan("blk-anon", new[]
        {
            Step("delete-entity", new
            {
                block = TitleName, handle = before.BlockLine.Handle.ToString()
            })
        }, "anon.json");

        var (code, _, _) = Run("run", planPath, "--drawing", anonymous);
        Assert.NotEqual(0, code);
        Assert.Equal(preBytes, File.ReadAllBytes(anonymous));

        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var failed = Assert.Single(records, r => r.Entry.Kind == "step-failed");
        using (var failedParams = JsonDocument.Parse(failed.Entry.ParamsJson))
            Assert.Contains("块不可编辑", failedParams.RootElement.GetProperty("Error").GetString());
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void ForgedAffectedInstances_EngineIgnores_AuditPreservesVerbatim()
    {
        // affectedInstances 仅为审计附注（视图确认计数），引擎不读：伪造值（999）不得改变执行，
        // 审计须逐字保留计划给出的值（可复算/可追责，不做校验或重写）。
        var before = Probe.Read(_drawing);
        string planPath = WritePlan("blk-forged", new[]
        {
            Step("move-entity", new
            {
                block = TitleName, handle = before.BlockLine.Handle.ToString(),
                dx = 2, dy = 0, affectedInstances = 999
            })
        }, "forged.json");

        var (code, stdout, stderr) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, code);
        Assert.Contains("\"ok\":true", stdout.Replace(" ", ""));
        AssertExpectedStderr(stderr);

        Assert.Equal(2, Probe.Read(_drawing).BlockLine.StartPoint.X, 6); // 按计划位移执行
        using var audit = OpenAudit();
        var records = audit.ReadAll();
        var step = Assert.Single(records, r => r.Entry.Kind == "step");
        Assert.Contains("\"affectedInstances\":999", step.Entry.ParamsJson.Replace(" ", ""));
        Assert.Single(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(audit.VerifyChain(out _));
    }
}
