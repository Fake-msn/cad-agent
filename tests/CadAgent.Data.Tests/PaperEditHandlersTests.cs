using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// Task 1：既有编辑处理器三空间化（模型/纸面/顶层 Insert 属性）。夹具复用
/// <see cref="PaperFixtures"/>（纸面布局 + 纸面线；new DrawingSession(path, doc) 直编同一对象图）。
/// 断言口径：目标空间被改且另一空间逐字不变；Viewport/block 参数 fail-closed。
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
    public void Move_BlockParam_RejectedAsPlan3()
    {
        var (s, layout) = New();
        var line = PaperLine(layout);
        var r = new MoveEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), dx = 1, dy = 1, block = "图框"
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内编辑将在后续切片提供（Plan-3）", r.Error);
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
    public void Delete_BlockParam_RejectedAsPlan3()
    {
        var (s, layout) = New();
        var line = PaperLine(layout);
        var r = new DeleteEntityHandler().Apply(s, P(new
        {
            handle = line.Handle.ToString(), block = "图框"
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内编辑将在后续切片提供（Plan-3）", r.Error);
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
}
