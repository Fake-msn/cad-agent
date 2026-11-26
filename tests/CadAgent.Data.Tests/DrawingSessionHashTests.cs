using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// Task 5 (项2 承重): ComputeHash 投影必须覆盖纸面空间、块定义、插入属性值——
/// 否则纸面/块编辑后哈希不变，audit 会显示 "no change"。
/// </summary>
public class DrawingSessionHashTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (string f in _tempFiles)
        {
            try { File.Delete(f); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private DrawingSession NewSession(Action<CadDocument> populate)
    {
        var doc = new CadDocument();
        populate(doc);
        string path = Path.Combine(Path.GetTempPath(), "hash-" + Guid.NewGuid().ToString("N") + ".dxf");
        _tempFiles.Add(path);
        return new DrawingSession(path, doc);
    }

    [Fact]
    public void Hash_Changes_WhenPaperSpaceEntityMoves()
    {
        var s = NewSession(doc =>
        {
            var layout = new Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AssociatedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        });
        string before = s.ComputeHash();
        var line = s.Doc.Layouts.First(l => l.Name == "布局1").AssociatedBlock.Entities.OfType<Line>().First();
        line.StartPoint = new XYZ(5, 0, 0);
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void Hash_Changes_WhenBlockDefinitionEntityMoves()
    {
        var s = NewSession(doc =>
        {
            var b = new BlockRecord("图框");
            b.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            doc.BlockRecords.Add(b);
        });
        string before = s.ComputeHash();
        s.Doc.BlockRecords.First(b => b.Name == "图框").Entities.OfType<Line>().First()
            .StartPoint = new XYZ(1, 1, 0);
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void Hash_Changes_WhenInsertAttributeValueChanges()
    {
        var s = NewSession(doc =>
        {
            var b = new BlockRecord("图框");
            doc.BlockRecords.Add(b);
            var ins = new Insert(b) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "A" });
            doc.Entities.Add(ins);
        });
        string before = s.ComputeHash();
        s.Doc.Entities.OfType<Insert>().First().Attributes.First().Value = "B";
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void Hash_Changes_WhenInsertAttributeMoves()
    {
        var s = NewSession(doc =>
        {
            var b = new BlockRecord("图框");
            doc.BlockRecords.Add(b);
            var ins = new Insert(b) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "A" });
            doc.Entities.Add(ins);
        });
        string before = s.ComputeHash();
        s.Doc.Entities.OfType<Insert>().First().Attributes.First().InsertPoint = new XYZ(5, 5, 0);
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void Hash_Changes_WhenPaperSpaceInsertAttributeValueChanges()
    {
        var s = NewSession(doc =>
        {
            var b = new BlockRecord("图框");
            doc.BlockRecords.Add(b);
            var layout = new Layout("布局1");
            doc.Layouts.Add(layout);
            var ins = new Insert(b) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "A" });
            layout.AssociatedBlock.Entities.Add(ins);
        });
        string before = s.ComputeHash();
        s.Doc.Layouts.First(l => l.Name == "布局1").AssociatedBlock.Entities
            .OfType<Insert>().First().Attributes.First().Value = "B";
        Assert.NotEqual(before, s.ComputeHash());
    }

    /// <summary>
    /// 块内 AttributeDefinition（属性默认值/字高）是块定义内容：值改动必须可观测，
    /// 且钉住「ADEF 分支先于 TextEntity」这一继承链不变量（否则默认值被泛型文本行吞掉）。
    /// </summary>
    [Fact]
    public void Hash_Changes_WhenBlockAttributeDefinitionChanges()
    {
        var s = NewSession(doc =>
        {
            var b = new BlockRecord("图框");
            b.Entities.Add(new AttributeDefinition { Tag = "图名", Value = "默认" });
            doc.BlockRecords.Add(b);
        });
        string before = s.ComputeHash();
        var adef = s.Doc.BlockRecords.First(b => b.Name == "图框").Entities.OfType<AttributeDefinition>().First();
        adef.Value = "新默认";
        string afterValue = s.ComputeHash();
        Assert.NotEqual(before, afterValue);
        adef.Height = 3.5;
        Assert.NotEqual(afterValue, s.ComputeHash());
    }

    /// <summary>块定义内嵌套 Insert 的属性同样是块内容（B| 行内的 A| 子行）。</summary>
    [Fact]
    public void Hash_Changes_WhenAttributeOnInsertInsideBlockDefinitionChanges()
    {
        var s = NewSession(doc =>
        {
            var inner = new BlockRecord("子块");
            doc.BlockRecords.Add(inner);
            var frame = new BlockRecord("图框");
            var ins = new Insert(inner) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "A" });
            frame.Entities.Add(ins);
            doc.BlockRecords.Add(frame);
        });
        string before = s.ComputeHash();
        s.Doc.BlockRecords.First(b => b.Name == "图框").Entities
            .OfType<Insert>().First().Attributes.First().Value = "B";
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void Hash_IsStable_AcrossSaveWorkingReload_AndCaptureRestore()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var layout = new Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AddViewport(new Viewport { Center = new XYZ(500, 350, 0), Width = 1000, Height = 700, ViewHeight = 200 });
            layout.AssociatedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            var block = new BlockRecord("图框");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            var ins = new Insert(block) { InsertPoint = new XYZ(0, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "T", Value = "V" });
            doc.Entities.Add(ins);
        });
        _tempFiles.Add(path); // .dxf 源：working 即自身
        var session = DrawingSession.Load(path);
        string h0 = session.ComputeHash();
        session.SaveWorking();
        session.Reload();
        Assert.Equal(h0, session.ComputeHash()); // B|/P| 排除依赖 BlockRecord.Layout 跨 DXF 往返保持
        byte[] state = session.CaptureState();
        session.RestoreState(state);
        Assert.Equal(h0, session.ComputeHash()); // undo 快照同口径
    }
}
