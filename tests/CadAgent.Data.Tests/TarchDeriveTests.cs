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

    // ---- 复审补充 M1：标注回收（上轮 0.9 不得残留授权）----

    [Fact]
    public void Enrich_Rescan_HandleVanished_AnnotationReclaimed()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("H1", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        Assert.Single(o.Annotations);

        // 第二轮证据不含 H1（H9 不满足种子条件）：证据行被陈旧清理删掉，标注必须同步回收。
        TarchDerive.Enrich(o, new[] { Wall("H9") with { FullyExplodable = false } });
        Assert.Empty(o.Annotations);
        Assert.DoesNotContain(o.ExplodeEvidence, e => e.Handle == "H1");
    }

    [Fact]
    public void Enrich_Rescan_BboxMismatch_AnnotationReclaimed_RowKept()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("H2", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string>(), new[] { 0.0, 0.0, 0, 100.0, 300.0, 3000 },
            DateTimeOffset.UtcNow, true, "0dt"));
        TarchDerive.Enrich(o, new[] { Wall("H2", Line(0, 0, 100, 0), Line(0, 100, 100, 100)) });
        Assert.Single(o.Annotations);

        // 第二轮 bbox 漂移 → 种子条件不满足（存疑）；行保留，但上轮 0.9 标注必须回收。
        var drifted = Wall("H2", Line(0, 0, 100, 0), Line(0, 100, 100, 100))
            with { Bbox = new[] { 0.0, 0, 0, 250, 300, 3000 } };
        TarchDerive.Enrich(o, new[] { drifted });
        Assert.Empty(o.Annotations);
        Assert.Single(o.ExplodeEvidence);
    }

    // ---- 复审补充 L1：符号块候选正向用例（只披露不落标注）----

    [Fact]
    public void Enrich_DorLibBlockName_YieldsDoorWindowCandidate_NoAnnotation()
    {
        var o = new SemanticOverlay();
        var ev = Wall("21E00",
            new ExplodePrimitive("block", new[] { 0.0, 0, 0 }, null, "$DorLib2D$DOOR-TAG"))
            with { FullyExplodable = false };   // 不触发种子，独立验证"永不落标注"
        var r = TarchDerive.Enrich(o, new[] { ev });

        var c = Assert.Single(r.SymbolCandidates);
        Assert.Equal("21E00", c.Handle);
        Assert.Equal("$DorLib2D$DOOR-TAG", c.BlockName);
        Assert.Equal("door-window", c.Kind);
        Assert.Contains("门窗", c.ClassHint);
        Assert.Empty(o.Annotations);
    }

    // ---- 复审补充 L3：零宽（重合线）不得给中心线 ----

    [Fact]
    public void Enrich_ZeroWidthCoincidentLines_NoCenterline()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "0" }, null, DateTimeOffset.UtcNow, true, null));
        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0), Line(0, 0, 100, 0)) });
        Assert.Null(o.ExplodeEvidence[0].CenterlineJson);
    }

    // ---- 复审补充 L4：重扫 XData 不得残留幽灵键，非 XData 参数保留 ----

    [Fact]
    public void Enrich_Rescan_XData_NoGhostKeys_NonXDataParamsKept()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("3EB5D", "TDbEquipment", SemanticClass.Equipment,
            new Dictionary<string, string> { ["PScale"] = "100" }, null, DateTimeOffset.UtcNow, true, null));

        TarchDerive.Enrich(o, new[]
        {
            Wall("3EB5D", Line(0, 0, 1, 0)) with
            {
                XData = new Dictionary<string, string> { ["HYDRANT_RADIUS"] = "28", ["GONE_KEY"] = "1" },
            },
        });
        Assert.Equal("1", o.NativeEntities.Single(n => n.Handle == "3EB5D").Params["XData:GONE_KEY"]);

        // 第二轮 XData 不再含 GONE_KEY → 该键必须消失（不得叠加残留）。
        TarchDerive.Enrich(o, new[]
        {
            Wall("3EB5D", Line(0, 0, 1, 0)) with
            {
                XData = new Dictionary<string, string> { ["HYDRANT_RADIUS"] = "30" },
            },
        });
        var p = o.NativeEntities.Single(n => n.Handle == "3EB5D");
        Assert.Equal("30", p.Params["XData:HYDRANT_RADIUS"]);
        Assert.False(p.Params.ContainsKey("XData:GONE_KEY"));
        Assert.Equal("100", p.Params["PScale"]);
    }
}
