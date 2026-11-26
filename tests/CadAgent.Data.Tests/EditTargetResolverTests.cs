using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 纸面/属性夹具（Task 1）：Layout + AddViewport（先 AddViewport 再取 AssociatedBlock，
/// 既有夹具口径）+ 纸面 Line。经 DxfWriter 落盘使内存对象获得句柄，再用
/// new DrawingSession(path, doc) 直接编辑同一对象图（DrawingSessionHashTests 先例）。
/// </summary>
internal static class PaperFixtures
{
    public const string LayoutName = "布局1";

    public static (DrawingSession Session, Layout Layout, string Path) PaperSession(
        Action<CadDocument>? extra = null)
    {
        var doc = new CadDocument();
        var layout = new Layout(LayoutName);
        doc.Layouts.Add(layout);
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
            StartPoint = new XYZ(0, 0, 0),
            EndPoint = new XYZ(10, 0, 0)
        });
        extra?.Invoke(doc);
        string path = Path.Combine(Path.GetTempPath(), "paper-" + Guid.NewGuid().ToString("N") + ".dxf");
        using (var fs = File.Create(path)) DxfWriter.Write(fs, doc);
        return (new DrawingSession(path, doc), layout, path);
    }

    /// <summary>顶层 Insert + 一条属性（构造法同 MoveEntityTests.Insert 分支）；inPaper 挂纸面块；
    /// handle 给定则强置 ATTRIB 句柄（脏图/正控制场景，落盘前生效）。</summary>
    public static (Insert Insert, AttributeEntity Attribute) AddAttributeInsert(
        CadDocument doc, bool inPaper = false, ulong? handle = null)
    {
        var block = new BlockRecord("TB_" + Guid.NewGuid().ToString("N")[..8]);
        doc.BlockRecords.Add(block);
        var insert = new Insert(block) { InsertPoint = new XYZ(10, 20, 0) };
        var attr = new AttributeEntity { Tag = "图名", Value = "栖贤雅庭" };
        attr.InsertPoint = new XYZ(10, 20, 0);
        attr.AlignmentPoint = new XYZ(12, 22, 0);
        if (handle is ulong forced) ForceHandle(attr, forced);
        insert.Attributes.Add(attr);
        if (inPaper)
            doc.Layouts.First(l => l.Name == LayoutName).AssociatedBlock!.Entities.Add(insert);
        else
            doc.Entities.Add(insert);
        return (insert, attr);
    }

    /// <summary>脏图模拟：ACadSharp 句柄 setter 非公有，经反射强置同句柄（多空间同句柄场景）。</summary>
    public static void ForceHandle(Entity e, ulong handle) =>
        typeof(Entity).GetProperty("Handle")!.GetSetMethod(nonPublic: true)!
            .Invoke(e, new object[] { handle });
}

/// <summary>
/// Task 1 Resolver 矩阵：默认检索序（模型→纸面→顶层属性）、显式限定 fail-closed、
/// 多空间同句柄拒绝、block 参数不检索块定义。
/// </summary>
public class EditTargetResolverTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private (DrawingSession S, Layout Layout) New(Action<CadDocument>? extra = null)
    {
        var (s, layout, path) = PaperFixtures.PaperSession(extra);
        _paths.Add(path);
        return (s, layout);
    }

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o);

    // ---- 默认检索序 ----

    [Fact]
    public void Resolve_PaperEntity_ByHandle_FindsLayout()
    {
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        var target = EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString() }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Paper, target!.Owner);
        Assert.Equal("布局1", target.LayoutName);
        Assert.Same(line, target.Entity);
    }

    [Fact]
    public void Resolve_ModelEntity_ByHandle_ReturnsModel()
    {
        Line? modelLine = null;
        var (s, _) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        var target = EditTargetResolver.Resolve(s, P(new { handle = modelLine!.Handle.ToString() }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Model, target!.Owner);
        Assert.Null(target.LayoutName);
        Assert.Same(modelLine, target.Entity);
    }

    [Fact]
    public void Resolve_EachHandleStaysInItsSpace()
    {
        // 兼容口径：仅 handle 时各句柄解析到自身空间（模型句柄→Model，纸面句柄→Paper，历史行为逐字不变）。
        Line? modelLine = null;
        var (s, layout) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        var paperLine = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        Assert.Equal(EditOwnerKind.Model,
            EditTargetResolver.Resolve(s, P(new { handle = modelLine!.Handle.ToString() }))!.Owner);
        Assert.Equal(EditOwnerKind.Paper,
            EditTargetResolver.Resolve(s, P(new { handle = paperLine.Handle.ToString() }))!.Owner);
    }

    [Fact]
    public void Resolve_ModelAttribute_DefaultFindsInsertAttribute()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc));
        var target = EditTargetResolver.Resolve(s, P(new { handle = attr!.Handle.ToString() }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.InsertAttribute, target!.Owner);
        Assert.Same(attr, target.Entity);
        Assert.Same(insert, target.AttributeOwner);
        Assert.Equal(insert!.Handle, target.InsertHandle);
        Assert.Null(target.LayoutName);
    }

    [Fact]
    public void Resolve_PaperAttribute_DefaultCarriesLayoutName()
    {
        AttributeEntity? attr = null;
        Insert? insert = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc, inPaper: true));
        var target = EditTargetResolver.Resolve(s, P(new { handle = attr!.Handle.ToString() }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.InsertAttribute, target!.Owner);
        Assert.Equal("布局1", target.LayoutName);
        Assert.Same(insert, target.AttributeOwner);
        Assert.Equal(insert!.Handle, target.InsertHandle);
    }

    [Fact]
    public void Resolve_AttributeInBlockDefinition_NotSearched()
    {
        // 默认不检索块定义（属性只编辑实例；块内属 Plan-3）——强置句柄 7777 保证可区分。
        AttributeEntity? nested = null;
        var (s, _) = New(doc =>
        {
            var inner = new BlockRecord("INNER");
            doc.BlockRecords.Add(inner);
            var outer = new BlockRecord("OUTER");
            var innerInsert = new Insert(inner);
            nested = new AttributeEntity { Tag = "T", Value = "V" };
            PaperFixtures.ForceHandle(nested, 7777);
            innerInsert.Attributes.Add(nested);
            outer.Entities.Add(innerInsert);
            doc.BlockRecords.Add(outer);
            doc.Entities.Add(new Insert(outer) { InsertPoint = new XYZ(0, 0, 0) });
        });
        Assert.NotNull(nested);
        // 反空断言：强置句柄必须真存活（否则 7777 未命中可能是"句柄本就不存在"而非"块定义未检索"）。
        Assert.Equal(7777UL, nested!.Handle);
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = "7777" })));
    }

    [Fact]
    public void Resolve_TopLevelForcedHandleAttribute_FindsInsertAttribute()
    {
        // 正控制：同一 7777 口径的顶层 Insert 属性必须解析到 InsertAttribute（否定不空转）。
        AttributeEntity? attr = null;
        Insert? insert = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc, handle: 7777UL));
        Assert.Equal(7777UL, attr!.Handle);
        var target = EditTargetResolver.Resolve(s, P(new { handle = "7777" }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.InsertAttribute, target!.Owner);
        Assert.Same(attr, target.Entity);
        Assert.Same(insert, target.AttributeOwner);
    }

    // ---- 显式限定（fail-closed，未命中不回退） ----

    [Fact]
    public void Resolve_PaperRestriction_ModelHandleNotFound()
    {
        Line? modelLine = null;
        var (s, _) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        var target = EditTargetResolver.Resolve(s, P(new
        {
            handle = modelLine!.Handle.ToString(),
            space = "paper",
            layout = PaperFixtures.LayoutName
        }));
        Assert.Null(target);
    }

    [Fact]
    public void Resolve_PaperRestriction_FindsPaperEntity()
    {
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        var target = EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            space = "paper",
            layout = PaperFixtures.LayoutName
        }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Paper, target!.Owner);
    }

    [Fact]
    public void Resolve_PaperLayoutAlone_ImpliesPaper()
    {
        // layout 仅纸面有语义：独给 layout 视同 space:"paper" 限定（add-line 与解析同口径）。
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        var target = EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            layout = PaperFixtures.LayoutName
        }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Paper, target!.Owner);
    }

    [Fact]
    public void Resolve_SpacePaperWithoutLayout_ReturnsNull()
    {
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            space = "paper"
        })));
    }

    [Fact]
    public void Resolve_UnknownLayout_ReturnsNull()
    {
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            space = "paper",
            layout = "不存在的布局"
        })));
    }

    [Fact]
    public void Resolve_NonPaperLayout_ReturnsNull()
    {
        // Model 布局（IsPaperSpace=false）不是纸面空间 → 限定 fail-closed（不回退模型顶层）。
        var (s, layout) = New();
        string modelLayout = s.Doc.Layouts.First(l => !l.IsPaperSpace).Name;
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            space = "paper",
            layout = modelLayout
        })));
    }

    [Fact]
    public void Resolve_SpaceNotPaper_ReturnsNull()
    {
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            space = "model",
            layout = PaperFixtures.LayoutName
        })));
    }

    [Fact]
    public void Resolve_AttributeOnly_FindsAttributeButNotEntity()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc));
        var byAttr = EditTargetResolver.Resolve(s, P(new
        {
            handle = attr!.Handle.ToString(),
            attribute = true
        }));
        Assert.Equal(EditOwnerKind.InsertAttribute, byAttr!.Owner);
        // attribute:true 只检索属性集合——父 Insert 自身不可命中。
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = insert!.Handle.ToString(),
            attribute = true
        })));
    }

    [Fact]
    public void Resolve_AttributeNotTrue_FailClosed()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = attr!.Handle.ToString(),
            attribute = false
        })));
    }

    [Fact]
    public void Resolve_PaperRestrictionWithAttribute_FindsPaperAttribute()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc, inPaper: true));
        var target = EditTargetResolver.Resolve(s, P(new
        {
            handle = attr!.Handle.ToString(),
            space = "paper",
            layout = PaperFixtures.LayoutName,
            attribute = true
        }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.InsertAttribute, target!.Owner);
        Assert.Equal("布局1", target.LayoutName);
    }

    // ---- 拒绝面 ----

    [Fact]
    public void Resolve_BlockParam_ReturnsNull()
    {
        // 块定义检索属 Plan-3：含 block 一律不解析（处理器入口先拒）。
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            block = "图框"
        })));
    }

    [Fact]
    public void Resolve_SameHandleInModelAndPaper_RejectsAmbiguous()
    {
        Line? modelLine = null;
        var (s, layout) = New(doc =>
        {
            modelLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) };
            doc.Entities.Add(modelLine);
        });
        var paperLine = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        PaperFixtures.ForceHandle(modelLine!, 4242);
        PaperFixtures.ForceHandle(paperLine, 4242);

        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = "4242" })));
        Assert.Null(EditTargetResolver.TryResolve(s, P(new { handle = "4242" }), out string? error));
        Assert.Contains("句柄在多个空间出现", error);
    }

    [Fact]
    public void Resolve_InvalidOrMissingHandle_ReturnsNull()
    {
        var (s, _) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = "not-a-number" })));
        Assert.Null(EditTargetResolver.Resolve(s, P(new { dx = 1 })));
    }

    [Fact]
    public void Resolve_PaperRestriction_ModelAttributeNotFound()
    {
        // 组合否定：纸面限定 + 属性检索，句柄来自模型空间属性 → 不回退（fail-closed）。
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = attr!.Handle.ToString(),
            space = "paper",
            layout = PaperFixtures.LayoutName,
            attribute = true
        })));
    }

    [Fact]
    public void Resolve_NumericJsonHandle_ResolvesEntity()
    {
        // handle 契约为 JSON 数字或十进制字符串：数字形态（ulong 序列化）同样可解析。
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        JsonElement p = JsonSerializer.SerializeToElement(new { handle = line.Handle });
        Assert.Equal(JsonValueKind.Number, p.GetProperty("handle").ValueKind);
        var target = EditTargetResolver.Resolve(s, p);
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Paper, target!.Owner);
        Assert.Same(line, target.Entity);
    }
}
