using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class TarchDeriveTests
{
    private static ExplodePrimitive Line(double x1, double y1, double x2, double y2) =>
        new("line", new[] { x1, y1, 0, x2, y2, 0 }, null, null);

    private static ExplodeEvidence Wall(string h, params ExplodePrimitive[] prims) => new(
        h, "TDbWall", "ImpCurve", "0dt", new[] { 0.0, 0, 0, 100, 300, 3000 }, true,
        prims.Length, 0, prims, new Dictionary<string, string>(), null, DateTimeOffset.UtcNow);

    [Fact]
    public void Enrich_ParallelPair_MatchingTotalWidth_DerivesCenterline()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "100" },
            new[] { 0.0, 0.0, 0, 100.0, 300.0, 3000 }, DateTimeOffset.UtcNow, true, "0dt"));
        var r = TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        var cl = o.ExplodeEvidence.Single(e => e.Handle == "21E00").CenterlineJson;
        Assert.NotNull(cl);
        Assert.Contains("\"kind\":\"line\"", cl);
        Assert.Contains("50", cl);   // 中线 y=50
        Assert.Empty(r.SymbolCandidates);
    }

    [Fact]
    public void Enrich_WidthMismatch_NoCenterline()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "150" },
            null, DateTimeOffset.UtcNow, true, null));
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        Assert.Null(o.ExplodeEvidence[0].CenterlineJson);
    }

    [Fact]
    public void Enrich_SingleLine_NoCenterline()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0)) });
        Assert.Null(o.ExplodeEvidence[0].CenterlineJson);
    }

    [Fact]
    public void Enrich_XData_MergesOnlyIntoExistingParams()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("3EB5D", "TDbEquipment", SemanticClass.Equipment,
            new Dictionary<string, string> { ["PScale"] = "100" }, null, DateTimeOffset.UtcNow, true, null));
        TarchDerive.Enrich(o, new[]
        {
            new ExplodeEvidence("3EB5D", "TDbEquipment", "ImpEntity", "E-设备", null, true, 1, 0,
                new[] { Line(0, 0, 1, 0) },
                new Dictionary<string, string> { ["HYDRANT_RADIUS"] = "28" }, null, DateTimeOffset.UtcNow),
        });
        var p = o.NativeEntities.Single(n => n.Handle == "3EB5D");
        Assert.Equal("28", p.Params["XData:HYDRANT_RADIUS"]);
        Assert.Equal("100", p.Params["PScale"]);
    }

    [Fact]
    public void Enrich_FullyAndClassKnown_SeedsNativeExplodeAnnotation()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        var a = Assert.Single(o.Annotations);
        Assert.Equal(Provenance.NativeExplode, a.Provenance);
        Assert.Equal(0.9, a.Confidence);
    }

    [Fact]
    public void Enrich_NotFully_NoAnnotation_ButRowKept()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("21E00") with { FullyExplodable = false } });
        Assert.Single(o.ExplodeEvidence);
        Assert.Empty(o.Annotations);
    }

    [Fact]
    public void Enrich_Rescan_RemovesStaleRows()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("OLD1", Line(0, 0, 1, 0)) });
        TarchDerive.Enrich(o, Array.Empty<ExplodeEvidence>());
        Assert.Empty(o.ExplodeEvidence);
    }

    // ---- 复审补充：空/缺基元防御（T4 注）----

    [Fact]
    public void Enrich_NullOrEmptyPrimitives_NoCrash_CenterlineNull_RowKept()
    {
        var o = new SemanticOverlay();
        var nullPrims = Wall("21E00") with { Primitives = null!, XData = null! };
        TarchDerive.Enrich(o, new[] { nullPrims, Wall("21E01") });

        Assert.Equal(2, o.ExplodeEvidence.Count);
        Assert.All(o.ExplodeEvidence, e => Assert.Null(e.CenterlineJson));
    }

    // ---- 复审补充：TotalWidth 不可解析视为缺失（不得借此拒推）----

    [Fact]
    public void Enrich_TotalWidthUnparseable_TreatedAsMissing_CenterlineKept()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "100mm" },
            null, DateTimeOffset.UtcNow, true, null));
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        Assert.NotNull(o.ExplodeEvidence[0].CenterlineJson);
    }

    // ---- 复审补充：反向线段仍配对（先按首线方向翻转再去叉积）----

    [Fact]
    public void Enrich_ReversedDirectionPair_StillDerivesCenterline()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[]
        {
            Wall("21E00", Line(0, 0, 100, 0), Line(100, 100, 0, 100)),
        });
        var cl = o.ExplodeEvidence[0].CenterlineJson;
        Assert.NotNull(cl);
        Assert.Contains("\"p1\":[0,50,0]", cl);
        Assert.Contains("\"p2\":[100,50,0]", cl);
    }
}
