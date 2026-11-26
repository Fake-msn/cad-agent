using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

/// <summary>
/// Plan 3a T1: ComputeHash must project Insert geometry (point/scale/rotation/block),
/// so Insert mutations are audit-visible. Pre-3a limitation: base fields only.
/// Fixture pattern per DxfFixture.AddLumBlock (2b-ii-b T9, roundtrip-proven).
/// </summary>
public class InsertHashProjectionTests : IDisposable
{
    private readonly string _path;
    public InsertHashProjectionTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var br = new BlockRecord("LUM");
        br.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(10, 0) });
        doc.BlockRecords.Add(br);
        doc.Entities.Add(new Insert(br) { InsertPoint = (XYZ)new XY(100, 100) });
    });
    public void Dispose() => File.Delete(_path);

    private static Insert FirstInsert(DrawingSession s) =>
        (Insert)s.Doc.Entities.First(e => e is Insert);

    [Fact]
    public void InsertMove_ChangesHash()
    {
        var s = DrawingSession.Load(_path);
        string before = s.ComputeHash();
        FirstInsert(s).InsertPoint = (XYZ)new XY(200, 100);
        s.InvalidateHash();
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void InsertScaleRotation_ChangesHash()
    {
        var s = DrawingSession.Load(_path);
        string before = s.ComputeHash();
        var ins = FirstInsert(s);
        ins.XScale = 2.0;
        s.InvalidateHash();
        Assert.NotEqual(before, s.ComputeHash());
        string mid = s.ComputeHash();
        ins.Rotation = Math.PI / 4;
        s.InvalidateHash();
        Assert.NotEqual(mid, s.ComputeHash());
    }

    [Fact]
    public void InsertHash_StableAcrossSaveReload()
    {
        var s = DrawingSession.Load(_path);
        string before = s.ComputeHash();
        s.SaveWorking();
        s.Reload();
        Assert.Equal(before, s.ComputeHash());
    }
}