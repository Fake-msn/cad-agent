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
public class DrawingSessionHashTests
{
    private static DrawingSession NewSession(Action<CadDocument> populate)
    {
        var doc = new CadDocument();
        populate(doc);
        return new DrawingSession(Path.Combine(Path.GetTempPath(), "hash-" + Guid.NewGuid().ToString("N") + ".dxf"), doc);
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
    public void Hash_IsStable_AcrossSaveAndReload()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var layout = new Layout("布局1");
            doc.Layouts.Add(layout);
            layout.AssociatedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(5, 0, 0) });
        });
        string h1 = DrawingSession.Load(path).ComputeHash();
        string h2 = DrawingSession.Load(path).ComputeHash();
        Assert.Equal(h1, h2);
    }
}
