using CadAgent.Core;
using CadAgent.Data.Semantic;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class AxisDoorDetectorTests : IDisposable
{
    private readonly string _path;
    public AxisDoorDetectorTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Circle { Center = (XYZ)new XY(0, 0), Radius = 400 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "1", Height = 300 });
        doc.Entities.Add(new Circle { Center = (XYZ)new XY(5000, 0), Radius = 400 }); // 无数字文本→不命中
        // ACadSharp 3.8.35 XML 文档核实：Arc.StartAngle/EndAngle 单位为弧度。
        doc.Entities.Add(new Arc { Center = (XYZ)new XY(1000, 0), Radius = 900, StartAngle = 0, EndAngle = Math.PI / 2 }); // 90°门弧
        doc.Entities.Add(new Arc { Center = (XYZ)new XY(2000, 0), Radius = 900, StartAngle = 0, EndAngle = Math.PI });     // 180°→不命中
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void AxisCircle_WithNumberText_AnnotatesBoth()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        AxisDetector.Detect(session, overlay);
        var axis = overlay.Annotations.Where(a => a.Class == SemanticClass.Axis).ToList();
        Assert.Equal(2, axis.Count); // 圆+文本
        Assert.All(axis, a => Assert.Contains("编号=1", a.Evidence));
    }

    [Fact]
    public void DoorArc_QuarterCircle_Annotates()
    {
        var session = DrawingSession.Load(_path);
        var overlay = new SemanticOverlay();
        DoorArcDetector.Detect(session, overlay);
        var doors = overlay.Annotations.Where(a => a.Class == SemanticClass.Door).ToList();
        Assert.Single(doors);
        Assert.Equal(0.5, doors[0].Confidence);
    }
}