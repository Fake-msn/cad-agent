using CadAgent.Core;
using CadAgent.Data.Semantic;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class SemanticScannerTests : IDisposable
{
    private readonly string _path;
    public SemanticScannerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("WALL"));
        doc.Layers.Add(new Layer("W-新墙"));
        doc.Layers.Add(new Layer("随便"));
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = doc.Layers.First(l => l.Name == "WALL") });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 200), EndPoint = (XYZ)new XY(2000, 200), Layer = doc.Layers.First(l => l.Name == "W-新墙") });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(10, 10), Layer = doc.Layers.First(l => l.Name == "随便") });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Scan_ProducesAnnotations_AndAmbiguousList()
    {
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.LayerInference);
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.GeometryInference);
        var names = result.Ambiguous.Select(a => a.LayerName).ToList();
        Assert.Contains("W-新墙", names);
        Assert.Contains("随便", names);
        Assert.DoesNotContain("WALL", names);
        Assert.Equal(1, result.Ambiguous.First(a => a.LayerName == "随便").EntityCount);
    }

    [Fact]
    public void Scan_WithConfirmedMapping_NoLongerAmbiguous()
    {
        var existing = new SemanticOverlay();
        existing.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        var result = SemanticScanner.Scan(DrawingSession.Load(_path), existing);
        Assert.DoesNotContain("W-新墙", result.Ambiguous.Select(a => a.LayerName));
        Assert.Contains(result.Overlay.Annotations, a => a.Provenance == Provenance.HumanConfirmed);
    }
}