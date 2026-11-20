using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CSMath;

namespace CadAgent.Data.Tests;

public class ConventionLibraryTests : IDisposable
{
    private readonly string _path;
    public ConventionLibraryTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var wallLayer = new Layer("WALL");
        doc.Layers.Add(wallLayer);
        var customLayer = new Layer("W-新墙");
        doc.Layers.Add(customLayer);
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0), Layer = wallLayer });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 200), EndPoint = (XYZ)new XY(100, 200), Layer = customLayer });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void ClassForLayer_KnownNames_CaseInsensitive()
    {
        Assert.Equal(SemanticClass.Wall, ConventionLibrary.ClassForLayer("wall"));
        Assert.Equal(SemanticClass.Axis, ConventionLibrary.ClassForLayer("AXIS"));
        Assert.Equal(SemanticClass.Annotation, ConventionLibrary.ClassForLayer("PUB_DIM"));
        Assert.Equal(SemanticClass.Unknown, ConventionLibrary.ClassForLayer("随便画的"));
    }

    [Fact]
    public void HintForLayer_NameClues()
    {
        Assert.Contains("墙", ConventionLibrary.HintForLayer("W-新墙"));
        Assert.Equal("", ConventionLibrary.HintForLayer("xyz123"));
    }

    [Fact]
    public void LayerInference_AnnotatesKnownLayer_SkipsUnknown()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        LayerInference.Annotate(session, overlay);
        var wallEntity = session.Summarize().Entities.First(e => e.Layer == "WALL");
        var a = overlay.Annotations.Single(x => x.TargetHandle == wallEntity.Handle);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.LayerInference, a.Provenance);
        Assert.Equal(0.8, a.Confidence);
        Assert.DoesNotContain(overlay.Annotations, x => x.Evidence.Contains("W-新墙"));
    }

    [Fact]
    public void LayerInference_HumanMappingTakesPrecedence()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        overlay.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        LayerInference.Annotate(session, overlay);
        var custom = session.Summarize().Entities.First(e => e.Layer == "W-新墙");
        var a = overlay.Annotations.Single(x => x.TargetHandle == custom.Handle);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
        Assert.Equal(1.0, a.Confidence);
    }
}