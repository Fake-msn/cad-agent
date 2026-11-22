using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// T4：语义按幅面作用域 + 闸口防跨幅跑偏。
/// 三参 CheckContext(required, targetHandle, sheetId)：sheetId 非 null 时标注须
/// SheetId==sheetId（同幅）或 SheetId==null（legacy 全局）且置信≥min；
/// targetHandle null → 操作级同幅过滤；两参/无 sheetId 路径行为保持（legacy）。
/// 检测器/推断产出经 WalkedEntity.SheetId stamp；跨幅拒绝审计留痕（spec §9 无旁路）。
/// </summary>
public class SheetScopeGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sheetscope-{Guid.NewGuid():N}");
    private readonly SqliteAuditStore _audit;

    public SheetScopeGateTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new SqliteAuditStore(Path.Combine(_dir, "a.db"));
    }
    public void Dispose()
    {
        _audit.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    // ---------- 纯 overlay 闸口（三参作用域） ----------

    [Fact]
    public void CheckContext_SheetAConfirmed_SheetBTarget_Refused()
    {
        // hA（S1）Lighting 1.0 已确认；hB（S2 内实体）无标注
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("hA", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工", "S1"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "hB", "S2").Allowed); // 跨幅跑偏拒绝
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, "hA", "S1").Allowed);  // 同幅放行
        // 跑偏核心：hA 的确认属 S1，操作声明幅面 S2 → 不得借用放行
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "hA", "S2").Allowed);
    }

    [Fact]
    public void CheckContext_LegacyNullSheet_GlobalAnnotationPasses()
    {
        // 旧 overlay 无 SheetId（legacy 全局）→ 三参 sheetId 非 null 时仍放行
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, "h1", "S2").Allowed);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null, "S2").Allowed); // 操作级同幅过滤亦放行全局标注
    }

    [Fact]
    public void CheckContext_SameSheet_LowConfidence_Refused()
    {
        // 同幅但置信 0.5 < 0.8 默认阈值 → 拒绝（诚实降级不变）
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Lighting, Provenance.GeometryInference, 0.5, "推断", "S1"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "h1", "S1").Allowed);
    }

    [Fact]
    public void CheckContext_OperationLevel_SameSheetFilter()
    {
        // targetHandle null → 操作级同幅过滤：S1 确认不得放行 S2 操作
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("hA", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工", "S1"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, null, "S2").Allowed);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null, "S1").Allowed);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null, null).Allowed); // sheetId null=legacy 操作级
    }

    [Fact]
    public void CheckContext_NullSheetId_LegacyUnscoped()
    {
        // 三参 sheetId=null → 与两参完全一致（无作用域，legacy 行为保持）
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("hA", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工", "S1"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, "hA", null).Allowed);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "hB", null).Allowed);
    }

    [Fact]
    public void TwoArgOverload_DelegatesNullSheet_NoScope()
    {
        // 两参 override 委托三参 null：S1 stamp 标注在两参路径不受作用域限制
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("hA", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工", "S1"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, "hA").Allowed);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null).Allowed);
    }

    // ---------- StepEngine 传递（plan params sheetId 契约） ----------

    // 双幅面夹具：图框×2（S1 x∈[0,1000]，S2 x∈[2000,3000]）+ S1 内灯具块 E（A=深照型灯）
    private static CadDocument TwoSheetLightingDoc()
    {
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        for (int i = 0; i < 2; i++)
        {
            var ins = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(i * 2000 + 500, 650, 0), Value = i == 0 ? "一层电力平面图" : "二层电力平面图", Height = 30, Layer = new Layer("图框") });
        }
        var e = new BlockRecord("E");
        e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
        doc.BlockRecords.Add(e);
        var lum = new Insert(e) { InsertPoint = new XYZ(100, 100, 0) };
        lum.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
        doc.Entities.Add(lum);
        return doc;
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private (DrawingSession Session, SemanticOverlay Overlay, string S1, string S2) Fixture()
    {
        string path = Path.Combine(_dir, $"d-{Guid.NewGuid():N}.dxf");
        using (var fs = File.Create(path)) DxfWriter.Write(fs, TwoSheetLightingDoc());
        var session = DrawingSession.Load(path);
        var overlay = SemanticScanner.Scan(session).Overlay; // seeder stamp：S1 灯具 Lighting NativeObject 0.95
        var sheets = session.Sheets;
        return (session, overlay, sheets[0].SheetId, sheets[1].SheetId);
    }

    private StepEngine Engine(SemanticOverlay overlay) =>
        new(_audit, new SnapshotStore(Path.Combine(_dir, "s")),
            new FixedIdentity(new OperatorIdentity("T\\u", Role.Draw)), new RoleChecker(),
            new IStepHandler[] { new LabelLuminairesHandler() }, new CadAgent.Data.Semantic.SemanticGate(overlay));

    private static StepPlan Plan(string paramsJson) =>
        new("p1", new[] { new StepSpec("label-luminaires", JsonDocument.Parse(paramsJson).RootElement) });

    [Fact]
    public void StepEngine_PlanWithSheetId_CrossSheetRefusedAudited()
    {
        // S1 确认 Lighting（NativeObject 0.95 stamp S1）；plan 声明 sheetId=S2 → tx failed + 审计留痕
        var (session, overlay, _, s2) = Fixture();
        var result = Engine(overlay).Run(Plan($$"""{"prefix":"AL","block":"E","sheetId":"{{s2}}"}"""), session, "s1");
        Assert.False(result.Ok);
        Assert.Equal(0, result.StepsDone);
        Assert.Contains("Lighting", result.Error);
        var last = _audit.ReadAll()[^1];
        Assert.Equal("semantic-gate-refused", last.Entry.Kind);
        Assert.Contains(s2, last.Entry.ParamsJson); // 拒绝留痕含目标幅面
        Assert.True(_audit.VerifyChain(out _));
    }

    [Fact]
    public void StepEngine_PlanWithSheetId_SameSheetAllowed()
    {
        // 同幅（S1）声明 → 放行执行
        var (session, overlay, s1, _) = Fixture();
        var result = Engine(overlay).Run(Plan($$"""{"prefix":"AL","block":"E","sheetId":"{{s1}}"}"""), session, "s1");
        Assert.True(result.Ok);
        Assert.Equal(1, result.StepsDone);
    }

    [Fact]
    public void StepEngine_PlanWithoutSheetId_LegacyGlobalAllowed()
    {
        // 无 sheetId → 两参 legacy 路径：操作级全局检查放行（行为保持）
        var (session, overlay, _, _) = Fixture();
        var result = Engine(overlay).Run(Plan("""{"prefix":"AL","block":"E"}"""), session, "s1");
        Assert.True(result.Ok);
    }

    // ---------- 检测器/推断 stamp SheetId（经 WalkedEntity.SheetId） ----------

    [Fact]
    public void Scan_LayerAndGeometryAnnotations_StampSheetId()
    {
        // S2 内：WALL 约定线 / 墙对 / 轴号圆+文本 / 门弧 / 房名文本 → 各自标注 stamp S2；
        // 框外远端 WALL 线 → SheetId null（不混入任何幅面）
        var doc = TwoSheetLightingDoc();
        var wallLayer = new Layer("WALL");
        doc.Layers.Add(wallLayer);
        doc.Entities.Add(new Line { StartPoint = new XYZ(2100, 50, 0), EndPoint = new XYZ(2900, 50, 0), Layer = wallLayer });
        // 竖直墙对：x=2450/2550（距框竖边 >400，避免与图框线误配对），d=100 overlap=500
        doc.Entities.Add(new Line { StartPoint = new XYZ(2450, 100, 0), EndPoint = new XYZ(2450, 600, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(2550, 100, 0), EndPoint = new XYZ(2550, 600, 0) });
        var axisCircle = new Circle { Center = new XYZ(2500, 500, 0), Radius = 200 };
        doc.Entities.Add(axisCircle);
        var axisText = new TextEntity { InsertPoint = new XYZ(2500, 500, 0), Value = "1", Height = 100 };
        doc.Entities.Add(axisText);
        var doorArc = new Arc { Center = new XYZ(2200, 600, 0), Radius = 800, StartAngle = 0, EndAngle = Math.PI / 2 };
        doc.Entities.Add(doorArc);
        var roomText = new TextEntity { InsertPoint = new XYZ(2600, 620, 0), Value = "办公室", Height = 100 };
        doc.Entities.Add(roomText);
        var farWall = new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(5100, 5000, 0), Layer = wallLayer };
        doc.Entities.Add(farWall);

        string path = Path.Combine(_dir, $"stamp-{Guid.NewGuid():N}.dxf");
        using (var fs = File.Create(path)) DxfWriter.Write(fs, doc);
        var session = DrawingSession.Load(path);
        var overlay = SemanticScanner.Scan(session).Overlay;
        string s2 = session.Sheets[1].SheetId;

        string H(Entity e) => e.Handle.ToString();
        // 图层约定（LayerInference 0.8）
        Assert.Equal(s2, overlay.Annotations.Single(a => a.TargetHandle == H(doc.Entities.OfType<Line>().First(l => l.Layer?.Name == "WALL"))).SheetId);
        // 墙对（GeometryInference 0.6）
        var wallPair = overlay.Annotations.Where(a => a.Class == SemanticClass.Wall && a.Provenance == Provenance.GeometryInference).ToList();
        Assert.Equal(2, wallPair.Count);
        Assert.All(wallPair, a => Assert.Equal(s2, a.SheetId));
        // 轴号（圆+文本）
        Assert.Equal(s2, overlay.Annotations.Single(a => a.TargetHandle == H(axisCircle) && a.Class == SemanticClass.Axis).SheetId);
        Assert.Equal(s2, overlay.Annotations.Single(a => a.TargetHandle == H(axisText) && a.Class == SemanticClass.Axis).SheetId);
        // 门弧
        Assert.Equal(s2, overlay.Annotations.Single(a => a.TargetHandle == H(doorArc) && a.Class == SemanticClass.Door).SheetId);
        // 房名
        Assert.Equal(s2, overlay.Annotations.Single(a => a.TargetHandle == H(roomText) && a.Class == SemanticClass.Room).SheetId);
        // 框外远端实体 → null（legacy 全局口径）
        Assert.Null(overlay.Annotations.Single(a => a.TargetHandle == H(farWall)).SheetId);

        // 闸口联动：S1 灯具 Lighting（seeder stamp S1）不得放行 S2 操作级声明（跨幅跑偏）；
        // 框外 farWall 的 Wall 标注 SheetId=null 属 legacy 全局，各幅面均放行（兼容口径）
        var gate = new CadAgent.Data.Semantic.SemanticGate(overlay);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null, session.Sheets[0].SheetId).Allowed);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, null, s2).Allowed);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Wall }, null, session.Sheets[0].SheetId).Allowed);
    }

    [Fact]
    public void Scan_HumanMappingAnnotation_StampSheetId_MappingsStayGlobal()
    {
        // 人工映射派生标注经实体归属 stamp；Mappings 本身保持全局 null（层映射跨幅通用）
        var doc = TwoSheetLightingDoc();
        var wire = new Layer("E-WIRE");
        doc.Layers.Add(wire);
        doc.Entities.Add(new Line { StartPoint = new XYZ(2100, 100, 0), EndPoint = new XYZ(2200, 100, 0), Layer = wire });
        string path = Path.Combine(_dir, $"map-{Guid.NewGuid():N}.dxf");
        using (var fs = File.Create(path)) DxfWriter.Write(fs, doc);
        var session = DrawingSession.Load(path);
        var overlay = new SemanticOverlay();
        overlay.ConfirmMapping(new LayerMapping("E-WIRE", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        var result = SemanticScanner.Scan(session, overlay);
        var line = session.Doc.Entities.OfType<Line>().First(l => l.Layer?.Name == "E-WIRE");
        var a = result.Overlay.Annotations.Single(x => x.TargetHandle == line.Handle.ToString());
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
        Assert.Equal(session.Sheets[1].SheetId, a.SheetId);
        Assert.All(result.Overlay.Mappings, m => Assert.Null(m.SheetId));
    }
}
