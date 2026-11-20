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

    [Fact]
    public void Scan_PrunesAnnotations_ForDeletedEntities_KeepsMappings()
    {
        var session = DrawingSession.Load(_path);
        var first = SemanticScanner.Scan(session);
        var wallLine = session.Doc.Entities.First(e => e.Layer?.Name == "WALL");
        string deletedHandle = wallLine.Handle.ToString();
        Assert.Contains(first.Overlay.Annotations, a => a.TargetHandle == deletedHandle);
        // 图层级人工映射必须跨剪枝保留。
        first.Overlay.ConfirmMapping(new LayerMapping("随便", SemanticClass.Auxiliary, "u", DateTimeOffset.UnixEpoch));

        session.Doc.Entities.Remove(wallLine);
        session.SaveWorking();
        session.Reload();
        var second = SemanticScanner.Scan(session, first.Overlay);

        // 已删除实体的标注被剪枝；存活实体的标注保留（W-新墙 线的几何推断 Wall）。
        Assert.DoesNotContain(second.Overlay.Annotations, a => a.TargetHandle == deletedHandle);
        Assert.Contains(second.Overlay.Annotations, a => a.Class == SemanticClass.Wall);
        Assert.Contains(second.Overlay.Mappings, m => m.LayerName == "随便");
    }

    [Fact]
    public void Scan_PrunesPhantomHandle_GateNoLongerSatisfied()
    {
        // 无任何墙信号的图纸：幻影句柄的人工确认 Wall 不得满足操作级闸口。
        string plain = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("随便"));
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(10, 10), Layer = doc.Layers.First(l => l.Name == "随便") });
        });
        try
        {
            var session = DrawingSession.Load(plain);
            var overlay = new SemanticOverlay();
            overlay.UpsertAnnotation(new SemanticAnnotation("ghost", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "幻影实体"));
            // 剪枝前：幻影标注确实能满足闸口（漏洞成立的前提）。
            Assert.True(new CadAgent.Data.Semantic.SemanticGate(overlay).Check(new[] { SemanticClass.Wall }).Allowed);
            var result = SemanticScanner.Scan(session, overlay);
            Assert.DoesNotContain(result.Overlay.Annotations, a => a.TargetHandle == "ghost");
            var gate = new CadAgent.Data.Semantic.SemanticGate(result.Overlay);
            Assert.False(gate.Check(new[] { SemanticClass.Wall }).Allowed);
        }
        finally { File.Delete(plain); }
    }
}
