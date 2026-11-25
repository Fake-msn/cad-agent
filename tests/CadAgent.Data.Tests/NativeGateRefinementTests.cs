using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class NativeGateRefinementTests
{
    private static NativeEntityParam P(string h, string obj = "TDbWall", bool ok = true, double b = 0.0) => new(
        h, obj, SemanticClass.Wall, new Dictionary<string, string> { ["LeftWidth"] = "100" },
        new[] { b, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, ok);

    private static SemanticOverlay OvWith(NativeEntityParam p)
    { var o = new SemanticOverlay(); o.UpsertNative(p); return o; }

    [Fact]
    public void NotInOverlay_ReturnsNotRead()
        => Assert.Equal(NativeGateVerdict.NotRead,
            NativeGateRefinement.Evaluate(new SemanticOverlay(), "21E00", P("21E00")));

    [Fact]
    public void ReadOkFalse_ReturnsReadIncomplete()
    {
        var o = OvWith(P("21E00", ok: false));
        Assert.Equal(NativeGateVerdict.ReadIncomplete,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00")));
    }

    [Fact]
    public void RereadNull_ReturnsStaleMismatch()  // 运行时句柄不存在
    {
        var o = OvWith(P("21E00"));
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(o, "21E00", reread: null));
    }

    [Fact]
    public void BboxDiffers_ReturnsStaleMismatch()  // stale 防护
    {
        var o = OvWith(P("21E00", b: 0.0));
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00", b: 9.0)));
    }

    [Fact]
    public void ObjectNameDiffers_ReturnsStaleMismatch()
    {
        var o = OvWith(P("21E00", obj: "TDbWall"));
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00", obj: "TDbColumn")));
    }

    [Fact]
    public void AllMatch_ReturnsPass()
    {
        var o = OvWith(P("21E00"));
        Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.Evaluate(o, "21E00", P("21E00")));
    }

    // 唯一的补充 theory（controller 裁定准许）：brief 六 fact 未覆盖、实现可 plausibly 写错的等价类——
    // Params 字符串值/键集不一致（判据显式裁定；Count-only 或漏比值的实现本 theory 即红）、
    // bbox 双 null 相等/单 null 即不符（逐字相等裁定）、Class/Layer 刻意不比较（T3 复审关注项）。
    [Theory]
    [InlineData("params-value-differs", NativeGateVerdict.StaleMismatch)]
    [InlineData("params-key-differs", NativeGateVerdict.StaleMismatch)]
    [InlineData("params-superset", NativeGateVerdict.StaleMismatch)]
    [InlineData("params-subset", NativeGateVerdict.StaleMismatch)]
    [InlineData("bbox-both-null", NativeGateVerdict.Pass)]
    [InlineData("bbox-one-null", NativeGateVerdict.StaleMismatch)]
    [InlineData("class-differs", NativeGateVerdict.Pass)]
    [InlineData("layer-differs", NativeGateVerdict.Pass)]
    public void UntestedEquivalenceClasses(string scenario, NativeGateVerdict expected)
    {
        NativeEntityParam stored, reread;
        switch (scenario)
        {
            case "params-value-differs":
                stored = P("21E00");
                reread = P("21E00") with { Params = new Dictionary<string, string> { ["LeftWidth"] = "101" } };
                break;
            case "params-key-differs":  // 同键数、键集不同（练 TryGetValue 缺键路径而非 Count 短路）
                stored = P("21E00");
                reread = P("21E00") with { Params = new Dictionary<string, string> { ["RightWidth"] = "100" } };
                break;
            case "params-superset":  // 重读多键（Count 不等）→ StaleMismatch（Count 短路路径）
                stored = P("21E00");
                reread = P("21E00") with
                {
                    Params = new Dictionary<string, string> { ["LeftWidth"] = "100", ["Extra"] = "1" }
                };
                break;
            case "params-subset":  // 重读缺键（Count 不等）→ StaleMismatch（Count 短路路径）
                stored = P("21E00") with
                {
                    Params = new Dictionary<string, string> { ["LeftWidth"] = "100", ["RightWidth"] = "100" }
                };
                reread = P("21E00");
                break;
            case "bbox-both-null":
                stored = P("21E00") with { Bbox = null };
                reread = P("21E00") with { Bbox = null };
                break;
            case "bbox-one-null":
                stored = P("21E00") with { Bbox = null };
                reread = P("21E00");
                break;
            case "class-differs":
                stored = P("21E00");
                reread = P("21E00") with { Class = SemanticClass.Column };
                break;
            case "layer-differs":
                stored = P("21E00") with { Layer = "WALL" };
                reread = P("21E00") with { Layer = "COLUMN" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var o = OvWith(stored);
        Assert.Equal(expected, NativeGateRefinement.Evaluate(o, "21E00", reread));
    }

    // R13（controller 裁定，ledger）：TarchDerive 经炸开通道并入的 "XData:*" 参数在 live 重读白名单中不存在，
    // ParamsEq 必须双侧忽略 XData:* 键，否则任何带 XData 的句柄在 semantic scan --explode 后永久 StaleMismatch。
    [Fact]
    public void R13a_StoredXDataIgnored_Pass()
    {
        var stored = P("21E00") with
        {
            Params = new Dictionary<string, string> { ["LeftWidth"] = "100", ["XData:HYDRANT_RADIUS"] = "28" }
        };
        var reread = P("21E00");  // 重读白名单不含 XData:*
        Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.Evaluate(OvWith(stored), "21E00", reread));
    }

    [Fact]
    public void R13b_OnlyXDataBothSides_Pass()
    {
        var stored = P("21E00") with { Params = new Dictionary<string, string> { ["XData:K"] = "1" } };
        var reread = P("21E00") with { Params = new Dictionary<string, string>() };
        Assert.Equal(NativeGateVerdict.Pass,
            NativeGateRefinement.Evaluate(OvWith(stored), "21E00", reread));
    }

    [Fact]
    public void R13c_NonXDataDrift_StillStaleMismatch()
    {
        var stored = P("21E00") with
        {
            Params = new Dictionary<string, string> { ["LeftWidth"] = "100", ["XData:K"] = "1" }
        };
        var reread = P("21E00") with
        {
            Params = new Dictionary<string, string> { ["LeftWidth"] = "101", ["XData:K"] = "2" }
        };
        Assert.Equal(NativeGateVerdict.StaleMismatch,
            NativeGateRefinement.Evaluate(OvWith(stored), "21E00", reread));
    }
}
