using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// T3：块属性语义种子（NativeObject）。天正电气块 Insert.Attributes 携带构件语义
/// （A=深照型灯→Lighting 等），确定性原生数据 conf 0.95，先于图层/几何推断；
/// 未知值不猜；N/$TEXT$ 拼 Evidence 不成类；SheetId 经插入点归属（R6 字段前移）。
/// </summary>
public class BlockAttributeSeederTests
{
    // 合成夹具（镜像 SheetRegistryTests 模式）：图框块 院标准图框_A0（1000×700）+
    // 块 E 含圆 r=50；Insert E 属性 A=深照型灯 @ (100,100) 层 EQUIP-照明（约定库 Unknown）。
    private static CadDocument EquipDoc(string aValue = "深照型灯", string layerName = "EQUIP-照明")
    {
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        frameIns.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
        doc.Entities.Add(frameIns);
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(500, 650, 0), Value = "一层电力平面图", Height = 30, Layer = new Layer("图框") });

        var e = new BlockRecord("E");
        e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
        doc.BlockRecords.Add(e);
        var layer = new Layer(layerName);
        doc.Layers.Add(layer);
        var ins = new Insert(e) { InsertPoint = new XYZ(100, 100, 0), Layer = layer };
        ins.Attributes.Add(new AttributeEntity { Tag = "A", Value = aValue });
        doc.Entities.Add(ins);
        return doc;
    }

    private static string TempDxf(CadDocument doc)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-t3-{Guid.NewGuid():N}.dxf");
        using var fs = File.Create(path);
        ACadSharp.IO.DxfWriter.Write(fs, doc);
        return path;
    }

    [Fact]
    public void Seed_EquipAttributes_NativeObjectAnnotations()
    {
        // scan 后 overlay 含该句柄 Lighting 标注 conf 0.95 NativeObject，SheetId=图框句柄
        string path = TempDxf(EquipDoc());
        try
        {
            var session = DrawingSession.Load(path);
            var result = SemanticScanner.Scan(session);
            var ins = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
            var frameIns = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "院标准图框_A0");
            var a = Assert.Single(result.Overlay.Annotations.Where(x => x.TargetHandle == ins.Handle.ToString()));
            Assert.Equal(SemanticClass.Lighting, a.Class);
            Assert.Equal(Provenance.NativeObject, a.Provenance);
            Assert.Equal(0.95, a.Confidence);
            Assert.Contains("块属性 A=深照型灯", a.Evidence);
            Assert.Contains("(块 E)", a.Evidence);
            Assert.Equal(frameIns.Handle.ToString(), a.SheetId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Seed_UnknownAttributeValue_NoAnnotation()
    {
        // A=未知构件 → 不种子（诚实：不猜）；层 EQUIP-照明 亦无约定/几何信号
        string path = TempDxf(EquipDoc("未知构件"));
        try
        {
            var session = DrawingSession.Load(path);
            var result = SemanticScanner.Scan(session);
            var ins = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
            Assert.DoesNotContain(result.Overlay.Annotations, x => x.TargetHandle == ins.Handle.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("A", "深照型灯", SemanticClass.Lighting)]
    [InlineData("A", "荧光灯", SemanticClass.Lighting)]
    [InlineData("A", "屏、台、箱、柜", SemanticClass.Equipment)]
    [InlineData("A", "配电箱", SemanticClass.Equipment)]
    [InlineData("A", "控制柜", SemanticClass.Equipment)]
    [InlineData("A", "双联单控开关", SemanticClass.Device)]
    [InlineData("A", "带保护极的单相暗敷插座", SemanticClass.Device)]
    [InlineData("A", "交流电动机", SemanticClass.Equipment)]
    [InlineData("A", "电机", SemanticClass.Equipment)]
    [InlineData("A", "未知构件", SemanticClass.Unknown)]
    [InlineData("A", "", SemanticClass.Unknown)]
    [InlineData("N", "4", SemanticClass.Unknown)]
    [InlineData("$TEXT$", "1C", SemanticClass.Unknown)]
    [InlineData("REMARK", "", SemanticClass.Unknown)]
    public void ClassForAttributeValue_Table(string tag, string value, SemanticClass expected) =>
        Assert.Equal(expected, AttributeSemantics.ClassForAttributeValue(tag, value));

    [Fact]
    public void Seed_NestedInsert_SuffixHandle_SheetId_AndEvidenceExtras()
    {
        // 嵌套 Insert（块 OUTER 内 Insert E @ (5,5)，顶层 OUTER scale 2 @ (100,100)）：
        // 世界插入点 (110,110) 在框内；HandlePath="{outer}:0"；N/$TEXT$ 拼入 Evidence 不单独成类。
        var doc = EquipDoc();
        var e = doc.BlockRecords.First(b => b.Name == "E");
        var outer = new BlockRecord("OUTER");
        var nested = new Insert(e) { InsertPoint = new XYZ(5, 5, 0) };
        nested.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
        nested.Attributes.Add(new AttributeEntity { Tag = "N", Value = "4" });
        nested.Attributes.Add(new AttributeEntity { Tag = "$TEXT$", Value = "1C" });
        outer.Entities.Add(nested);
        doc.BlockRecords.Add(outer);
        var outerIns = new Insert(outer) { InsertPoint = new XYZ(100, 100, 0), XScale = 2, YScale = 2 };
        doc.Entities.Add(outerIns);

        var sheets = SheetRegistryBuilder.Build(doc);
        var overlay = new SemanticOverlay();
        BlockAttributeSeeder.Seed(doc, overlay, sheets);

        var a = Assert.Single(overlay.Annotations.Where(x => x.TargetHandle == outerIns.Handle + ":0"));
        Assert.Equal(SemanticClass.Lighting, a.Class);
        Assert.Equal(Provenance.NativeObject, a.Provenance);
        Assert.Equal(0.95, a.Confidence);
        Assert.Contains("N=4", a.Evidence);
        Assert.Contains("$TEXT$=1C", a.Evidence);
        Assert.Equal(sheets[0].SheetId, a.SheetId);
        // 顶层 E 插入（A=深照型灯）亦种子：共 2 条 Lighting，无 N/$TEXT$ 单独标注
        Assert.Equal(2, overlay.Annotations.Count(x => x.Class == SemanticClass.Lighting));
        Assert.Equal(2, overlay.Annotations.Count);
    }

    [Fact]
    public void Seed_OnlyNTextAttributes_NoAnnotation()
    {
        // 仅 N/$TEXT$ 辅助属性（无 A）→ 不成类（诚实：不猜）
        var doc = new CadDocument();
        var b = new BlockRecord("M");
        b.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 10 });
        doc.BlockRecords.Add(b);
        var ins = new Insert(b) { InsertPoint = new XYZ(0, 0, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "N", Value = "4" });
        ins.Attributes.Add(new AttributeEntity { Tag = "$TEXT$", Value = "M" });
        doc.Entities.Add(ins);
        var overlay = new SemanticOverlay();
        BlockAttributeSeeder.Seed(doc, overlay, SheetRegistryBuilder.Build(doc));
        Assert.Empty(overlay.Annotations);
    }

    [Fact]
    public void Scan_NativeObject_CoexistsWithLayerInference_GatePasses()
    {
        // Insert 层 WALL + A=深照型灯：NativeObject Lighting 0.95 与 LayerInference Wall 0.8 共存
        // （Upsert 键=handle+provenance）；闸口 Any(conf≥min) 取高置信，Lighting 放行。
        string path = TempDxf(EquipDoc(layerName: "WALL"));
        try
        {
            var session = DrawingSession.Load(path);
            var result = SemanticScanner.Scan(session);
            var ins = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
            string h = ins.Handle.ToString();
            Assert.Contains(result.Overlay.Annotations, a => a.TargetHandle == h && a.Provenance == Provenance.NativeObject && a.Class == SemanticClass.Lighting);
            Assert.Contains(result.Overlay.Annotations, a => a.TargetHandle == h && a.Provenance == Provenance.LayerInference && a.Class == SemanticClass.Wall);
            var gate = new CadAgent.Data.Semantic.SemanticGate(result.Overlay);
            Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, h).Allowed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scan_HumanMapping_KeepsNativeObject()
    {
        // 人工映射覆盖清除陈旧推断，但不得删除 NativeObject（块属性原生数据，共存不删）
        string path = TempDxf(EquipDoc());
        try
        {
            var session = DrawingSession.Load(path);
            var overlay = new SemanticOverlay();
            overlay.ConfirmMapping(new LayerMapping("EQUIP-照明", SemanticClass.Auxiliary, "u", DateTimeOffset.UnixEpoch));
            var result = SemanticScanner.Scan(session, overlay);
            var ins = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
            string h = ins.Handle.ToString();
            Assert.Contains(result.Overlay.Annotations, a => a.TargetHandle == h && a.Provenance == Provenance.HumanConfirmed && a.Class == SemanticClass.Auxiliary);
            Assert.Contains(result.Overlay.Annotations, a => a.TargetHandle == h && a.Provenance == Provenance.NativeObject && a.Class == SemanticClass.Lighting);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Seed_SkipsHumanConfirmedHandle()
    {
        // 人工确认优先（与 LayerInference 同口径）：已 HumanConfirmed 的句柄不再种子
        var doc = EquipDoc();
        var ins = doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation(ins.Handle.ToString(), SemanticClass.Column, Provenance.HumanConfirmed, 1.0, "人工改判柱"));
        BlockAttributeSeeder.Seed(doc, overlay, SheetRegistryBuilder.Build(doc));
        var a = Assert.Single(overlay.Annotations.Where(x => x.TargetHandle == ins.Handle.ToString()));
        Assert.Equal(SemanticClass.Column, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
    }

    [Fact]
    public void Seed_NoAttributes_NoOp()
    {
        // 行为保持：无属性图纸 seeder 无操作
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) });
        var overlay = new SemanticOverlay();
        BlockAttributeSeeder.Seed(doc, overlay, SheetRegistryBuilder.Build(doc));
        Assert.Empty(overlay.Annotations);
    }

    [SkippableFact]
    public void RealDrawing_LuminaireCount_MatchesProbe()
    {
        // 真实标本探针钉住（outputs/api-probe/tarch-probe-out.txt）：
        // A=深照型灯×1364 → Lighting NativeObject == 1364；A=屏、台、箱、柜×137 + A=交流电动机×10 → Equipment ≥ 137。
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native\电施（强电）-2#厂房电力+普通照明平面图.dwg";
        Skip.If(!File.Exists(path), "真实图纸不在本机（仓库不携带），跳过");
        using var fs = File.OpenRead(path);
        var doc = new ACadSharp.IO.DwgReader(fs).Read();
        var overlay = new SemanticOverlay();
        BlockAttributeSeeder.Seed(doc, overlay, SheetRegistryBuilder.Build(doc));
        var native = overlay.Annotations.Where(a => a.Provenance == Provenance.NativeObject).ToList();
        Assert.Equal(1364, native.Count(a => a.Class == SemanticClass.Lighting));
        Assert.True(native.Count(a => a.Class == SemanticClass.Equipment) >= 137,
            $"Equipment 计数 {native.Count(a => a.Class == SemanticClass.Equipment)} 应 ≥137");
        Assert.All(native, a => Assert.Equal(0.95, a.Confidence));
    }
}
