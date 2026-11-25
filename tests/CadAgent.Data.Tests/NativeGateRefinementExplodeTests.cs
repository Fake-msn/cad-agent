using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class NativeGateRefinementExplodeTests
{
    private static ExplodeEvidence E(string h, string rx = "TDbOpening", bool full = true, double b = 0.0) => new(
        h, rx, "ImpEntity", "L", new[] { b, 0, 0, 10, 10, 0 }, full, 2, 0,
        Array.Empty<ExplodePrimitive>(), new Dictionary<string, string>(), null, DateTimeOffset.UtcNow);

    private static SemanticOverlay Ov(params ExplodeEvidence[] evs)
    { var o = new SemanticOverlay(); foreach (var e in evs) o.UpsertExplode(e); return o; }

    [Fact]
    public void ParamsGatePass_WinsDirectly()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(new NativeEntityParam("H1", "TDbWall", SemanticClass.Wall,
            new Dictionary<string, string>(), new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, true, null));
        Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.EvaluateCombined(o, "H1", o.NativeEntities[0], null));
    }

    [Fact]
    public void ExplodeGate_PassesWhenParamsMissing()
        => Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.EvaluateCombined(Ov(E("H2")), "H2", null, E("H2")));

    [Fact]
    public void NoExplodeRow_ExplodeNotRead()
        => Assert.Equal(NativeGateVerdict.ExplodeNotRead,
            NativeGateRefinement.EvaluateCombined(new SemanticOverlay(), "H3", null, E("H3")));

    [Fact]
    public void NotFully_NotFullyExplodable()
        => Assert.Equal(NativeGateVerdict.NotFullyExplodable,
            NativeGateRefinement.EvaluateCombined(Ov(E("H4", full: false)), "H4", null, E("H4", full: false)));

    [Fact]
    public void RereadNull_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H5")), "H5", null, null));

    [Fact]
    public void BboxDrift_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H6", b: 0)), "H6", null, E("H6", b: 5)));

    [Fact]
    public void RxDrift_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H7")), "H7", null, E("H7", rx: "TDbEquipment")));

    // 补充等价类（controller 准许）：炸开 bbox 单 null 即不符（RULING），非"null 视同通配"。
    [Fact]
    public void BboxOneNull_StaleExplode()
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("H8") with { Bbox = null }), "H8", null, E("H8")));

    // 补充等价类：容差 1e-3 内不视为漂移（与参数门逐字相等形成对照；防"精确相等"实现漏判）。
    [Fact]
    public void BboxWithinTolerance_Pass()
        => Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.EvaluateCombined(Ov(E("H9", b: 0.0)), "H9", null, E("H9", b: 5e-4)));

    // R15（控制器义务，顺带修）：非有限 bbox 分量必须按**不符** fail-closed——旧写法
    // `Math.Abs(diff) > tol` 对 NaN 恒 false，会把 NaN/Inf 漂移当"容差内一致"放行。
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BboxNonFiniteComponent_StaleExplode(double bad)
        => Assert.Equal(NativeGateVerdict.StaleExplode,
            NativeGateRefinement.EvaluateCombined(Ov(E("HA", b: bad)), "HA", null, E("HA", b: 0)));
}
