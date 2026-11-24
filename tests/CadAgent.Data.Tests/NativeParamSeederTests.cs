using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;
using Xunit;

namespace CadAgent.Data.Tests;

public class NativeParamSeederTests
{
    private static NativeEntityParam P(string handle, SemanticClass cls, bool ok) => new(
        handle, "TDbWall", cls, new Dictionary<string, string>(),
        new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, ok);

    [Fact]
    public void Seed_ClassifiedAndOk_ProducesAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        Assert.Single(o.NativeEntities);
        var a = Assert.Single(o.Annotations);
        Assert.Equal("21E00", a.TargetHandle);
        Assert.Equal(Provenance.NativeParameter, a.Provenance);
        Assert.Equal(0.95, a.Confidence);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal("COM 原生读参: TDbWall", a.Evidence);
    }

    [Fact]
    public void Seed_UnknownClass_NoAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Unknown, ok: true) });
        Assert.Single(o.NativeEntities);
        Assert.Empty(o.Annotations);
    }

    [Fact]
    public void Seed_ReadOkFalse_NoAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: false) });
        Assert.Single(o.NativeEntities);
        Assert.Empty(o.Annotations);
    }

    [Fact]
    public void Seed_Rescan_Idempotent()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        Assert.Single(o.NativeEntities);
        Assert.Single(o.Annotations);
    }

    /// <summary>
    /// T3 台账（T6 落实，镜像 BlockAttributeSeeder R7 先例）：首扫产 NativeParameter 标注，
    /// 同句柄重扫 ReadOk=false（读参降级）→ 陈旧标注必须清除（"当前原生数据"语义，旧类
    /// 标注不得残留放行闸口），NativeEntities 同句柄覆盖更新。
    /// </summary>
    [Fact]
    public void Seed_RescanReadOkFalse_RemovesStaleAnnotation()
    {
        var o = new SemanticOverlay();
        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: true) });
        Assert.Single(o.Annotations);
        Assert.True(Assert.Single(o.NativeEntities).ReadOk);

        NativeParamSeeder.Seed(o, new[] { P("21E00", SemanticClass.Wall, ok: false) });
        Assert.Empty(o.Annotations);
        Assert.False(Assert.Single(o.NativeEntities).ReadOk);
    }
}
