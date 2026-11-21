using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3d T1：房间识别——房名模式+墙体 bbox 启发式；无墙默认区域显式 assumed；
/// Room 标注 GeometryInference 0.6（人工 map 升级 1.0）；扫描/摘要集成。
/// </summary>
public class RoomDetectorTests : IDisposable
{
    private readonly string _path;
    public RoomDetectorTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("WALL"));
        var wall = doc.Layers.First(l => l.Name == "WALL");
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(6000, 0), Layer = wall });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 4000), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(0, 4000), Layer = wall });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(6000, 0), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(3000, 2000), Value = "客厅", Height = 5 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(50000, 50000), Value = "卧室", Height = 5 });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Detect_RoomNames_BboxFromWalls_AssumedFallback()
    {
        var rooms = RoomDetector.Detect(DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal(2, rooms.Count);
        var living = rooms.First(r => r.Name == "客厅");
        Assert.Equal(6000, living.Width, 0);
        Assert.Equal(4000, living.Depth, 0);
        Assert.False(living.Assumed);
        var bed = rooms.First(r => r.Name == "卧室");
        Assert.True(bed.Assumed);
        Assert.Equal(6000, bed.Width, 0);
        Assert.Equal(6000, bed.Depth, 0);
    }

    [Fact]
    public void AnnotateRooms_RoomSemantics_GeometryInference()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        RoomDetector.AnnotateRooms(session, overlay);
        Assert.Equal(2, overlay.Annotations.Count(a => a.Class == SemanticClass.Room));
        Assert.All(overlay.Annotations.Where(a => a.Class == SemanticClass.Room),
            a => Assert.Equal(Provenance.GeometryInference, a.Provenance));
        Assert.All(overlay.Annotations.Where(a => a.Class == SemanticClass.Room),
            a => Assert.Equal(0.6, a.Confidence));
    }

    [Fact]
    public void AnnotateRooms_HumanConfirmed_NotDowngraded()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        string handle = session.Doc.Entities.OfType<TextEntity>().First(t => t.Value == "客厅").Handle.ToString();
        overlay.UpsertAnnotation(new SemanticAnnotation(handle, SemanticClass.Room, Provenance.HumanConfirmed, 1.0, "人工"));
        RoomDetector.AnnotateRooms(session, overlay);
        var a = overlay.Annotations.Single(x => x.TargetHandle == handle && x.Class == SemanticClass.Room);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
    }

    [Fact]
    public void Scan_IncludesRoomAnnotations()
    {
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Room);
    }

    [Fact]
    public void Summary_IncludesRooms()
    {
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), null);
        Assert.Contains("客厅", s);
        Assert.Contains("房间", s);
        Assert.Contains("assumed", s);
    }
}