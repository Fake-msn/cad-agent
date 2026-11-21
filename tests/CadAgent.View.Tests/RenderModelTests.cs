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
            new RenderItem(RenderKind.Circle, new (double, double)[] { (10, 10), (2, 0) }, "0", 0xFFFFFF, null, 0, 0)
        };
        Assert.Equal((8.0, 8.0, 12.0, 12.0), RenderModelBuilder.Bounds(items));
    }
}
