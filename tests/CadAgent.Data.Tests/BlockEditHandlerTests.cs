using System.Text.Json;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 处理器块定义夹具（Plan-3 Task 5）：命名块 TITLE_EDIT 内含 Line + TextEntity +
/// AttributeDefinition + 嵌套 Insert(INNER_EDIT)；模型两条 Insert 引用该块（全实例语义）。
/// 保护标志/动态标志复用 <see cref="BlockFixtures"/>（落盘后强置，口俓同 Resolver 测试）。
/// </summary>
internal static class EditBlockFixtures
{
    public const string TitleName = "TITLE_EDIT";
    public const string InnerName = "INNER_EDIT";
    public const string LayoutName = "布局1";
    public const ulong NestedAttributeHandle = 7101;

    public static (DrawingSession Session, BlockRecord Block, Line Line, TextEntity Text,
        AttributeDefinition Adef, Insert Nested, Insert First, Insert Second, string Path) Session()
    {
        var doc = new CadDocument();
        var layout = new Layout(LayoutName);
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0), Width = 1000, Height = 700,
            ViewTarget = new XYZ(0, 0, 0), ViewHeight = 200
        });
        layout.AssociatedBlock!.Entities.Add(new Line
        {
            StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0)
        });

        var inner = new BlockRecord(InnerName);
        inner.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 2 });
        doc.BlockRecords.Add(inner);

        var block = new BlockRecord(TitleName);
        var line = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) };
        var text = new TextEntity { Value = "图名", InsertPoint = new XYZ(2, 2, 0), Height = 3.5 };
        var adef = new AttributeDefinition
        {
            Tag = "图号", Value = "JS-01", InsertPoint = new XYZ(4, 2, 0), Height = 3.5
        };
        var nested = new Insert(inner) { InsertPoint = new XYZ(5, 0, 0) };
        nested.Attributes.Add(new AttributeEntity { Tag = "内属性", Value = "内值" });
        block.Entities.Add(line);
        block.Entities.Add(text);
        block.Entities.Add(adef);
        block.Entities.Add(nested);
        doc.BlockRecords.Add(block);

        var first = new Insert(block) { InsertPoint = new XYZ(0, 0, 0) };
        var second = new Insert(block) { InsertPoint = new XYZ(100, 0, 0) };
        doc.Entities.Add(first);
        doc.Entities.Add(second);

        string path = DxfFixture.WriteTempDxf(doc);
        // 块内 Insert 的属性不在块定义直接实体层：句柄强置以钉住“不可解析”语义。
        PaperFixtures.ForceHandle(nested.Attributes.First(), NestedAttributeHandle);
        return (new DrawingSession(path, doc), block, line, text, adef, nested, first, second, path);
    }
}

/// <summary>
/// Plan-3 Task 5：四类句柄编辑接通块定义空间（move/delete/change-layer/set-text）。
/// 断言口俓：块定义被改（全实例共享同一 BlockRecord）、失败先于任何变更、
/// 入口文案区分未知块/受保护块/非法句柄/块内未命中；set-attribute 仍拒 block。
/// </summary>
public class BlockEditHandlerTests : IDisposable
{
    private readonly List<string> _paths = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"blockedit-{Guid.NewGuid():N}");

    public BlockEditHandlerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        foreach (string p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private (DrawingSession S, BlockRecord Block, Line Line, TextEntity Text,
        AttributeDefinition Adef, Insert Nested, Insert First, Insert Second) New()
    {
        var (s, block, line, text, adef, nested, first, second, path) = EditBlockFixtures.Session();
        _paths.Add(path);
        return (s, block, line, text, adef, nested, first, second);
    }

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o);

    // ---- move-entity：块局部增量，全实例共享定义 ----

    [Fact]
    public void Move_BlockLine_AppliesLocalDelta_SharedDefinition()
    {
        var (s, block, line, _, _, _, first, second) = New();
        Assert.NotEqual(0UL, line.Handle); // 反空断言：落盘后块内实体必须获得句柄
        XYZ firstPoint = first.InsertPoint;
        string before = s.ComputeHash();

        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = block.Name, dx = 1.5, dy = -2
        }));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(line.Handle.ToString(), r.TargetHandle);
        Assert.Equal(1.5, line.StartPoint.X, 6);
        Assert.Equal(-2, line.StartPoint.Y, 6);
        Assert.Equal(11.5, line.EndPoint.X, 6);
        // 两条实例引用同一块定义：定义变即两实例同步；Insert 自身位置不动（块局部增量）。
        Assert.Same(block, first.Block);
        Assert.Same(block, second.Block);
        // 实例可达证据：经实例的 Block.Entities 能取到已移动实体（不只是同引用比较）。
        Assert.Contains(line, first.Block!.Entities);
        Assert.Contains(line, second.Block!.Entities);
        Assert.Equal(1.5, second.Block!.Entities.OfType<Line>().Single().StartPoint.X, 6);
        Assert.Equal(firstPoint.X, first.InsertPoint.X, 6);
        Assert.NotEqual(before, s.ComputeHash());
        // 第二实例经同名块再解析到同一实体。
        var again = EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString(), block = block.Name }));
        Assert.Same(line, again!.Entity);
    }

    // ---- delete-entity：从块定义集合移除 ----

    [Fact]
    public void Delete_BlockLine_RemovedFromDefinition()
    {
        var (s, block, line, _, _, _, first, second) = New();
        int before = block.Entities.Count;
        string handle = line.Handle.ToString();

        var r = new DeleteEntityHandler().Apply(s, P(new { handle, block = block.Name }));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(handle, r.TargetHandle);
        Assert.DoesNotContain(line, block.Entities);
        Assert.Equal(before - 1, block.Entities.Count);
        // 全实例视角：两条实例仍指向同一块定义，经实例集合已不可达该线。
        Assert.Same(block, first.Block);
        Assert.Same(block, second.Block);
        Assert.DoesNotContain(line, first.Block!.Entities);
        Assert.DoesNotContain(line, second.Block!.Entities);
        Assert.Empty(block.Entities.OfType<Line>());
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle, block = block.Name })));
    }

    [Fact]
    public void Delete_BlockAttributeDefinition_Rejected()
    {
        var (s, block, _, _, adef, _, _, _) = New();
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = adef.Handle.ToString(), block = block.Name
        }));
        Assert.False(r.Ok);
        Assert.Equal("属性定义不可删除", r.Error);
        Assert.Contains(adef, block.Entities); // 定义模板保护：删除失败不改集合
    }

    // ---- change-layer ----

    [Fact]
    public void ChangeLayer_BlockText_SetsLayer()
    {
        var (s, block, _, text, _, _, first, _) = New();
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = block.Name, layer = "T-文字"
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("T-文字", text.Layer.Name);
        Assert.Equal("0", first.Layer.Name); // 实例层不变（块定义内容变）
    }

    // ---- set-text ----

    [Fact]
    public void SetText_BlockText_ChangesValue()
    {
        var (s, block, _, text, _, _, _, _) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = block.Name, text = "新图名"
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("新图名", text.Value);
        Assert.Equal(text.Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void SetText_BlockAttributeDefinition_Rejected()
    {
        var (s, block, _, _, adef, _, _, _) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = adef.Handle.ToString(), block = block.Name, text = "改默认"
        }));
        Assert.False(r.Ok);
        Assert.Equal("属性定义默认值编辑未支持", r.Error);
        Assert.Equal("JS-01", adef.Value);
    }

    [Fact]
    public void SetText_BlockInternalInsertAttribute_NotFound()
    {
        // 块内 Insert 的 ATTRIB 不是块定义直接实体（单层语义）：不得解析，防误改嵌套实例属性。
        var (s, block, _, _, _, _, _, _) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = EditBlockFixtures.NestedAttributeHandle.ToString(),
            block = block.Name, text = "改内属性"
        }));
        Assert.False(r.Ok);
        Assert.Contains("块定义中未找到句柄", r.Error);
    }

    [Fact]
    public void SetAttribute_BlockParam_Rejected()
    {
        var (s, block, _, _, _, nested, _, _) = New();
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            handle = nested.Attributes.First().Handle.ToString(),
            block = block.Name, value = "新值"
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内属性编辑未支持（Plan-3 仅文字/几何）", r.Error);
    }

    [Fact]
    public void SetAttribute_InsertTagFormWithBlock_Rejected()
    {
        // insert+tag 形态同样不接受 block（两形态一致拒绝，且失败先于变更）。
        var (s, block, _, _, _, nested, _, _) = New();
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = nested.Handle.ToString(), tag = "内属性", value = "新值", block = block.Name
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内属性编辑未支持（Plan-3 仅文字/几何）", r.Error);
        Assert.Equal("内值", nested.Attributes.First().Value);
    }

    // ---- 入口守卫 ----

    [Fact]
    public void BlockPath_UnknownBlock_RejectedAsUnknownBlockName()
    {
        // 未知块名与"已有块未命中句柄"必须区分：前者报未知块名。
        var (s, _, line, _, _, _, _, _) = New();
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = "不存在的块", dx = 1, dy = 1
        }));
        Assert.False(r.Ok);
        Assert.Equal("未知块名: 不存在的块", r.Error);
        Assert.DoesNotContain("块定义中未找到句柄", r.Error);
        Assert.Equal(0, line.StartPoint.X, 6); // 失败先于变更
    }

    [Fact]
    public void ChangeLayer_UnknownBlock_RejectedAsUnknownBlockName()
    {
        var (s, _, _, text, _, _, _, _) = New();
        string beforeLayer = text.Layer.Name;
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = "不存在的块", layer = "T"
        }));
        Assert.False(r.Ok);
        Assert.Equal("未知块名: 不存在的块", r.Error);
        Assert.Equal(beforeLayer, text.Layer.Name); // 失败先于变更
    }

    [Fact]
    public void SetText_UnknownBlock_RejectedAsUnknownBlockName()
    {
        var (s, _, _, text, _, _, _, _) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = "不存在的块", text = "改"
        }));
        Assert.False(r.Ok);
        Assert.Equal("未知块名: 不存在的块", r.Error);
        Assert.Equal("图名", text.Value);
    }

    [Fact]
    public void BlockPath_IllegalBlockParam_RejectedAsIllegalParam()
    {
        // 非字符串/非整数 block 值：专用"非法 block 参数"文案（不得误报未知块名）。
        var (s, _, line, _, _, _, _, _) = New();
        var r = new MoveEntityHandler().Apply(s,
            JsonSerializer.SerializeToElement(new { handle = line.Handle.ToString(), block = 1.5, dx = 1, dy = 1 }));
        Assert.False(r.Ok);
        Assert.StartsWith("非法 block 参数: 1.5", r.Error);
        Assert.Equal(0, line.StartPoint.X, 6);
    }

    [Theory]
    [InlineData(BlockTypeFlags.Anonymous)]
    [InlineData(BlockTypeFlags.XRef)]
    [InlineData(BlockTypeFlags.XRefOverlay)]
    [InlineData(BlockTypeFlags.XRefDependent)]
    public void BlockPath_ProtectedBlock_Rejected(BlockTypeFlags flag)
    {
        var (s, block, line, _, _, _, _, _) = New();
        BlockFixtures.Protected(block, flag);
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = block.Name
        }));
        Assert.False(r.Ok);
        Assert.Contains("块不可编辑", r.Error);
        Assert.Contains(block.Name, r.Error);
        Assert.Contains(line, block.Entities);
    }

    [Fact]
    public void BlockPath_DynamicBlock_Rejected()
    {
        var (s, block, _, text, _, _, _, _) = New();
        BlockFixtures.ForceDynamic(block);
        Assert.True(block.IsDynamic); // 反空断言：动态标志确已生效
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = block.Name, text = "改"
        }));
        Assert.False(r.Ok);
        Assert.Contains("块不可编辑", r.Error);
        Assert.Equal("图名", text.Value);
    }

    [Fact]
    public void BlockPath_UnloadedBlock_Rejected()
    {
        var (s, block, _, text, _, _, _, _) = New();
        block.IsUnloaded = true;
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = block.Name, layer = "T"
        }));
        Assert.False(r.Ok);
        Assert.Contains("块不可编辑", r.Error);
    }

    [Fact]
    public void BlockPath_BlockWithSpace_Rejected()
    {
        var (s, block, line, _, _, _, _, _) = New();
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = block.Name, dx = 1, dy = 1,
            space = "paper", layout = EditBlockFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Contains("block 不能与 space/layout/attribute", r.Error);
    }

    // 互斥优先于 space 通用预校验（block+space:"model" / space:"paper" 缺 layout 此前误报 space 文案）。
    [Fact]
    public void BlockPath_BlockWithModelSpace_RejectedAsConflict_NoMutation()
    {
        var (s, block, line, _, _, _, _, _) = New();
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = block.Name, dx = 1, dy = 1, space = "model"
        }));
        Assert.False(r.Ok);
        Assert.Contains("block 不能与 space/layout/attribute", r.Error);
        Assert.DoesNotContain("space 仅支持", r.Error);
        Assert.Equal(0, line.StartPoint.X, 6); // 失败先于变更
        Assert.Equal(10, line.EndPoint.X, 6);
    }

    [Fact]
    public void BlockPath_BlockWithPaperSpaceWithoutLayout_RejectedAsConflict()
    {
        var (s, block, line, _, _, _, _, _) = New();
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = block.Name, space = "paper"
        }));
        Assert.False(r.Ok);
        Assert.Contains("block 不能与 space/layout/attribute", r.Error);
        Assert.DoesNotContain("须配 layout", r.Error);
        Assert.Contains(line, block.Entities);
    }

    [Fact]
    public void BlockPath_BlockWithLayout_RejectedAsConflict()
    {
        var (s, block, _, text, _, _, _, _) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), block = block.Name, text = "改",
            layout = EditBlockFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Contains("block 不能与 space/layout/attribute", r.Error);
        Assert.Equal("图名", text.Value);
    }

    [Fact]
    public void BlockPath_HandleZero_Rejected()
    {
        var (s, block, line, _, _, _, _, _) = New();
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = 0, block = block.Name, dx = 1, dy = 1
        }));
        Assert.False(r.Ok);
        Assert.Contains("非法句柄", r.Error);
        Assert.Equal(0, line.StartPoint.X, 6);
    }

    [Fact]
    public void BlockPath_ModelHandleWithBlock_RejectedAsBlockMiss()
    {
        var (s, block, _, _, _, _, first, _) = New();
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = first.Handle.ToString(), block = block.Name
        }));
        Assert.False(r.Ok);
        Assert.Contains("块定义中未找到句柄", r.Error);
        Assert.Contains(block.Name, r.Error);
        Assert.Contains(first, s.Doc.Entities); // 模型实体未被删
    }

    [Theory]
    [InlineData("move-entity")]
    [InlineData("delete-entity")]
    [InlineData("change-layer")]
    [InlineData("set-text")]
    public void BlockPath_ViewportInBlock_Rejected(string kind)
    {
        var (s, block, _, _, _, _, _, _) = New();
        var vp = new Viewport
        {
            Center = new XYZ(0, 0, 0), Width = 10, Height = 10,
            ViewTarget = new XYZ(0, 0, 0), ViewHeight = 10
        };
        PaperFixtures.ForceHandle(vp, 7001);
        block.Entities.Add(vp);
        JsonElement p = kind switch
        {
            "move-entity" => P(new { handle = 7001, block = block.Name, dx = 1, dy = 1 }),
            "delete-entity" => P(new { handle = 7001, block = block.Name }),
            "change-layer" => P(new { handle = 7001, block = block.Name, layer = "T" }),
            _ => P(new { handle = 7001, block = block.Name, text = "改" })
        };
        StepResult r = kind switch
        {
            "move-entity" => new MoveEntityHandler().Apply(s, p),
            "delete-entity" => new DeleteEntityHandler().Apply(s, p),
            "change-layer" => new ChangeLayerHandler().Apply(s, p),
            _ => new SetTextHandler().Apply(s, p)
        };
        Assert.False(r.Ok);
        Assert.Equal("视口对象不支持编辑", r.Error);
        Assert.Contains(vp, block.Entities);
    }

    // ---- 默认（无 block）行为保持 ----

    [Fact]
    public void ModelLine_NoBlock_StillEditable()
    {
        var (s, _, _, _, _, _, _, _) = New();
        var modelLine = new Line { StartPoint = new XYZ(0, 50, 0), EndPoint = new XYZ(3, 50, 0) };
        s.Doc.Entities.Add(modelLine);
        PaperFixtures.ForceHandle(modelLine, 7002);
        var r = new MoveEntityHandler().Apply(s, P(new { handle = 7002, dx = 4, dy = 5 }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(4, modelLine.StartPoint.X, 6);
    }

    // ---- StepEngine 整合：审计 params 含 block，前后哈希不同 ----

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    [Fact]
    public void StepEngine_BlockMove_AuditsBlockParam_AndHashChanges()
    {
        var (s, block, line, _, _, _, _, _) = New();
        string auditPath = Path.Combine(_dir, "audit.db");
        using var audit = new SqliteAuditStore(auditPath);
        var engine = new StepEngine(audit, new SnapshotStore(Path.Combine(_dir, "snapshots")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new MoveEntityHandler() });
        string before = s.ComputeHash();
        var plan = StepPlanParser.Parse(JsonSerializer.Serialize(new
        {
            planId = "blk",
            steps = new[]
            {
                new
                {
                    kind = "move-entity",
                    @params = new { block = block.Name, handle = line.Handle.ToString(), dx = 3, dy = 4 }
                }
            }
        }));

        var result = engine.Run(plan, s, "s1");

        Assert.True(result.Ok, result.Error);
        var step = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "step");
        Assert.Contains(block.Name, step.Entry.ParamsJson);
        Assert.Equal(line.Handle.ToString(), step.Entry.Target); // 审计目标=实体句柄（块内句柄）
        Assert.NotEqual(step.Entry.BeforeHash, step.Entry.AfterHash);
        Assert.Equal(before, step.Entry.BeforeHash);
        Assert.Equal(3, line.StartPoint.X, 6);
    }
}
