using CadAgent.Core;
using CadAgent.Data.Semantic;
using CSMath;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
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

    // ---- BF-T3：Scanner 集成（块级纠偏切片第三任务，spec §13.3）----

    /// <summary>临时 skill 包（镜像 BlockSymbolMatcherTests._pkg 构造）：symbols.yaml 根键 symbols:。</summary>
    private static string CreateSymbolPackage()
    {
        string pkg = Path.Combine(Path.GetTempPath(), $"bfscan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(pkg, "symbols"));
        File.WriteAllText(Path.Combine(pkg, "skill.yaml"), "name: test-pkg\nversion: \"1.0.0\"\n");
        File.WriteAllText(Path.Combine(pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        var sym = new CadDocument();
        sym.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        sym.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(pkg, "symbols", "sym.dxf")))
            DxfWriter.Write(fs, sym);
        return pkg;
    }

    [Fact]
    public void Scan_WithSymbolPackages_BlockMatchPopulated_AutoSeedInOverlay()
    {
        // 夹具：临时 skill 包 + 含克隆块文档（符号几何逐字克隆 ×4 实例，DXF 往返后指纹仍精确）。
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            var br = new BlockRecord("符号块");
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
            doc.BlockRecords.Add(br);
            for (int i = 0; i < 4; i++)
                doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 1000, 0, 0) });
        });
        try
        {
            var packages = new[] { SkillLoader.LoadDirectory(pkg) };
            var result = SemanticScanner.Scan(DrawingSession.Load(blockDxf), symbolPackages: packages);
            Assert.NotNull(result.BlockMatch);
            var match = result.BlockMatch!;
            var seed = Assert.Single(match.AutoSeeds);
            Assert.Equal("测试符号", seed.SymbolName);
            Assert.Equal(4, seed.Handles.Count);
            // 自动种子落 overlay：GeometryInference 0.9 + marker evidence；剪枝后句柄存活。
            var seeded = result.Overlay.Annotations.Where(a => a.Provenance == Provenance.GeometryInference
                && a.Evidence.StartsWith("几何指纹精确匹配:")).ToList();
            Assert.Equal(4, seeded.Count);
            Assert.All(seeded, a => Assert.Equal(0.9, a.Confidence));
            Assert.All(seed.Handles, h => Assert.Contains(result.Overlay.Annotations, a => a.TargetHandle == h));
            Assert.Empty(match.Suggestions);
        }
        finally { Directory.Delete(pkg, true); File.Delete(blockDxf); }
    }

    [Fact]
    public void Scan_WithoutPackages_BlockMatchNull_BehaviorUnchanged()
    {
        // 既有夹具：无包 → BlockMatch null；annotations/ambiguous 与旧口径逐字一致。
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Null(result.BlockMatch);
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.LayerInference);
        Assert.Contains(result.Overlay.Annotations, a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.GeometryInference);
        var names = result.Ambiguous.Select(a => a.LayerName).ToList();
        Assert.Contains("W-新墙", names);
        Assert.Contains("随便", names);
        Assert.DoesNotContain("WALL", names);
        Assert.Equal(1, result.Ambiguous.First(a => a.LayerName == "随便").EntityCount);
        // 空包列表与无包等价（Count>0 守卫——无索引不得误清既有种子）。
        var empty = SemanticScanner.Scan(DrawingSession.Load(_path), symbolPackages: Array.Empty<SkillPackage>());
        Assert.Null(empty.BlockMatch);
    }
}
