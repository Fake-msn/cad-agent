using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 2: render model builder (DrawingSession -> flat RenderItem list).
/// Fixtures go through the real DXF write/load roundtrip (DxfFixture pattern),
/// so every asserted property is verified to survive DxfWriter/DxfReader.
/// ACadSharp 3.8.35 API verified via package XML docs + runtime reflection probe:
/// Insert.Block (BlockRecord), Insert.Rotation radians, Arc.StartAngle/EndAngle
/// radians, LwPolyline.Vertex.Location XY, Polyline2D.Vertices (Vertex2D, XYZ),
/// Color.Index short (256=ByLayer, -1=true color), BlockRecord.BlockEntity.BasePoint.
/// </summary>
public class RenderModelTests
{
    private static void WithSession(Action<CadDocument> populate, Action<DrawingSession> test)
    {
        string path = DxfFixture.CreateTempDxf(populate);
        try { test(DrawingSession.Load(path)); }
        finally { File.Delete(path); }
    }

    /// <summary>line + circle + arc + closed lwpolyline + text + insert(block SYM with one line).</summary>
    private static void Mixed(CadDocument doc)
    {
        var red = new Layer("RED") { Color = new Color(1) };
        doc.Layers.Add(red);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 20, 0), Layer = red });
        doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3 });
        doc.Entities.Add(new Arc(new XYZ(1, 2, 0), 4, Math.PI / 6, Math.PI / 3));
        var lw = new LwPolyline { IsClosed = true };
        lw.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
        lw.Vertices.Add(new LwPolyline.Vertex(new XY(4, 0)));
        lw.Vertices.Add(new LwPolyline.Vertex(new XY(4, 3)));
        doc.Entities.Add(lw);
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(7, 8, 0), Value = "HELLO", Height = 2.5, Rotation = Math.PI / 4 });
        var block = new BlockRecord("SYM");
        block.Entities.Add(new Line { StartPoint = new XYZ(-1, -1, 0), EndPoint = new XYZ(1, 1, 0) });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(100, 200, 0),
            XScale = 2,
            YScale = 2,
            ZScale = 1,
            Rotation = Math.PI / 2
        });
    }

    private static void AddSymBlock(CadDocument doc)
    {
        var block = new BlockRecord("SYM");
        block.Entities.Add(new Line { StartPoint = new XYZ(-1, -1, 0), EndPoint = new XYZ(1, 1, 0) });
        doc.BlockRecords.Add(block);
    }

    [Fact]
    public void Build_MixedFixture_CountsKindsAndOrder()
    {
        WithSession(Mixed, s =>
        {
            var items = RenderModelBuilder.Build(s, out int skipped);
            Assert.Equal(6, items.Count);
            Assert.Equal(0, skipped);
            Assert.Equal(
                new[] { RenderKind.Line, RenderKind.Circle, RenderKind.Arc, RenderKind.Polyline, RenderKind.Text, RenderKind.Line },
                items.Select(i => i.Kind).ToArray());
        });
    }

    [Fact]
    public void Build_ConvenienceOverload_MatchesOutParamOverload()
    {
        WithSession(Mixed, s =>
        {
            var a = RenderModelBuilder.Build(s);
            var b = RenderModelBuilder.Build(s, out _);
            // RenderItem.Points is a list (reference equality in record Equals),
            // so compare structurally instead of Assert.Equal on the records.
            Assert.Equal(b.Count, a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(b[i].Kind, a[i].Kind);
                Assert.Equal(b[i].Points, a[i].Points);
                Assert.Equal(b[i].Layer, a[i].Layer);
                Assert.Equal(b[i].ColorRgb, a[i].ColorRgb);
                Assert.Equal(b[i].Text, a[i].Text);
                Assert.Equal(b[i].TextHeight, a[i].TextHeight);
                Assert.Equal(b[i].RotationDeg, a[i].RotationDeg);
                Assert.Equal(b[i].Handle, a[i].Handle);
            }
        });
    }

    [Fact]
    public void Build_Line_TwoPointsAndLayer()
    {
        WithSession(Mixed, s =>
        {
            var line = RenderModelBuilder.Build(s)[0];
            Assert.Equal(RenderKind.Line, line.Kind);
            Assert.Equal(2, line.Points.Count);
            Assert.Equal(0, line.Points[0].X, 3);
            Assert.Equal(0, line.Points[0].Y, 3);
            Assert.Equal(10, line.Points[1].X, 3);
            Assert.Equal(20, line.Points[1].Y, 3);
            Assert.Equal("RED", line.Layer);
            Assert.Null(line.Text);
        });
    }

    [Fact]
    public void Build_Circle_CenterPlusRadiusConvention()
    {
        WithSession(Mixed, s =>
        {
            var circle = RenderModelBuilder.Build(s)[1];
            Assert.Equal(RenderKind.Circle, circle.Kind);
            Assert.Equal(2, circle.Points.Count);
            Assert.Equal(5, circle.Points[0].X, 3);
            Assert.Equal(5, circle.Points[0].Y, 3);
            Assert.Equal(3, circle.Points[1].X, 3);   // radius in Points[1].X
            Assert.Equal(0, circle.Points[1].Y, 3);
        });
    }

    [Fact]
    public void Build_Arc_CenterRadiusDegreesConvention()
    {
        WithSession(Mixed, s =>
        {
            var arc = RenderModelBuilder.Build(s)[2];
            Assert.Equal(RenderKind.Arc, arc.Kind);
            Assert.Equal(3, arc.Points.Count);
            Assert.Equal(1, arc.Points[0].X, 3);
            Assert.Equal(2, arc.Points[0].Y, 3);
            Assert.Equal(4, arc.Points[1].X, 3);      // radius
            Assert.Equal(30, arc.Points[1].Y, 3);     // start angle, degrees
            Assert.Equal(60, arc.Points[2].X, 3);     // end angle, degrees
            Assert.Equal(0, arc.Points[2].Y, 3);
        });
    }

    [Fact]
    public void Build_ClosedLwPolyline_AppendsFirstVertex()
    {
        WithSession(Mixed, s =>
        {
            var poly = RenderModelBuilder.Build(s)[3];
            Assert.Equal(RenderKind.Polyline, poly.Kind);
            Assert.Equal(4, poly.Points.Count);
            Assert.Equal((0.0, 0.0), poly.Points[0]);
            Assert.Equal((4.0, 0.0), poly.Points[1]);
            Assert.Equal((4.0, 3.0), poly.Points[2]);
            Assert.Equal(poly.Points[0], poly.Points[3]); // closed -> first appended
        });
    }

    [Fact]
    public void Build_Text_InsertPointValueHeightRotation()
    {
        WithSession(Mixed, s =>
        {
            var text = RenderModelBuilder.Build(s)[4];
            Assert.Equal(RenderKind.Text, text.Kind);
            Assert.Single(text.Points);
            Assert.Equal(7, text.Points[0].X, 3);
            Assert.Equal(8, text.Points[0].Y, 3);
            Assert.Equal("HELLO", text.Text);
            Assert.Equal(2.5, text.TextHeight, 3);
            Assert.Equal(45, text.RotationDeg, 3);
        });
    }

    [Fact]
    public void Build_Insert_ExpandsBlockLine_ScaleRotateTranslate()
    {
        // Block line (-1,-1)->(1,1); scale 2 -> (-2,-2)->(2,2);
        // rotation 90deg (R*(x,y)=(-y,x)) -> (2,-2)->(-2,2);
        // translate (100,200) -> (102,198)->(98,202). Scale BEFORE rotation.
        WithSession(Mixed, s =>
        {
            var expanded = RenderModelBuilder.Build(s)[5];
            Assert.Equal(RenderKind.Line, expanded.Kind);
            Assert.Equal(2, expanded.Points.Count);
            Assert.Equal(102, expanded.Points[0].X, 3);
            Assert.Equal(198, expanded.Points[0].Y, 3);
            Assert.Equal(98, expanded.Points[1].X, 3);
            Assert.Equal(202, expanded.Points[1].Y, 3);
            Assert.Equal("0", expanded.Layer);
        });
    }

    [Fact]
    public void Build_Insert_NonUniformScale_AxesIndependent()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("B");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0), XScale = 2, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(0, line.Points[0].X, 3);
            Assert.Equal(0, line.Points[0].Y, 3);
            Assert.Equal(2, line.Points[1].X, 3);
            Assert.Equal(1, line.Points[1].Y, 3);
        });
    }

    [Fact]
    public void Build_Insert_NonUniformScaleWithRotation_PinsRSOrder()
    {
        // Pins transform order R*S (scale BEFORE rotation) with a case where the
        // two orders differ: sx=2, sy=1, rotation=90deg, block line endpoint (1,1),
        // basePoint (0,0), insertPoint (0,0).
        //   R*S: S(1,1)=(2,1); R90(2,1)=(-1,2)  <- required
        //   S*R: R90(1,1)=(-1,1); S(-1,1)=(-2,1) <- must NOT happen
        // Uniform-scale rotation tests cannot distinguish the orders (commutative);
        // this non-uniform + rotation combination can.
        WithSession(doc =>
        {
            var block = new BlockRecord("RS");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(0, 0, 0),
                XScale = 2,
                YScale = 1,
                ZScale = 1,
                Rotation = Math.PI / 2
            });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Line, line.Kind);
            Assert.Equal(0, line.Points[0].X, 3);
            Assert.Equal(0, line.Points[0].Y, 3);
            Assert.Equal(-1, line.Points[1].X, 3);  // R*S -> (-1,2), NOT S*R -> (-2,1)
            Assert.Equal(2, line.Points[1].Y, 3);
        });
    }

    [Fact]
    public void Build_Insert_BlockBasePointSubtracted()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("BASE");
            block.BlockEntity.BasePoint = new XYZ(1, 1, 0);
            block.Entities.Add(new Line { StartPoint = new XYZ(1, 1, 0), EndPoint = new XYZ(2, 1, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(10, 10, 0), XScale = 1, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(10, line.Points[0].X, 3);
            Assert.Equal(10, line.Points[0].Y, 3);
            Assert.Equal(11, line.Points[1].X, 3);
            Assert.Equal(10, line.Points[1].Y, 3);
        });
    }

    [Fact]
    public void Build_Insert_TextRotationAddedHeightScaled()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("TXT");
            block.Entities.Add(new TextEntity { InsertPoint = new XYZ(1, 0, 0), Value = "T", Height = 2 });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(10, 10, 0),
                XScale = 2,
                YScale = 2,
                ZScale = 1,
                Rotation = Math.PI / 2
            });
        }, s =>
        {
            var text = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Text, text.Kind);
            // (1,0)*2=(2,0); rot90 -> (0,2); +(10,10) = (10,12)
            Assert.Equal(10, text.Points[0].X, 3);
            Assert.Equal(12, text.Points[0].Y, 3);
            Assert.Equal("T", text.Text);
            Assert.Equal(4, text.TextHeight, 3);      // height * |YScale|
            Assert.Equal(90, text.RotationDeg, 3);    // insert rotation added
        });
    }

    [Fact]
    public void Build_Insert_CircleCenterTransformedRadiusScaled()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("C");
            block.Entities.Add(new Circle { Center = new XYZ(1, 0, 0), Radius = 1 });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(100, 200, 0),
                XScale = 2,
                YScale = 2,
                ZScale = 1,
                Rotation = Math.PI / 2
            });
        }, s =>
        {
            var circle = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Circle, circle.Kind);
            Assert.Equal(100, circle.Points[0].X, 3);
            Assert.Equal(202, circle.Points[0].Y, 3);
            Assert.Equal(2, circle.Points[1].X, 3);   // radius * scale
            Assert.Equal(0, circle.Points[1].Y, 3);
        });
    }

    [Fact]
    public void Build_Insert_ArcAnglesShiftedByRotation()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("A");
            block.Entities.Add(new Arc(new XYZ(0, 0, 0), 1, 0, Math.PI / 2));
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(0, 0, 0),
                XScale = 1,
                YScale = 1,
                ZScale = 1,
                Rotation = Math.PI / 2
            });
        }, s =>
        {
            var arc = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Arc, arc.Kind);
            Assert.Equal(1, arc.Points[1].X, 3);
            Assert.Equal(90, arc.Points[1].Y, 3);     // 0deg + 90deg insert rotation
            Assert.Equal(180, arc.Points[2].X, 3);    // 90deg + 90deg
        });
    }

    [Fact]
    public void Build_NestedInsert_ExpandsOneLevel()
    {
        // Top Insert(NEST) -> NEST contains Insert(SYM) at (5,0) -> SYM line expands.
        WithSession(doc =>
        {
            AddSymBlock(doc);
            var nest = new BlockRecord("NEST");
            nest.Entities.Add(new Insert(doc.BlockRecords["SYM"]) { InsertPoint = new XYZ(5, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
            doc.BlockRecords.Add(nest);
            doc.Entities.Add(new Insert(nest) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s, out int skipped));
            Assert.Equal(0, skipped);
            Assert.Equal(RenderKind.Line, line.Kind);
            Assert.Equal(4, line.Points[0].X, 3);   // -1 + 5
            Assert.Equal(-1, line.Points[0].Y, 3);
            Assert.Equal(6, line.Points[1].X, 3);   // 1 + 5
            Assert.Equal(1, line.Points[1].Y, 3);
        });
    }

    [Fact]
    public void Build_NestedInsert_DepthGuardStopsThirdLevel()
    {
        // Insert(OUTER) -> Insert(NEST) -> Insert(SYM): third-level insert is NOT
        // expanded (depth guard); it is counted as skipped, no geometry emitted.
        WithSession(doc =>
        {
            AddSymBlock(doc);
            var nest = new BlockRecord("NEST");
            nest.Entities.Add(new Insert(doc.BlockRecords["SYM"]) { InsertPoint = new XYZ(5, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
            doc.BlockRecords.Add(nest);
            var outer = new BlockRecord("OUTER");
            outer.Entities.Add(new Insert(nest) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
            doc.BlockRecords.Add(outer);
            doc.Entities.Add(new Insert(outer) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s, out int skipped);
            Assert.Empty(items);
            Assert.Equal(1, skipped);
        });
    }

    [Fact]
    public void Build_LayerIndexColor_MappedToRgb()
    {
        WithSession(Mixed, s =>
        {
            var items = RenderModelBuilder.Build(s);
            Assert.Equal(0xFF0000u, items[0].ColorRgb);  // RED layer, ACI 1
            Assert.Equal(0xFFFFFFu, items[1].ColorRgb);  // layer "0", ACI 7 -> white (non-zero)
            Assert.NotEqual(0u, items[2].ColorRgb);
        });
    }

    [Fact]
    public void Build_LayerIndexColor30_FullAciTableOrange()
    {
        // ACI 30 is outside the old first-10 table (used to fall back to white);
        // Color.GetIndexRGB covers the full 256-entry ACI table -> orange 0xFF7F00.
        WithSession(doc =>
        {
            var orange = new Layer("ORANGE") { Color = new Color(30) };
            doc.Layers.Add(orange);
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0), Layer = orange });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(0xFF7F00u, line.ColorRgb);
        });
    }

    [Fact]
    public void Build_TrueColorLayer_DirectRgb()
    {
        WithSession(doc =>
        {
            var tc = new Layer("TC") { Color = new Color(10, 20, 30) };
            doc.Layers.Add(tc);
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0), Layer = tc });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(0x0A141Eu, line.ColorRgb);
        });
    }

    [Fact]
    public void Build_ExplicitEntityColor_OverridesLayer()
    {
        WithSession(doc =>
        {
            var red = new Layer("RED") { Color = new Color(1) };
            doc.Layers.Add(red);
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0), Layer = red, Color = new Color(3) });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(0x00FF00u, line.ColorRgb); // ACI 3 green wins over RED layer
        });
    }

    [Fact]
    public void Build_ByBlockChild_InheritsInsertColor()
    {
        WithSession(doc =>
        {
            var block = new BlockRecord("B");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0), Color = Color.ByBlock });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1, Color = new Color(1) });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(0xFF0000u, line.ColorRgb);
        });
    }

    [Fact]
    public void Build_UnsupportedEntity_SkippedAndCounted()
    {
        WithSession(doc =>
        {
            Mixed(doc);
            doc.Entities.Add(new Point { Location = new XYZ(50, 60, 0) }); // known to ACadSharp, unsupported by builder
        }, s =>
        {
            var items = RenderModelBuilder.Build(s, out int skipped);
            Assert.Equal(6, items.Count);
            Assert.Equal(1, skipped);
        });
    }

    [Fact]
    public void Build_MText_PlainTextAsTextItem()
    {
        WithSession(doc =>
        {
            doc.Entities.Add(new MText { InsertPoint = new XYZ(9, 9, 0), Value = "MT", Height = 3 });
        }, s =>
        {
            var text = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Text, text.Kind);
            Assert.Equal("MT", text.Text);
            Assert.Equal(3, text.TextHeight, 3);
            Assert.Equal(9, text.Points[0].X, 3);
            Assert.Equal(9, text.Points[0].Y, 3);
        });
    }

    [Fact]
    public void Build_Polyline2D_ClosedAndOpen()
    {
        WithSession(doc =>
        {
            var closed = new Polyline2D { IsClosed = true };
            closed.Vertices.Add(new Vertex2D { Location = new XYZ(0, 0, 0) });
            closed.Vertices.Add(new Vertex2D { Location = new XYZ(2, 0, 0) });
            closed.Vertices.Add(new Vertex2D { Location = new XYZ(2, 2, 0) });
            doc.Entities.Add(closed);
            var open = new Polyline2D();
            open.Vertices.Add(new Vertex2D { Location = new XYZ(5, 5, 0) });
            open.Vertices.Add(new Vertex2D { Location = new XYZ(6, 6, 0) });
            doc.Entities.Add(open);
        }, s =>
        {
            var items = RenderModelBuilder.Build(s);
            Assert.Equal(2, items.Count);
            Assert.Equal(RenderKind.Polyline, items[0].Kind);
            Assert.Equal(4, items[0].Points.Count);
            Assert.Equal(items[0].Points[0], items[0].Points[3]);
            Assert.Equal(RenderKind.Polyline, items[1].Kind);
            Assert.Equal(2, items[1].Points.Count);
            Assert.Equal(5, items[1].Points[0].X, 3);
            Assert.Equal(6, items[1].Points[1].X, 3);
        });
    }

    [Fact]
    public void Build_Handle_TopLevelEntities_MatchDxfEntityHandles()
    {
        // T4 contract extension: Handle is the source entity's DXF handle as the
        // repo-wide decimal string (e.Handle.ToString(), same convention as the
        // audit/semantic TargetHandle strings).
        WithSession(Mixed, s =>
        {
            var items = RenderModelBuilder.Build(s);
            var entities = s.Doc.Entities.ToList();
            // Mixed: line, circle, arc, lwpolyline, text are 1:1 with items[0..4].
            for (int i = 0; i < 5; i++)
                Assert.Equal(entities[i].Handle.ToString(), items[i].Handle);
        });
    }

    [Fact]
    public void Build_Handle_InsertChildren_ParentHandlePlusIndex_Unique()
    {
        // Two inserts of the same 2-line block -> 4 items. Children carry the
        // parent Insert's handle + ":childIndex" so handles stay unique even
        // though the block entities are shared.
        WithSession(doc =>
        {
            var block = new BlockRecord("B2");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 1, 0), EndPoint = new XYZ(1, 1, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(10, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s);
            Assert.Equal(4, items.Count);
            var inserts = s.Doc.Entities.OfType<Insert>().ToList();
            string h0 = inserts[0].Handle.ToString(), h1 = inserts[1].Handle.ToString();
            Assert.Equal($"{h0}:0", items[0].Handle);
            Assert.Equal($"{h0}:1", items[1].Handle);
            Assert.Equal($"{h1}:0", items[2].Handle);
            Assert.Equal($"{h1}:1", items[3].Handle);
            Assert.Equal(4, items.Select(i => i.Handle).Distinct().Count());
        });
    }

    [Fact]
    public void Build_Handle_NestedInsert_ChainedIndicesUnique()
    {
        // Top Insert(NEST) -> Insert(SYM) -> line: handle chains the child
        // indices "{top}:{i}:{j}" so nested expansions stay unique.
        WithSession(doc =>
        {
            AddSymBlock(doc);
            var nest = new BlockRecord("NEST");
            nest.Entities.Add(new Insert(doc.BlockRecords["SYM"]) { InsertPoint = new XYZ(5, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
            doc.BlockRecords.Add(nest);
            doc.Entities.Add(new Insert(nest) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1 });
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            string top = s.Doc.Entities.Single().Handle.ToString();
            Assert.Equal($"{top}:0:0", item.Handle);
        });
    }

    [Fact]
    public void Bounds_MixedFixture_IncludesCircleAndArcExtents()
    {
        WithSession(Mixed, s =>
        {
            var (minX, minY, maxX, maxY) = RenderModelBuilder.Bounds(RenderModelBuilder.Build(s));
            // arc center (1,2) r4 -> conservative box x[-3,5] y[-2,6];
            // expanded insert line reaches (102,198)/(98,202).
            Assert.Equal(-3, minX, 3);
            Assert.Equal(-2, minY, 3);
            Assert.Equal(102, maxX, 3);
            Assert.Equal(202, maxY, 3);
        });
    }

    [Fact]
    public void Bounds_Empty_UnitFallback()
    {
        Assert.Equal((0.0, 0.0, 1.0, 1.0), RenderModelBuilder.Bounds(Array.Empty<RenderItem>()));
        WithSession(_ => { }, s =>
        {
            var items = RenderModelBuilder.Build(s);
            Assert.Empty(items);
            Assert.Equal((0.0, 0.0, 1.0, 1.0), RenderModelBuilder.Bounds(items));
        });
    }

    [Fact]
    public void Bounds_CircleOnly_ExtentNotRadiusPoint()
    {
        // Circle Points[1]=(radius,0) is metadata, NOT a coordinate: bounds must
        // be center +/- radius, not the raw point list hull.
        var items = new[]
        {
            new RenderItem(RenderKind.Circle, new (double, double)[] { (10, 10), (2, 0) }, "0", 0xFFFFFF, null, 0, 0, "C1")
        };
        Assert.Equal((8.0, 8.0, 12.0, 12.0), RenderModelBuilder.Bounds(items));
    }

    // ---- 计划4 视图延迟项 Task 1：OCS 任意轴变换（镜像/倾斜法向渲染） ----
    // 夹具沿用既有 WithSession/DxfFixture 模式：经真实 DXF 写/读往返，
    // 验证 Normal/Center/顶点/插入点/旋转均能在 DxfWriter/DxfReader 下存活。

    [Fact]
    public void Circle_MirroredNormal_TessellatesMirroredPolyline()
    {
        // Normal=(0,0,-1) → OCS x 轴镜像：圆心 (5,5) 半径 3 → WCS 圆心 (-5,5)
        WithSession(doc =>
        {
            doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3, Normal = new XYZ(0, 0, -1) });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s);
            var item = Assert.Single(items);
            Assert.Equal(RenderKind.Polyline, item.Kind); // 非默认法向→tessellation（真椭圆投影的诚实近似）
            Assert.True(item.Points.Count >= 72);
            Assert.True(item.Closed); // 整圆经 Closed 标志闭合（画布 EndFigure 连回首点，无 5° 弦缺口）
            double cx = item.Points.Average(p => p.X);
            double cy = item.Points.Average(p => p.Y);
            Assert.Equal(-5, cx, 3);
            Assert.Equal(5, cy, 3);
            Assert.Equal(3, item.Points.Max(p => Math.Abs(p.X - cx)), 2);
        });
    }

    [Fact]
    public void Circle_DefaultNormal_StillExactCircle()
    {
        WithSession(doc =>
        {
            doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3 });
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Circle, item.Kind); // 行为保持
        });
    }

    [Fact]
    public void LwPolyline_MirroredNormal_VerticesMirrored()
    {
        WithSession(doc =>
        {
            var lw = new LwPolyline { Normal = new XYZ(0, 0, -1) };
            lw.Vertices.Add(new LwPolyline.Vertex(new XY(1, 2)));
            lw.Vertices.Add(new LwPolyline.Vertex(new XY(3, 4)));
            doc.Entities.Add(lw);
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal((-1.0, 2.0), item.Points[0]);
            Assert.Equal((-3.0, 4.0), item.Points[1]);
        });
    }

    [Fact]
    public void Text_MirroredNormal_InsertionMirroredRotationProjected()
    {
        WithSession(doc =>
        {
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = new XYZ(2, 3, 0), Value = "镜像", Height = 5,
                Rotation = Math.PI / 2, Normal = new XYZ(0, 0, -1)
            });
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal((-2.0, 3.0), item.Points[0]);
            // OCS 内 90° 方向 (0,1) → 镜像后 (0,1) → WCS 角仍 90°
            Assert.Equal(90, item.RotationDeg, 3);
        });
    }

    [Fact]
    public void Arc_MirroredNormalInsideRotatedInsert_RotationAppliedOnce()
    {
        // 块内弧（OCS 0°→90°，镜像法向）+ Insert 旋转 90°：OCS 点 (cosθ,sinθ)
        // 镜像 → (-cosθ,sinθ)，再经 insert 仿射 R90 → (-sinθ,-cosθ)。
        // 正确 WCS 端点 (0,-1)→(-1,0)。tessellation 起始角若预加 rotationDeg
        // 会二次旋转 → (-1,0)→(0,1)，必须不发生（旋转已由 t.Apply 施加）。
        WithSession(doc =>
        {
            var block = new BlockRecord("ARCM");
            block.Entities.Add(new Arc(new XYZ(0, 0, 0), 1, 0, Math.PI / 2) { Normal = new XYZ(0, 0, -1) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0), XScale = 1, YScale = 1, ZScale = 1, Rotation = Math.PI / 2 });
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Polyline, item.Kind);
            Assert.Equal(73, item.Points.Count); // 弧含末端点：72 段 + 1
            Assert.False(item.Closed); // 非整圆弧保持开放
            Assert.Equal(0, item.Points[0].X, 3);
            Assert.Equal(-1, item.Points[0].Y, 3);
            Assert.Equal(-1, item.Points[^1].X, 3);
            Assert.Equal(0, item.Points[^1].Y, 3);
        });
    }

    [Fact]
    public void Insert_MirroredNormalWithRotation_InsertionPointMirrored()
    {
        // DXF INSERT 插入点为 OCS 坐标（controller 探针核定：InsertPoint=(5,5,0)+
        // Normal=(0,0,-1) DXF 往返保持原值，ACadSharp 不转换）→ canonical 变换
        // M·T(ip)·R·S：先块内 R·S，再平移到 OCS 插入点，最后整体经 OCS 映射。
        // 块含线 (0,0)→(1,0)，Insert ip=(5,5,0)、Normal=(0,0,-1)、Rotation=π/2：
        // start = M·ip = (-5,5)；end = M·(R90·(1,0)+ip) = M·(5,6) = (-5,6)。
        // 错误序 T(ip)·R·S·M 会得 (5,5)→(5,4)：位置未镜像 + 内容转向错误。
        WithSession(doc =>
        {
            var block = new BlockRecord("MIRINS");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(5, 5, 0),
                XScale = 1,
                YScale = 1,
                ZScale = 1,
                Rotation = Math.PI / 2,
                Normal = new XYZ(0, 0, -1)
            });
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Line, item.Kind);
            Assert.Equal(-5, item.Points[0].X, 6);
            Assert.Equal(5, item.Points[0].Y, 6);
            Assert.Equal(-5, item.Points[1].X, 6);
            Assert.Equal(6, item.Points[1].Y, 6);
        });
    }

    // ---- 计划4 视图延迟项 Task 2：Dimension 匿名块展开 + Hatch 边界/实心填充 ----
    // 夹具沿用既有 WithSession/DxfFixture 模式：经真实 DXF 写/读往返。探针核定：
    // Dimension.Block 可设且往返保留匿名块（*Dnn，AssignDocument 重命名为 *D{handle}）；
    // DxfWriter 默认配置不重生成标注几何（UpdateDimensionsInModel/InBlocks 默认 false，
    // 块内厂商绘制的图元原样保留）；Hatch.BoundaryPath.GetPoints(16) 弧段插值。

    /// <summary>
    /// 辅助：4 条 Line 边构造矩形边界路径（探针核定 new Hatch.BoundaryPath.Line(new Line{...})
    /// 边构造 + new Hatch.BoundaryPath(edges) 路径构造）。
    /// </summary>
    private static Hatch.BoundaryPath SquarePath(double x0, double y0, double x1, double y1)
    {
        return new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(x0, y0, 0), EndPoint = new XYZ(x1, y0, 0) }),
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(x1, y0, 0), EndPoint = new XYZ(x1, y1, 0) }),
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(x1, y1, 0), EndPoint = new XYZ(x0, y1, 0) }),
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(x0, y1, 0), EndPoint = new XYZ(x0, y0, 0) }),
        });
    }

    [Fact]
    public void Dimension_WithAnonymousBlock_ExpandsBlockEntities()
    {
        WithSession(doc =>
        {
            var blk = new BlockRecord("*D1");
            blk.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            blk.Entities.Add(new TextEntity { InsertPoint = new XYZ(5, 2, 0), Value = "10", Height = 2.5 });
            doc.BlockRecords.Add(blk);
            doc.Entities.Add(new DimensionLinear { Block = blk, DefinitionPoint = new XYZ(0, 0, 0), InsertionPoint = new XYZ(5, 5, 0) });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s);
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.Kind == RenderKind.Line);
            Assert.Contains(items, i => i.Kind == RenderKind.Text && i.Text == "10");
            // 子图元句柄后缀口径同 Insert："{dimHandle}:{childIndex}"（取加载后的真实句柄）。
            string dimHandle = s.Doc.Entities.OfType<DimensionLinear>().Single().Handle.ToString();
            Assert.All(items, i => Assert.StartsWith(dimHandle, i.Handle));
        });
    }

    [Fact]
    public void Dimension_NullBlock_SkippedAndCounted()
    {
        WithSession(doc => doc.Entities.Add(new DimensionLinear()), s =>
        {
            var items = RenderModelBuilder.Build(s, out int skipped);
            Assert.Empty(items);
            Assert.Equal(1, skipped); // 诚实计数，不假装已渲染
        });
    }

    [Fact]
    public void Dimension_NestedChain_WithinLimit_Expands()
    {
        // 顶层 Dimension(depth 0) -> 块内 Dimension(depth 1) -> Line(depth 2)：
        // 链深恰在 MaxInsertDepth=2 限内，全部展开（钉住守卫不过度拦截）。
        WithSession(doc =>
        {
            var b2 = new BlockRecord("*D2");
            b2.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            doc.BlockRecords.Add(b2);
            var b1 = new BlockRecord("*D1");
            b1.Entities.Add(new DimensionLinear { Block = b2 });
            doc.BlockRecords.Add(b1);
            doc.Entities.Add(new DimensionLinear { Block = b1 });
        }, s =>
        {
            var line = Assert.Single(RenderModelBuilder.Build(s, out int skipped));
            Assert.Equal(0, skipped);
            Assert.Equal(RenderKind.Line, line.Kind);
        });
    }

    [Fact]
    public void Dimension_NestedChain_DepthGuardStopsBeyondLimit()
    {
        // 顶层 Dimension(depth 0) -> 块内 Dimension(depth 1) -> 块内 Dimension(depth 2)：
        // 第三层 Dimension 与 Insert 深度守卫对齐——不展开、诚实 skip+计数，
        // 最内层 Line 不可见（守卫缺失时会被展开，此断言 RED）。
        WithSession(doc =>
        {
            var b3 = new BlockRecord("*D3");
            b3.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
            doc.BlockRecords.Add(b3);
            var b2 = new BlockRecord("*D2");
            b2.Entities.Add(new DimensionLinear { Block = b3 });
            doc.BlockRecords.Add(b2);
            var b1 = new BlockRecord("*D1");
            b1.Entities.Add(new DimensionLinear { Block = b2 });
            doc.BlockRecords.Add(b1);
            doc.Entities.Add(new DimensionLinear { Block = b1 });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s, out int skipped);
            Assert.Empty(items);
            Assert.Equal(1, skipped); // 超深 Dimension 诚实计数，不假装已渲染
        });
    }

    [Fact]
    public void Dimension_SelfReferentialBlock_DepthGuardPreventsInfiniteRecursion()
    {
        // 病态 DXF：块内 Dimension 回指自身所在块（自指环）。无守卫时无限递归
        // → StackOverflow（进程崩溃不可捕获）；守卫在 depth 2 截断 → skip+计数。
        WithSession(doc =>
        {
            var blk = new BlockRecord("*D1");
            blk.Entities.Add(new DimensionLinear { Block = blk });
            doc.BlockRecords.Add(blk);
            doc.Entities.Add(new DimensionLinear { Block = blk });
        }, s =>
        {
            var items = RenderModelBuilder.Build(s, out int skipped);
            Assert.Empty(items);
            Assert.Equal(1, skipped);
        });
    }

    [Fact]
    public void Hatch_SolidSquare_ProducesFilledHatchItem()
    {
        WithSession(doc =>
        {
            var h = new Hatch { IsSolid = true };
            h.Paths.Add(SquarePath(0, 0, 10, 10));
            doc.Entities.Add(h);
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Equal(RenderKind.Hatch, item.Kind);
            Assert.True(item.Fill);
            // 方形 4 顶点；GetPoints 每边输出起点+终点（相邻顶点重复，探针实测
            // DXF 往返后方形单路径 8 点）——按实际输出钉住精确点数。
            Assert.Equal(8, item.Points.Count(p => !double.IsNaN(p.X)));
        });
    }

    [Fact]
    public void Hatch_TwoPaths_NaNSeparated()
    {
        WithSession(doc =>
        {
            var h = new Hatch { IsSolid = true };
            h.Paths.Add(SquarePath(0, 0, 10, 10));
            h.Paths.Add(SquarePath(3, 3, 7, 7)); // 岛
            doc.Entities.Add(h);
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.Contains(item.Points, p => double.IsNaN(p.X)); // 路径分隔符
        });
    }

    [Fact]
    public void Hatch_MirroredNormal_PointsMirrored()
    {
        WithSession(doc =>
        {
            var h = new Hatch { IsSolid = true, Normal = new XYZ(0, 0, -1) };
            h.Paths.Add(SquarePath(1, 1, 3, 3));
            doc.Entities.Add(h);
        }, s =>
        {
            var item = Assert.Single(RenderModelBuilder.Build(s));
            Assert.All(item.Points.Where(p => !double.IsNaN(p.X)), p => Assert.True(p.X < 0));
        });
    }

    [Fact]
    public void Bounds_HatchNaNSeparators_Skipped()
    {
        // Hatch Points 的 (NaN,NaN) 路径分隔符不是坐标：Bounds 必须跳过，
        // 包围盒只由真实顶点决定（NaN 比较恒 false，守卫前后行为一致——钉住约定）。
        var items = new[]
        {
            new RenderItem(RenderKind.Hatch, new (double, double)[]
            {
                (0, 0), (10, 0), (10, 10), (0, 10),
                (double.NaN, double.NaN),
                (3, 3), (7, 3), (7, 7), (3, 7)
            }, "0", 0xFFFFFF, null, 0, 0, "H1", Fill: true)
        };
        Assert.Equal((0.0, 0.0, 10.0, 10.0), RenderModelBuilder.Bounds(items));
    }
}
