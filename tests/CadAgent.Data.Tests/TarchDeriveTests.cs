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

    // I-2（修复波1）/ I-2b（修复波2）：bbox 不可验（null）的证据行——旧实现跳过 bbox 比对后
    // 静默落 0.9，等于把"包围盒不可验"当"已验证一致"。保守口径：**bbox=null 一律不种子**
    //（不只 extents:degenerate 前缀；extents:Exception 等其它不可验原因同口径），行本身保留可复核。
    [Theory]
    [InlineData("extents:degenerate")]
    [InlineData("extents:degenerate:roundtrip")]   // 前缀口径：后续细分原因亦不种子
    [InlineData("extents:Exception")]              // I-2b：非退化前缀但 bbox 同样不可验
    public void Enrich_UnverifiableBbox_FullyAndClassKnown_NoAnnotation_ButRowKept(string failReason)
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("D1") with { Bbox = null, FailReason = failReason } });

        var row = Assert.Single(o.ExplodeEvidence);
        Assert.Equal(failReason, row.FailReason);   // 证据行保留（撤行=丢事实）
        Assert.Null(row.Bbox);
        Assert.Empty(o.Annotations);                // 但不得落 NativeExplode 0.9
    }

    [Fact]
    public void Enrich_Rescan_RemovesStaleRows()
    {
        var o = new SemanticOverlay();
        TarchDerive.Enrich(o, new[] { Wall("OLD1", Line(0, 0, 1, 0)) });
        TarchDerive.Enrich(o, Array.Empty<ExplodeEvidence>());
        Assert.Empty(o.ExplodeEvidence);
    }

    // ---- M7（PR#30 终审）：陈旧清理与 Upsert 同口径（OrdinalIgnoreCase）----

    /// <summary>同一实体的混合大小写行：本轮读到的存活行原地刷新（不删后追），未读到的照旧回收——
    /// 两行场景下"21E00 存活 / 21e01 消失"只能剩一行且句柄为本轮实读形态。</summary>
    [Fact]
    public void Enrich_Rescan_MixedCaseRow_ReclaimedByIgnoreCase()
    {
        var o = new SemanticOverlay();
        o.UpsertExplode(Wall("21e00", Line(0, 0, 100, 0)));   // 上轮小写形态（同一实体）
        o.UpsertExplode(Wall("21e01", Line(0, 0, 100, 0)));   // 上轮另一实体（本轮不再读到）

        TarchDerive.Enrich(o, new[] { Wall("21E00", Line(0, 0, 100, 0)) });   // 本轮大写形态

        var row = Assert.Single(o.ExplodeEvidence);
        Assert.Equal("21E00", row.Handle);   // 存活行按本轮形态刷新；陈旧行回收
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

    // ---- B1（边界补齐）：块定义 TDb 独立段——全量刷新语义（重扫替换、空/缺省清空），只披露不落标注 ----

    [Fact]
    public void Enrich_BlockDefs_ReplacedOnRescan_EmptyClears()
    {
        var o = new SemanticOverlay();
        var ev = new[] { Wall("21E00") };

        TarchDerive.Enrich(o, ev, new[]
        {
            new BlockDefTdbEntry("门块A", "TDbECabletry", 3),
        });
        var first = Assert.Single(o.BlockDefTdb);
        Assert.Equal("门块A", first.BlockName);
        Assert.Equal(3, first.Count);

        // 重扫（新读数）→ 整表替换，旧块名不得残留。
        TarchDerive.Enrich(o, ev, new[]
        {
            new BlockDefTdbEntry("块B", "TDbEFitting", 1),
            new BlockDefTdbEntry("块C", "TDbECabletry", 2),
        });
        Assert.Equal(2, o.BlockDefTdb.Count);
        Assert.DoesNotContain(o.BlockDefTdb, e => e.BlockName == "门块A");

        // 空表（桥无 BDEF 行 / 旧运行时）→ 清空，不留上轮幽灵。
        TarchDerive.Enrich(o, ev, Array.Empty<BlockDefTdbEntry>());
        Assert.Empty(o.BlockDefTdb);

        // 缺省（null）与空表同口径：清空。
        TarchDerive.Enrich(o, ev, new[] { new BlockDefTdbEntry("块D", "TDbECabletry", 1) });
        TarchDerive.Enrich(o, ev);
        Assert.Empty(o.BlockDefTdb);
    }

    /// <summary>
    /// M-6（修复波1）：块定义段**只披露**——不改写既有标注/原生参数表。
    /// 旧用例把证据类设为 Unknown（"天然不种子"），断言近乎同义反复；本用例改用**类已知**证据
    /// （FullyExplodable=false 使其本条不种子）并预置既有标注 + 原生参数行：块定义进入独立表时，
    /// 这两张表必须逐字不变（不得回收非 NativeExplode 标注、不得改原生参数）。
    /// </summary>
    [Fact]
    public void Enrich_BlockDefs_DisclosureOnly_DoesNotTouchAnnotationsOrNativeEntities()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string> { ["TotalWidth"] = "100" },
            null, DateTimeOffset.UtcNow, true, "0dt"));
        o.UpsertAnnotation(new SemanticAnnotation(
            "21E00", SemanticClass.Wall, Provenance.LayerInference, 0.8, "图层推断"));

        // 类已知（TDbWall）但未全炸开 → 本条证据本身不种子；块定义更不得造标注。
        var ev = Wall("21E00") with { FullyExplodable = false };
        TarchDerive.Enrich(o, new[] { ev }, new[]
        {
            new BlockDefTdbEntry("$equip$00002832", "TDbECabletry", 1364),
        });

        var row = Assert.Single(o.ExplodeEvidence);
        Assert.Equal("21E00", row.Handle);   // 块定义（按块名）不混入实例证据表
        var native = Assert.Single(o.NativeEntities);
        Assert.Equal("100", native.Params!["TotalWidth"]);
        var annotation = Assert.Single(o.Annotations);
        Assert.Equal(Provenance.LayerInference, annotation.Provenance);   // 既有标注原样保留
        Assert.Equal(0.8, annotation.Confidence);
        Assert.Single(o.BlockDefTdb);
    }

    /// <summary>
    /// A4（技术债批次A）：块定义行盖章 DrawingHash——每个入表行携带**本轮**图纸哈希
    ///（来源可追溯：同名块在不同图纸下的计数不可混读）；缺省（null）则整表为 null，
    /// 且覆盖掉入参行自带的旧哈希（重扫整表替换，哈希随行一并不残留）。
    /// </summary>
    [Fact]
    public void Enrich_BlockDefs_StampsDrawingHash()
    {
        var o = new SemanticOverlay();
        var ev = new[] { Wall("21E00") };

        TarchDerive.Enrich(o, ev, new[]
        {
            new BlockDefTdbEntry("门块A", "TDbECabletry", 3),
            new BlockDefTdbEntry("块B", "TDbEFitting", 1, "SHA256:OLD"),
        }, "SHA256:ABC");

        Assert.Equal(2, o.BlockDefTdb.Count);
        Assert.All(o.BlockDefTdb, d => Assert.Equal("SHA256:ABC", d.DrawingHash));

        // 缺省哈希（旧调用面）：列仍为 null，且覆盖掉入参行自带的旧哈希。
        TarchDerive.Enrich(o, ev, new[] { new BlockDefTdbEntry("块B", "TDbEFitting", 1, "SHA256:OLD") });
        Assert.All(o.BlockDefTdb, d => Assert.Null(d.DrawingHash));
    }
}
