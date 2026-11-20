using CadAgent.Core;
using CadAgent.Data.Semantic;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class WallPairDetectorTests : IDisposable
{
    private static Line L(double x1, double y1, double x2, double y2) =>
        new() { StartPoint = (XYZ)new XY(x1, y1), EndPoint = (XYZ)new XY(x2, y2) };

    private readonly string _path;
    public WallPairDetectorTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(L(0, 0, 2000, 0));       // 墙线1
        doc.Entities.Add(L(0, 200, 2000, 200));   // 墙线2（d=200，重叠2000）
        doc.Entities.Add(L(0, 5000, 300, 5000));  // 短线（重叠300<500，不命中）
        doc.Entities.Add(L(0, 5100, 300, 5100));
        doc.Entities.Add(L(0, 0, 0, 2000));       // 垂直线（无同向对）
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void ParallelPair_WallThickness_AnnotatesBoth()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        WallPairDetector.Detect(session, overlay);
        var handles = session.Summarize().Entities.Select(e => e.Handle).ToList();
        var wallAnnotations = overlay.Annotations.Where(a => a.Class == SemanticClass.Wall).ToList();
        Assert.Equal(2, wallAnnotations.Count);
        Assert.All(wallAnnotations, a =>
        {
            Assert.Equal(Provenance.GeometryInference, a.Provenance);
            Assert.Equal(0.6, a.Confidence);
            Assert.Contains("d=200", a.Evidence);
        });
    }

    [Fact]
    public void ShortOverlap_NotAnnotated()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        WallPairDetector.Detect(session, overlay);
        Assert.Equal(2, overlay.Annotations.Count); // 只有那对长墙线
    }

    [Fact]
    public void HigherConfidenceExisting_NotOverwritten()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        string h = session.Summarize().Entities[0].Handle;
        overlay.UpsertAnnotation(new SemanticAnnotation(h, SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        WallPairDetector.Detect(session, overlay);
        Assert.Equal(1.0, overlay.Annotations.Single(a => a.TargetHandle == h && a.Provenance == Provenance.HumanConfirmed).Confidence);
    }
}