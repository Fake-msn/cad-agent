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
}
