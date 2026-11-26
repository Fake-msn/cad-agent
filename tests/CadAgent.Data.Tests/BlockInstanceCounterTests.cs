using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Data.Blocks;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// Plan-3 T4：块实例计数（确认提示用，约值）。夹具经 DxfFixture 落盘回读
/// （计数对象总来自读回的文档，与真实调用链同源）。
/// 下探口径 = 渲染器 MaxInsertDepth=2：根 0、depth>=2 仍计数但不再深入
/// → 约值/下界，非渲染可见实例数（渲染器 depth>=2 跳绘）；纸面布局（IsPaperSpace）
/// 顶层同为根，布局关联块自身不作实例。
/// </summary>
public class BlockInstanceCounterTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private CadDocument Load(Action<CadDocument> populate)
    {
        string path = DxfFixture.CreateTempDxf(populate);
        _paths.Add(path);
        return DrawingSession.Load(path).Doc;
    }

    private static BlockRecord Block(CadDocument doc, string name, params Entity[] entities)
    {
        var block = new BlockRecord(name);
        foreach (var e in entities) block.Entities.Add(e);
        doc.BlockRecords.Add(block);
        return block;
    }

    private static Layout PaperLayout(CadDocument doc, string name)
    {
        var layout = new Layout(name);
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
        return layout;
    }

    [Fact]
    public void Count_TwoTopLevelInsertsOfSameBlock_ReturnsTwo()
    {
        var doc = Load(d =>
        {
            var b = Block(d, "B", new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            d.Entities.Add(new Insert(b) { InsertPoint = new XYZ(0, 0, 0) });
            d.Entities.Add(new Insert(b) { InsertPoint = new XYZ(100, 0, 0) });
        });

        var inserts = doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(2, inserts.Count);
        Assert.All(inserts, i => Assert.Equal("B", i.Block!.Name));
        Assert.NotEqual(inserts[0].Handle, inserts[1].Handle); // 两个独立实例
        Assert.Equal(2, BlockInstanceCounter.Count(doc, "B"));
    }

    [Fact]
    public void Count_NestedInsideOuterBlock_OneNestedPlusOneTopLevel()
    {
        var doc = Load(d =>
        {
            var b = Block(d, "B");
            var outer = Block(d, "OUTER", new Insert(b) { InsertPoint = new XYZ(5, 5, 0) });
            d.Entities.Add(new Insert(outer) { InsertPoint = new XYZ(0, 0, 0) });
            d.Entities.Add(new Insert(b) { InsertPoint = new XYZ(50, 0, 0) });
        });

        // 嵌套引用在块定义内（不在模型顶层遍历范围内，必须递归才可见）
        Assert.Single(doc.BlockRecords.Single(r => r.Name == "OUTER").Entities.OfType<Insert>());
        var top = doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(2, top.Count);
        Assert.Contains(top, i => i.Block!.Name == "OUTER");
        Assert.Contains(top, i => i.Block!.Name == "B");
        Assert.Equal(2, BlockInstanceCounter.Count(doc, "B"));
        Assert.Equal(1, BlockInstanceCounter.Count(doc, "OUTER"));
    }

    [Fact]
    public void Count_DepthGuard_CountsAtDepthTwoButDoesNotDescendFurther()
    {
        // 链 A→B→C→B（环）：模型 Insert(A) 为根。B 在深度 1 计入并下探至深度 2 的 C；
        // C 内的 Insert(B) 位于深度 3 → 守卫截断不计（约值口径）。
        var doc = Load(d =>
        {
            var a = new BlockRecord("A");
            var b = new BlockRecord("B");
            var c = new BlockRecord("C");
            d.BlockRecords.Add(a);
            d.BlockRecords.Add(b);
            d.BlockRecords.Add(c);
            // 先引用空块再补内容（ACadSharp 对含环块定义的 doc.Entities.Add 会深克隆递归，同 BlockWalkerTests 规避）
            d.Entities.Add(new Insert(a) { InsertPoint = new XYZ(0, 0, 0) });
            a.Entities.Add(new Insert(b) { InsertPoint = new XYZ(1, 0, 0) });
            b.Entities.Add(new Insert(c) { InsertPoint = new XYZ(2, 0, 0) });
            c.Entities.Add(new Insert(b) { InsertPoint = new XYZ(3, 0, 0) });
        });

        Assert.Equal(1, BlockInstanceCounter.Count(doc, "B")); // 仅 A 内一层；C 内 B 在深度 3 截断
        Assert.Equal(1, BlockInstanceCounter.Count(doc, "C")); // 深度 2 命中仍计数
        Assert.Equal(1, BlockInstanceCounter.Count(doc, "A"));
        Assert.Equal(0, BlockInstanceCounter.Count(doc, "C", maxDepth: 1)); // 深度守卫可收紧
    }

    [Fact]
    public void Count_MaxDepthTightened_TruncatesDeeperNesting_NoCycle()
    {
        // 无环链 A→B→C→D：默认深度 2 截断 D（深度 3）；maxDepth:3 时计入。
        var doc = Load(d =>
        {
            var a = Block(d, "A");
            var b = Block(d, "B");
            var c = Block(d, "C");
            var last = Block(d, "D");
            d.Entities.Add(new Insert(a) { InsertPoint = new XYZ(0, 0, 0) });
            a.Entities.Add(new Insert(b) { InsertPoint = new XYZ(1, 0, 0) });
            b.Entities.Add(new Insert(c) { InsertPoint = new XYZ(2, 0, 0) });
            c.Entities.Add(new Insert(last) { InsertPoint = new XYZ(3, 0, 0) });
        });

        Assert.Equal(1, BlockInstanceCounter.Count(doc, "B")); // 深度 1
        Assert.Equal(1, BlockInstanceCounter.Count(doc, "C")); // 深度 2：计数、不再深入
        Assert.Equal(0, BlockInstanceCounter.Count(doc, "D")); // 深度 3：默认截断
        Assert.Equal(1, BlockInstanceCounter.Count(doc, "D", maxDepth: 3));
    }

    [Fact]
    public void Count_NameMatching_IsCaseInsensitive_AndDegenerateInputsAreZero()
    {
        var doc = Load(d =>
        {
            var b = Block(d, "B");
            d.Entities.Add(new Insert(b) { InsertPoint = new XYZ(0, 0, 0) });
            d.Entities.Add(new Insert(b) { InsertPoint = new XYZ(10, 0, 0) });
        });

        Assert.Equal(2, BlockInstanceCounter.Count(doc, "b"));
        Assert.Equal(0, BlockInstanceCounter.Count(doc, "NOPE"));
        Assert.Equal(0, BlockInstanceCounter.Count(doc, ""));
        Assert.Equal(0, BlockInstanceCounter.Count(doc, null!));
        Assert.Equal(0, BlockInstanceCounter.Count(null, "B"));
    }

    [Fact]
    public void Count_PaperLayoutInsert_CountedAlongsideModel()
    {
        var doc = Load(d =>
        {
            var b = Block(d, "B");
            var layout = PaperLayout(d, "布局1");
            layout.AssociatedBlock!.Entities.Add(new Insert(b) { InsertPoint = new XYZ(10, 10, 0) });
            d.Entities.Add(new Insert(b) { InsertPoint = new XYZ(0, 0, 0) });
        });

        // 回读文档必带非纸面 model 布局且其关联块非空：IsPaperSpace 过滤为排除逻辑的承重前提
        var model = doc.Layouts.Single(l => !l.IsPaperSpace);
        Assert.NotNull(model.AssociatedBlock);
        Assert.Single(model.AssociatedBlock!.Entities.OfType<Insert>());

        var paper = doc.Layouts.Single(l => l.Name == "布局1" && l.IsPaperSpace);
        Assert.Single(paper.AssociatedBlock!.Entities.OfType<Insert>());
        Assert.Single(doc.Entities.OfType<Insert>());
        Assert.Equal(2, BlockInstanceCounter.Count(doc, "B")); // 含纸面一处，model 布局不双计
    }

    [Fact]
    public void Count_LayoutAssociatedBlock_IsNotTreatedAsInstance()
    {
        var doc = Load(d =>
        {
            var b = Block(d, "B");
            var layout = PaperLayout(d, "布局1");
            layout.AssociatedBlock!.Entities.Add(new Insert(b) { InsertPoint = new XYZ(10, 10, 0) });
        });

        var paper = doc.Layouts.Single(l => l.Name == "布局1" && l.IsPaperSpace);
        Assert.Equal("布局1", paper.AssociatedBlock!.Name); // 布局块确以布局名命名
        Assert.Equal(0, BlockInstanceCounter.Count(doc, "布局1")); // 布局块自身不作实例（防双计）
        Assert.Equal(1, BlockInstanceCounter.Count(doc, "B"));
    }
}
