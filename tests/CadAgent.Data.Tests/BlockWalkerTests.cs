using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// T2：层名归一 + 循环守卫块遍历（底图完整性）。
/// 世界坐标经插入变换复合；句柄后缀 "{parent}:{i}" 与渲染器同 convention；
/// R3：Walk 对 Insert 先 yield 自身（供 T3 属性消费）再递归子实体。
/// </summary>
public class BlockWalkerTests
{
    // 合成嵌套夹具（镜像 SheetRegistryTests 模式）：
    // 块 B 含线 (0,0)-(10,0) 层 "x$0$A-WALL"；顶层 Insert B @ (100,200) scale 2 → 世界 (100,200)-(120,200)；
    // 块 C 含 Insert B @ (5,5)；顶层 Insert C @ (0,0) scale 2 → 孙线世界 (10,10)-(30,10)。
    private static (CadDocument Doc, Insert InsB, Insert InsC) NestedDoc()
    {
        var doc = new CadDocument();
        var wallLayer = new Layer("x$0$A-WALL");
        doc.Layers.Add(wallLayer);
        var b = new BlockRecord("B");
        b.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0), Layer = wallLayer });
        doc.BlockRecords.Add(b);
        var c = new BlockRecord("C");
        c.Entities.Add(new Insert(b) { InsertPoint = new XYZ(5, 5, 0) });
        doc.BlockRecords.Add(c);
        var insB = new Insert(b) { InsertPoint = new XYZ(100, 200, 0), XScale = 2, YScale = 2 };
        var insC = new Insert(c) { InsertPoint = new XYZ(0, 0, 0), XScale = 2, YScale = 2 };
        doc.Entities.Add(insB);
        doc.Entities.Add(insC);
        return (doc, insB, insC);
    }

    [Fact]
    public void Normalize_StripsBoundXrefChains()
    {
        Assert.Equal("A-WALL", CanonicalLayer.Normalize("2号厂房0917_t9$0$核心筒-楼梯大样$0$A-WALL"));
        Assert.Equal("E-WIRE", CanonicalLayer.Normalize("E-WIRE"));
        Assert.Equal("PUB_TEXT", CanonicalLayer.Normalize("x$0$PUB_TEXT"));
    }

    [Fact]
    public void Walk_NestedBlockEntities_YieldSuffixHandlesAndWorldCoords()
    {
        var (doc, insB, insC) = NestedDoc();
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        // 孙线（深度 2，两级后缀）：世界 (10,10)-(30,10)，层名 $0$ 链归一
        var deep = walked.First(w => w.Entity is Line && w.HandlePath == insC.Handle + ":0:0");
        Assert.Equal(10, deep.WorldX, 6);
        Assert.Equal(10, deep.WorldY, 6);
        Assert.Equal("A-WALL", deep.CanonicalLayer);
        Assert.Equal(new[] { (10.0, 10.0), (30.0, 10.0) }, deep.WorldPoints);
        // 直接子线：顶层 Insert B scale 2 → 世界 (100,200)-(120,200)
        var direct = walked.First(w => w.Entity is Line && w.HandlePath == insB.Handle + ":0");
        Assert.Equal(100, direct.WorldX, 6);
        Assert.Equal(200, direct.WorldY, 6);
        Assert.Equal(new[] { (100.0, 200.0), (120.0, 200.0) }, direct.WorldPoints);
    }

    [Fact]
    public void Walk_TopLevel_WorldEqualsLocal_PlainHandlePath()
    {
        // 行为保持：顶层 Walk 世界坐标=本地点，HandlePath=句柄本身
        var doc = new CadDocument();
        var line = new Line { StartPoint = new XYZ(3, 4, 0), EndPoint = new XYZ(5, 6, 0) };
        doc.Entities.Add(line);
        var w = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).Single();
        Assert.Equal(line.Handle.ToString(), w.HandlePath);
        Assert.Equal(3, w.WorldX, 6);
        Assert.Equal(4, w.WorldY, 6);
        Assert.Equal(new[] { (3.0, 4.0), (5.0, 6.0) }, w.WorldPoints);
    }

    [Fact]
    public void Walk_YieldsInsertItself_BeforeChildren()
    {
        // R3：Insert 自身先 yield（世界坐标=插入点经外层变换），供 T3 块属性消费
        var (doc, insB, insC) = NestedDoc();
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        Assert.Same(insB, walked[0].Entity);
        Assert.Equal(insB.Handle.ToString(), walked[0].HandlePath);
        Assert.Equal(100, walked[0].WorldX, 6);
        Assert.Equal(200, walked[0].WorldY, 6);
        // 嵌套 Insert 自身：C 内 Insert B 插入点 (5,5) × C scale 2 → 世界 (10,10)
        var nestedIns = walked.First(w => w.Entity is Insert && w.HandlePath == insC.Handle + ":0");
        Assert.Equal(10, nestedIns.WorldX, 6);
        Assert.Equal(10, nestedIns.WorldY, 6);
        // 自身先于子实体
        int selfIdx = walked.FindIndex(w => ReferenceEquals(w.Entity, insC));
        int childIdx = walked.FindIndex(w => w.HandlePath == insC.Handle + ":0");
        Assert.True(selfIdx < childIdx);
    }

    [Fact]
    public void Walk_SelfReferentialBlock_Terminates()
    {
        // 块 S 含 Insert S（自指）→ 块名访问栈守卫终止，不栈溢出。
        // 夹具注意：ACadSharp doc.Entities.Add 对"块内已含自指 Insert"的插入会触发
        // Insert.Clone→BlockRecord.Clone 无限递归（库自身缺陷）；先加空块插入再补自指
        // （块引用为活引用，Diag 探针核定），图结构仍真实自指。
        var doc = new CadDocument();
        var s = new BlockRecord("S");
        doc.BlockRecords.Add(s);
        doc.Entities.Add(new Insert(s));
        s.Entities.Add(new Insert(s));
        s.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0) });
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        Assert.True(walked.Count < 1000);
    }

    [Fact]
    public void Walk_MaxDepth_StopsExpansion()
    {
        // maxDepth=2：顶层 Insert C（深度1）展开一层；C 内 Insert B（深度2）只 yield 自身不再展开
        var (doc, _, insC) = NestedDoc();
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>(), maxDepth: 2).ToList();
        Assert.Contains(walked, w => w.HandlePath == insC.Handle + ":0" && w.Entity is Insert);
        Assert.DoesNotContain(walked, w => w.HandlePath == insC.Handle + ":0:0");
    }

    [Fact]
    public void Walk_NestedEntity_SheetIdByWorldPoint()
    {
        // 幅面归属：框内嵌套实体→框句柄；框外远端→null
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        var w = new BlockRecord("W");
        w.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.BlockRecords.Add(w);
        var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        frameIns.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
        doc.Entities.Add(frameIns);
        var inside = new Insert(w) { InsertPoint = new XYZ(100, 100, 0) };
        var outside = new Insert(w) { InsertPoint = new XYZ(5000, 5000, 0) };
        doc.Entities.Add(inside);
        doc.Entities.Add(outside);
        var sheets = SheetRegistryBuilder.Build(doc);
        var walked = BlockWalker.Walk(doc, sheets).ToList();
        Assert.Equal(frameIns.Handle.ToString(), walked.First(x => x.HandlePath == inside.Handle + ":0").SheetId);
        Assert.Null(walked.First(x => x.HandlePath == outside.Handle + ":0").SheetId);
    }

    [Fact]
    public void Detect_NestedWallPair_WorldCoords()
    {
        // 嵌套墙对世界坐标钉住：块内线对 d=200，Insert scale 1.5 → 世界 d=300 命中 [100,400]
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var w = new BlockRecord("W");
            w.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(2000, 0, 0) });
            w.Entities.Add(new Line { StartPoint = new XYZ(0, 200, 0), EndPoint = new XYZ(2000, 200, 0) });
            doc.BlockRecords.Add(w);
            doc.Entities.Add(new Insert(w) { InsertPoint = new XYZ(1000, 1000, 0), XScale = 1.5, YScale = 1.5 });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var overlay = new SemanticOverlay();
            WallPairDetector.Detect(session, overlay);
            string insHandle = session.Doc.Entities.OfType<Insert>().Single().Handle.ToString();
            var walls = overlay.Annotations.Where(a => a.Class == SemanticClass.Wall).ToList();
            Assert.Equal(2, walls.Count);
            Assert.Contains(walls, a => a.TargetHandle == insHandle + ":0");
            Assert.Contains(walls, a => a.TargetHandle == insHandle + ":1");
            Assert.All(walls, a => Assert.Contains("d=300", a.Evidence));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scan_BoundXrefWallLayer_ConventionHitThroughNormalization()
    {
        // 块内 LwPolyline 双线对层名 "x$0$WALL"：归一前约定库 Unknown（对照），
        // scan 后经归一命中 Wall（LayerInference 0.8），且不再进歧义清单。
        Assert.Equal(SemanticClass.Unknown, ConventionLibrary.ClassForLayer("x$0$WALL"));
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var layer = new Layer("x$0$WALL");
            doc.Layers.Add(layer);
            var w = new BlockRecord("W");
            var lw1 = new LwPolyline { Layer = layer };
            lw1.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
            lw1.Vertices.Add(new LwPolyline.Vertex(new XY(2000, 0)));
            var lw2 = new LwPolyline { Layer = layer };
            lw2.Vertices.Add(new LwPolyline.Vertex(new XY(0, 200)));
            lw2.Vertices.Add(new LwPolyline.Vertex(new XY(2000, 200)));
            w.Entities.Add(lw1);
            w.Entities.Add(lw2);
            doc.BlockRecords.Add(w);
            doc.Entities.Add(new Insert(w) { InsertPoint = new XYZ(0, 0, 0) });
        });
        try
        {
            var result = SemanticScanner.Scan(DrawingSession.Load(path));
            var nestedWalls = result.Overlay.Annotations
                .Where(a => a.Class == SemanticClass.Wall && a.TargetHandle.Contains(':')).ToList();
            Assert.Equal(2, nestedWalls.Count);
            Assert.All(nestedWalls, a => Assert.Equal(Provenance.LayerInference, a.Provenance));
            Assert.DoesNotContain("x$0$WALL", result.Ambiguous.Select(a => a.LayerName));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Detect_NestedAxisAndDoor_WorldRadiusScaled()
    {
        // 世界半径钉住：圆 r=60（本地 <minRadius 100）× scale 2 → 120 命中轴网；
        // 门弧 r=450（本地 <minRadius 500）× scale 2 → 900 命中门弧。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var b = new BlockRecord("AX");
            b.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 60 });
            b.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Value = "1", Height = 30 });
            b.Entities.Add(new Arc { Center = new XYZ(3000, 0, 0), Radius = 450, StartAngle = 0, EndAngle = Math.PI / 2 });
            doc.BlockRecords.Add(b);
            doc.Entities.Add(new Insert(b) { InsertPoint = new XYZ(5000, 5000, 0), XScale = 2, YScale = 2 });
        });
        try
        {
            var session = DrawingSession.Load(path);
            string insHandle = session.Doc.Entities.OfType<Insert>().Single().Handle.ToString();
            var overlay = new SemanticOverlay();
            AxisDetector.Detect(session, overlay);
            var axis = overlay.Annotations.Where(a => a.Class == SemanticClass.Axis).ToList();
            Assert.Equal(2, axis.Count); // 圆+文本（后缀句柄）
            Assert.Contains(axis, a => a.TargetHandle == insHandle + ":0");
            Assert.Contains(axis, a => a.TargetHandle == insHandle + ":1");
            Assert.All(axis, a => Assert.Contains("r=120", a.Evidence));
            DoorArcDetector.Detect(session, overlay);
            var door = overlay.Annotations.Single(a => a.Class == SemanticClass.Door);
            Assert.Equal(insHandle + ":2", door.TargetHandle);
            Assert.Contains("r=900", door.Evidence);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Detect_NestedRoom_WorldBboxAndCanonicalWallLayer()
    {
        // 嵌套房间：墙线层 "x$0$WALL" 归一命中约定；bbox/锚点用世界坐标
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var wallLayer = new Layer("x$0$WALL");
            doc.Layers.Add(wallLayer);
            var b = new BlockRecord("ROOM");
            b.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(6000, 0, 0), Layer = wallLayer });
            b.Entities.Add(new Line { StartPoint = new XYZ(0, 4000, 0), EndPoint = new XYZ(6000, 4000, 0), Layer = wallLayer });
            b.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(0, 4000, 0), Layer = wallLayer });
            b.Entities.Add(new Line { StartPoint = new XYZ(6000, 0, 0), EndPoint = new XYZ(6000, 4000, 0), Layer = wallLayer });
            b.Entities.Add(new TextEntity { InsertPoint = new XYZ(3000, 2000, 0), Value = "客厅", Height = 300 });
            doc.BlockRecords.Add(b);
            doc.Entities.Add(new Insert(b) { InsertPoint = new XYZ(10000, 10000, 0) });
        });
        try
        {
            var session = DrawingSession.Load(path);
            var rooms = RoomDetector.Detect(session, new SemanticOverlay());
            var room = Assert.Single(rooms);
            Assert.Equal("客厅", room.Name);
            Assert.Equal(13000, room.X, 6);
            Assert.Equal(12000, room.Y, 6);
            Assert.Equal(6000, room.Width, 6);
            Assert.Equal(4000, room.Depth, 6);
            Assert.False(room.Assumed);
            var overlay = new SemanticOverlay();
            RoomDetector.AnnotateRooms(session, overlay);
            string insHandle = session.Doc.Entities.OfType<Insert>().Single().Handle.ToString();
            Assert.Contains(overlay.Annotations, a => a.Class == SemanticClass.Room && a.TargetHandle == insHandle + ":4");
        }
        finally { File.Delete(path); }
    }
}
