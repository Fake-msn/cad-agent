using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Semantic;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class SkillAliasScanTests : IDisposable
{
    private readonly string _path;
    public SkillAliasScanTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("W-自定义"));
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0),
            Layer = doc.Layers.First(l => l.Name == "W-自定义") });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Scan_WithSkillAlias_AnnotatesAndRemovesAmbiguous()
    {
        var aliases = new Dictionary<string, SemanticClass>(StringComparer.OrdinalIgnoreCase)
        { ["W-自定义"] = SemanticClass.Wall };
        var result = SemanticScanner.Scan(DrawingSession.Load(_path), null, aliases);
        Assert.Empty(result.Ambiguous);
        var a = Assert.Single(result.Overlay.Annotations);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Contains("skill别名", a.Evidence);
    }

    [Fact]
    public void Scan_WithoutAlias_StillAmbiguous()
    {
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Contains("W-自定义", result.Ambiguous.Select(x => x.LayerName));
    }

    [Fact]
    public void Scan_HumanMappingBeatsSkillAlias()
    {
        var existing = new SemanticOverlay();
        existing.ConfirmMapping(new LayerMapping("W-自定义", SemanticClass.Column, "u", DateTimeOffset.UnixEpoch));
        var aliases = new Dictionary<string, SemanticClass> { ["W-自定义"] = SemanticClass.Wall };
        var result = SemanticScanner.Scan(DrawingSession.Load(_path), existing, aliases);
        var a = Assert.Single(result.Overlay.Annotations);
        Assert.Equal(SemanticClass.Column, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
    }
}
