using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// Task 1：既有编辑处理器三空间化（模型/纸面/顶层 Insert 属性）。夹具复用
/// <see cref="PaperFixtures"/>（纸面布局 + 纸面线；new DrawingSession(path, doc) 直编同一对象图）。
/// 断言口径：目标空间被改且另一空间逐字不变；Viewport fail-closed；add-* 仍拒 block（块定义编辑见 BlockEditHandlerTests）。
/// </summary>
public class PaperEditHandlersTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private (DrawingSession S, ACadSharp.Objects.Layout Layout) New(Action<CadDocument>? extra = null)
    {
        var (s, layout, path) = PaperFixtures.PaperSession(extra);
        _paths.Add(path);
        return (s, layout);
    }

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o);

    private static Line PaperLine(ACadSharp.Objects.Layout layout) =>
        layout.AssociatedBlock!.Entities.OfType<Line>().Single();

    // ---- move-entity ----

    [Fact]
    public void Move_PaperLine_ExplicitSpaceLayout()
    {
        var (s, layout) = New();
        var line = PaperLine(layout);
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), dx = 5, dy = 7,
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(5, line.StartPoint.X, 6);
        Assert.Equal(7, line.StartPoint.Y, 6);
        Assert.Empty(s.Doc.Entities); // 模型空间未被写入
    }

    [Fact]
    public void Move_PaperLine_HandleOnly_AutoResolves()
    {
        var (s, layout) = New();
        var line = PaperLine(layout);
        var r = new MoveEntityHandler().Apply(s, P(new { handle = line.Handle.ToString(), dx = 5, dy = 7 }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(5, line.StartPoint.X, 6);
    }

    [Fact]
    public void Move_ModelLine_PaperLayoutPresent_StaysModel()
    {
        Line? modelLine = null;
        var (s, layout) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 3), EndPoint = new XYZ(10, 0, 3) };
            doc.Entities.Add(modelLine);
        });
        var paperLine = PaperLine(layout);
        double paperX = paperLine.StartPoint.X;
        var r = new MoveEntityHandler().Apply(s, P(new { handle = modelLine!.Handle.ToString(), dx = 1, dy = 2 }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, modelLine.StartPoint.X, 6);
        Assert.Equal(3, modelLine.StartPoint.Z, 6); // Z 高程保持（既有口径）
        Assert.Equal(paperX, paperLine.StartPoint.X, 6); // 纸面未被波及其他实体
    }

    [Fact]
    public void Move_Attribute_TranslatesBothAnchorPoints_ParentUntouched()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc));
        var (ip, ap) = (attr!.InsertPoint, attr.AlignmentPoint);
        var r = new MoveEntityHandler().Apply(s, P(new { handle = attr.Handle.ToString(), dx = 3, dy = 4 }));
        Assert.True(r.Ok, r.Error);
        // ATTRIB 渲染锚点按对齐方式二选一（InsertPoint/AlignmentPoint）：两点同施增量。
        Assert.Equal(ip.X + 3, attr.InsertPoint.X, 6);
        Assert.Equal(ip.Y + 4, attr.InsertPoint.Y, 6);
        Assert.Equal(ap.X + 3, attr.AlignmentPoint.X, 6);
        Assert.Equal(ap.Y + 4, attr.AlignmentPoint.Y, 6);
        Assert.Equal(10, insert!.InsertPoint.X, 6); // 父 Insert 不随属性移动
    }

    [Fact]
    public void Move_PaperAttribute_ResolvesInsertAttribute()
    {
        AttributeEntity? attr = null;
        var (s, layout) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc, inPaper: true));
        double before = attr!.InsertPoint.X;
        int paperCountBefore = layout.AssociatedBlock!.Entities.Count();
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = attr.Handle.ToString(), dx = 2, dy = 1,
            space = "paper", layout = PaperFixtures.LayoutName, attribute = true
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(before + 2, attr.InsertPoint.X, 6);
        Assert.Equal(paperCountBefore, layout.AssociatedBlock!.Entities.Count());
    }

    [Fact]
    public void Move_Viewport_Rejected()
    {
        var (s, layout) = New();
        var vp = layout.AssociatedBlock!.Entities.OfType<Viewport>().Single(v => !v.RepresentsPaper);
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = vp.Handle.ToString(), dx = 1, dy = 1,
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Equal("视口对象不支持编辑", r.Error);
    }

    [Fact]
    public void Move_UnknownBlockName_RejectedAsUnknownBlock()
    {
        // Plan-3 Task 5：block 已接通块定义；未知块名 → 专用"未知块名"文案（不回落到纸面实体）。
        var (s, layout) = New();
        var line = PaperLine(layout);
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), dx = 1, dy = 1, block = "不存在的块"
        }));
        Assert.False(r.Ok);
        Assert.Equal("未知块名: 不存在的块", r.Error);
        Assert.Equal(0, line.StartPoint.X, 6);
    }

    [Fact]
    public void Move_AmbiguousHandle_Rejects()
    {
        Line? modelLine = null;
        var (s, layout) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        PaperFixtures.ForceHandle(modelLine!, 4343);
        PaperFixtures.ForceHandle(PaperLine(layout), 4343);
        var r = new MoveEntityHandler().Apply(s, P(new { handle = "4343", dx = 1, dy = 1 }));
        Assert.False(r.Ok);
        Assert.Contains("句柄在多个空间出现", r.Error);
    }

    // ---- delete-entity ----

    [Fact]
    public void Delete_PaperLine_RemovesOnlyPaper()
    {
        Line? modelLine = null;
        var (s, layout) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        var paperLine = PaperLine(layout);
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = paperLine.Handle.ToString(), space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Empty(layout.AssociatedBlock!.Entities.OfType<Line>());
        Assert.Same(modelLine, Assert.Single(s.Doc.Entities)); // 模型空间未被删
    }

    [Fact]
    public void Delete_Attribute_RemovesFromInsertAttributes()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new DeleteEntityHandler().Apply(s, P(new { handle = attr!.Handle.ToString() }));
        Assert.True(r.Ok, r.Error);
        Assert.Empty(insert!.Attributes);
        Assert.Same(insert, Assert.Single(s.Doc.Entities)); // 父 Insert 保留
    }

    [Fact]
    public void Delete_PaperAttribute_RemovesFromPaperInsert()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, layout) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc, inPaper: true));
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = attr!.Handle.ToString(),
            space = "paper", layout = PaperFixtures.LayoutName, attribute = true
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Empty(insert!.Attributes);
        Assert.Contains(insert, layout.AssociatedBlock!.Entities);
    }

    [Fact]
    public void Delete_Viewport_Rejected()
    {
        var (s, layout) = New();
        var vp = layout.AssociatedBlock!.Entities.OfType<Viewport>().Single(v => !v.RepresentsPaper);
        var r = new DeleteEntityHandler().Apply(s, P(new { handle = vp.Handle.ToString() }));
        Assert.False(r.Ok);
        Assert.Equal("视口对象不支持编辑", r.Error);
    }

    [Fact]
    public void Delete_UnknownBlockName_RejectedAsUnknownBlock()
    {
        // Plan-3 Task 5：block 已接通块定义；未知块名 → "未知块名"文案，纸面实体不得删除。
        var (s, layout) = New();
        var line = PaperLine(layout);
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = "不存在的块"
        }));
        Assert.False(r.Ok);
        Assert.Equal("未知块名: 不存在的块", r.Error);
        Assert.Contains(line, layout.AssociatedBlock!.Entities);
    }

    [Fact]
    public void Delete_UnknownHandle_ReportsSearchedSpaces()
    {
        var (s, _) = New();
        var r = new DeleteEntityHandler().Apply(s, P(new { handle = "DEADBEEF" }));
        Assert.False(r.Ok);
        Assert.Contains("句柄不存在（已检索：模型/纸面/顶层属性）", r.Error);
    }

    // ---- change-layer ----

    [Fact]
    public void ChangeLayer_PaperEntity_SetsLayerOnlyThere()
    {
        Line? modelLine = null;
        var (s, layout) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        var paperLine = PaperLine(layout);
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = paperLine.Handle.ToString(), layer = "P-纸面",
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("P-纸面", paperLine.Layer.Name);
        Assert.Equal("0", modelLine!.Layer.Name); // 模型实体层不变
    }

    [Fact]
    public void ChangeLayer_Attribute_SetsLayer()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = attr!.Handle.ToString(), layer = "A-属性"
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("A-属性", attr.Layer.Name);
    }

    [Fact]
    public void ChangeLayer_Viewport_Rejected()
    {
        var (s, layout) = New();
        var vp = layout.AssociatedBlock!.Entities.OfType<Viewport>().Single(v => !v.RepresentsPaper);
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = vp.Handle.ToString(), layer = "0"
        }));
        Assert.False(r.Ok);
        Assert.Equal("视口对象不支持编辑", r.Error);
    }

    // ---- add-line / add-text 写纸面 ----

    [Fact]
    public void AddLine_PaperLayout_WritesIntoLayoutBlock()
    {
        var (s, layout) = New();
        var r = new AddLineHandler().Apply(s, P(new
        {
            x1 = 1, y1 = 2, x2 = 5, y2 = 6,
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, layout.AssociatedBlock!.Entities.OfType<Line>().Count());
        var added = layout.AssociatedBlock!.Entities.OfType<Line>().Last();
        Assert.Equal(5, added.EndPoint.X, 6);
        Assert.Empty(s.Doc.Entities); // 模型空间未被写入（空间隔离）
    }

    [Fact]
    public void AddText_PaperLayout_WritesIntoLayoutBlock()
    {
        var (s, layout) = New();
        var r = new AddTextHandler().Apply(s, P(new
        {
            x = 420, y = 297, text = "出图日期", height = 3.5,
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        var text = Assert.Single(layout.AssociatedBlock!.Entities.OfType<TextEntity>());
        Assert.Equal("出图日期", text.Value);
        Assert.Equal(420, text.InsertPoint.X, 6);
        Assert.Empty(s.Doc.Entities);
    }

    [Fact]
    public void AddLine_Default_StillWritesModel()
    {
        var (s, layout) = New();
        var r = new AddLineHandler().Apply(s, P(new { x1 = 1, y1 = 2, x2 = 5, y2 = 6 }));
        Assert.True(r.Ok, r.Error);
        Assert.Single(s.Doc.Entities);
        Assert.Single(layout.AssociatedBlock!.Entities.OfType<Line>()); // 纸面仍只有夹具线
    }

    [Fact]
    public void AddLine_PaperWithoutLayout_Fails()
    {
        var (s, _) = New();
        var r = new AddLineHandler().Apply(s, P(new
        {
            x1 = 1, y1 = 2, x2 = 5, y2 = 6, space = "paper"
        }));
        Assert.False(r.Ok);
        Assert.Contains("layout", r.Error);
    }

    [Fact]
    public void AddLine_UnknownLayout_Fails()
    {
        var (s, _) = New();
        var r = new AddLineHandler().Apply(s, P(new
        {
            x1 = 1, y1 = 2, x2 = 5, y2 = 6, space = "paper", layout = "不存在的布局"
        }));
        Assert.False(r.Ok);
        Assert.Contains("布局不存在", r.Error);
    }

    [Fact]
    public void AddLine_NonPaperLayout_Fails()
    {
        var (s, _) = New();
        string modelLayout = s.Doc.Layouts.First(l => !l.IsPaperSpace).Name;
        var r = new AddLineHandler().Apply(s, P(new
        {
            x1 = 1, y1 = 2, x2 = 5, y2 = 6, space = "paper", layout = modelLayout
        }));
        Assert.False(r.Ok);
        Assert.Contains("不是纸面空间", r.Error);
    }

    [Theory]
    [InlineData("add-line")]
    [InlineData("add-text")]
    public void Add_BlockParam_RejectedAsPlan3(string kind)
    {
        var (s, _) = New();
        var p = kind == "add-line"
            ? P(new { x1 = 1, y1 = 2, x2 = 5, y2 = 6, block = "图框" })
            : P(new { x = 1, y = 2, text = "T", height = 3, block = "图框" });
        var r = kind == "add-line"
            ? new AddLineHandler().Apply(s, p)
            : new AddTextHandler().Apply(s, p);
        Assert.False(r.Ok);
        Assert.Equal("块内编辑将在后续切片提供（Plan-3）", r.Error);
    }

    // ---- add-circle 纸面化 ----

    [Fact]
    public void AddCircle_PaperLayout_WritesIntoLayoutBlock()
    {
        var (s, layout) = New();
        var r = new AddCircleHandler().Apply(s, P(new
        {
            x = 3, y = 4, radius = 2, space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        var circle = Assert.Single(layout.AssociatedBlock!.Entities.OfType<Circle>());
        Assert.Equal(3, circle.Center.X, 6);
        Assert.Equal(2, circle.Radius, 6);
        Assert.Empty(s.Doc.Entities); // 空间隔离：模型未写入
    }

    [Fact]
    public void AddCircle_PaperWithoutLayout_Fails()
    {
        var (s, _) = New();
        var r = new AddCircleHandler().Apply(s, P(new { x = 3, y = 4, radius = 2, space = "paper" }));
        Assert.False(r.Ok);
        Assert.Contains("layout", r.Error);
    }

    [Fact]
    public void AddCircle_BlockParam_RejectedAsPlan3()
    {
        var (s, _) = New();
        var r = new AddCircleHandler().Apply(s, P(new
        {
            x = 3, y = 4, radius = 2, block = "图框"
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内编辑将在后续切片提供（Plan-3）", r.Error);
    }

    [Fact]
    public void AddCircle_Default_StillWritesModel()
    {
        var (s, layout) = New();
        var r = new AddCircleHandler().Apply(s, P(new { x = 3, y = 4, radius = 2 }));
        Assert.True(r.Ok, r.Error);
        Assert.Single(s.Doc.Entities.OfType<Circle>());
        Assert.Empty(layout.AssociatedBlock!.Entities.OfType<Circle>());
    }

    // ---- 布局隐含纸面（只给 layout、不写 space）：空间隔离硬约束钉在处理器+集合解析 ----

    private static JsonElement LayoutOnlyProbe(string kind) => kind switch
    {
        "add-line" => P(new { x1 = 1, y1 = 2, x2 = 5, y2 = 6, layout = PaperFixtures.LayoutName }),
        "add-text" => P(new { x = 420, y = 297, text = "纸面字", height = 3.5, layout = PaperFixtures.LayoutName }),
        "add-circle" => P(new { x = 3, y = 4, radius = 2, layout = PaperFixtures.LayoutName }),
        "insert-block" => P(new { block = "PBLK", x = 150, y = 300, layout = PaperFixtures.LayoutName }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static StepResult ApplyAdd(string kind, DrawingSession s, JsonElement p) => kind switch
    {
        "add-line" => new AddLineHandler().Apply(s, p),
        "add-text" => new AddTextHandler().Apply(s, p),
        "add-circle" => new AddCircleHandler().Apply(s, p),
        "insert-block" => new InsertBlockHandler().Apply(s, p),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>
    /// 只给 layout（省 space）必须等价 space:"paper"：实体落 layout.AssociatedBlock.Entities，
    /// Doc.Entities 计数不变（TryResolveAddCollection 语义经处理器端到端钉住，非仅 Resolver）。
    /// </summary>
    [Theory]
    [InlineData("add-line")]
    [InlineData("add-text")]
    [InlineData("add-circle")]
    [InlineData("insert-block")]
    public void Add_LayoutOnlyWithoutSpace_ImpliesPaper(string kind)
    {
        var (s, layout) = New(doc => doc.BlockRecords.Add(new BlockRecord("PBLK")));
        int modelBefore = s.Doc.Entities.Count;
        int paperBefore = layout.AssociatedBlock!.Entities.Count();
        var r = ApplyAdd(kind, s, LayoutOnlyProbe(kind));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(paperBefore + 1, layout.AssociatedBlock!.Entities.Count()); // 落纸面块
        Assert.Equal(modelBefore, s.Doc.Entities.Count); // 模型计数不变（空间隔离）
        Entity added = layout.AssociatedBlock!.Entities.Last();
        Assert.Equal(r.TargetHandle, added.Handle.ToString());
    }

    // ---- R3：space 预校验（全部接受 space 的处理器） ----

    /// <summary>各处理器最小合法参数（handle 用纸面实句柄：若未先拒，space 会被误读成未命中/成功）。</summary>
    private static JsonElement SpaceProbe(string kind, string space, ACadSharp.Objects.Layout layout) => kind switch
    {
        "add-line" => P(new { x1 = 1, y1 = 2, x2 = 3, y2 = 4, space, layout = PaperFixtures.LayoutName }),
        "add-text" => P(new { x = 1, y = 2, text = "T", height = 3, space, layout = PaperFixtures.LayoutName }),
        "add-circle" => P(new { x = 1, y = 2, radius = 3, space, layout = PaperFixtures.LayoutName }),
        "move-entity" => P(new { handle = PaperLine(layout).Handle.ToString(), dx = 1, dy = 1, space, layout = PaperFixtures.LayoutName }),
        "delete-entity" => P(new { handle = PaperLine(layout).Handle.ToString(), space, layout = PaperFixtures.LayoutName }),
        "change-layer" => P(new { handle = PaperLine(layout).Handle.ToString(), layer = "0", space, layout = PaperFixtures.LayoutName }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static StepResult ApplyKind(string kind, DrawingSession s, JsonElement p) => kind switch
    {
        "add-line" => new AddLineHandler().Apply(s, p),
        "add-text" => new AddTextHandler().Apply(s, p),
        "add-circle" => new AddCircleHandler().Apply(s, p),
        "move-entity" => new MoveEntityHandler().Apply(s, p),
        "delete-entity" => new DeleteEntityHandler().Apply(s, p),
        "change-layer" => new ChangeLayerHandler().Apply(s, p),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [InlineData("add-line", "foo")]
    [InlineData("add-line", "model")]
    [InlineData("add-text", "foo")]
    [InlineData("add-text", "model")]
    [InlineData("add-circle", "foo")]
    [InlineData("add-circle", "model")]
    [InlineData("move-entity", "foo")]
    [InlineData("move-entity", "model")]
    [InlineData("delete-entity", "foo")]
    [InlineData("delete-entity", "model")]
    [InlineData("change-layer", "foo")]
    [InlineData("change-layer", "model")]
    public void SpaceOtherThanPaper_RejectedAtEntry(string kind, string space)
    {
        var (s, layout) = New();
        var r = ApplyKind(kind, s, SpaceProbe(kind, space, layout));
        Assert.False(r.Ok);
        // R3：不得塌陷为"句柄不存在"，须为明确的 space 取值拒绝。
        Assert.Contains("仅支持 paper", r.Error);
    }

    // ---- 终审修复：space/layout/attribute 语义预校验文案（先于 Resolver，不得塌陷为"句柄不存在"） ----

    [Fact]
    public void Move_PaperWithoutLayout_ReportsLayoutRequired()
    {
        var (s, layout) = New();
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = PaperLine(layout).Handle.ToString(), dx = 1, dy = 1, space = "paper"
        }));
        Assert.False(r.Ok);
        Assert.Equal("space:\"paper\" 须配 layout", r.Error);
    }

    [Fact]
    public void Delete_PaperWithNonPaperLayout_ReportsLayoutName()
    {
        var (s, _) = New();
        string modelLayout = s.Doc.Layouts.First(l => !l.IsPaperSpace).Name;
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = "1234", space = "paper", layout = modelLayout
        }));
        Assert.False(r.Ok);
        Assert.Equal($"布局不存在或非纸面布局: {modelLayout}", r.Error);
    }

    [Fact]
    public void Delete_PaperWithUnknownLayout_ReportsLayoutName()
    {
        var (s, _) = New();
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = "1234", space = "paper", layout = "不存在的布局"
        }));
        Assert.False(r.Ok);
        Assert.Equal("布局不存在或非纸面布局: 不存在的布局", r.Error);
    }

    [Fact]
    public void ChangeLayer_AttributeNotTrue_ReportsExplicitMessage()
    {
        var (s, layout) = New();
        var r = new ChangeLayerHandler().Apply(s, P(new
        {
            handle = PaperLine(layout).Handle.ToString(), layer = "0", attribute = false
        }));
        Assert.False(r.Ok);
        Assert.Equal("attribute 仅支持 true", r.Error);
    }

    [Fact]
    public void SetText_PaperWithoutLayout_ReportsLayoutRequired()
    {
        var (s, layout) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = PaperLine(layout).Handle.ToString(), text = "改", space = "paper"
        }));
        Assert.False(r.Ok);
        Assert.Equal("space:\"paper\" 须配 layout", r.Error);
    }

    [Fact]
    public void SetText_AttributeNonBoolean_ReportsExplicitMessage()
    {
        var (s, layout) = New();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = PaperLine(layout).Handle.ToString(), text = "改", attribute = "true"
        }));
        Assert.False(r.Ok);
        Assert.Equal("attribute 仅支持 true", r.Error);
    }

    [Fact]
    public void Delete_PaperBoundaryViewport_Rejected()
    {
        // 纸边界视口（RepresentsPaper=true，Layout 构造时自动附加于块首）同样不可编辑。
        var (s, layout) = New();
        var vp = layout.AssociatedBlock!.Entities.OfType<Viewport>().Single(v => v.RepresentsPaper);
        Assert.NotEqual(0UL, vp.Handle); // 句柄 0 会与未写句柄对象混淆，先钉住夹具前提
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = vp.Handle.ToString(), space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Equal("视口对象不支持编辑", r.Error);
    }
}

/// <summary>
/// 契约文档不漂移：HandlerRegistry 注册的每个 kind 都必须在 HandlerKindDocs.Default
/// 有独立契约行（"&lt;kind&gt;:" 行首）。新增处理器漏写文档在此红灯。
/// </summary>
public class HandlerKindDocsTests
{
    [Fact]
    public void Default_DocumentsEveryRegisteredKind()
    {
        string[] lines = HandlerKindDocs.Default
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToArray();
        foreach (string kind in HandlerRegistry.All().Select(h => h.Kind))
            Assert.Contains(lines, l => l.StartsWith(kind + ":", StringComparison.Ordinal));
    }
}
