using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Core.Tests;

public class ExplodeEvidenceModelTests
{
    private static ExplodeEvidence E(string h, bool full = true) => new(
        h, "TDbWall", "ImpCurve", "0dt", new[] { 0.0, 0, 0, 1, 1, 3000 }, full, 4, 0,
        new List<ExplodePrimitive> { new("line", new[] { 0.0, 0, 0, 1.0, 0, 0 }, null, null) },
        new Dictionary<string, string> { ["HYDRANT_RADIUS"] = "28" },
        null, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("TDbDimension2", SemanticClass.Annotation)]
    [InlineData("TDbSpace", SemanticClass.Room)]
    [InlineData("TDbElevation", SemanticClass.Annotation)]
    [InlineData("TDbOpening", SemanticClass.Unknown)]
    public void ClassMap_ExtendedClasses(string obj, SemanticClass expected)
        => Assert.Equal(expected, TDbClassMap.ClassFor(obj));

    [Fact]
    public void Provenance_HasNativeExplode()
        => Assert.True(Enum.IsDefined(typeof(Provenance), "NativeExplode"));

    [Fact]
    public void Overlay_UpsertExplode_Idempotent()
    {
        var o = new SemanticOverlay();
        o.UpsertExplode(E("21E00"));
        o.UpsertExplode(E("21E00", full: false));
        Assert.Single(o.ExplodeEvidence);
        Assert.False(o.ExplodeEvidence[0].FullyExplodable);
    }

    [Fact]
    public void Overlay_Json_ExplicitNullField_Load_Normalizes()
    {
        // 变异守卫：显式 "ExplodeEvidence": null 绕过属性初始化，必须由 Load 的 ??= 归一。
        var dir = Directory.CreateTempSubdirectory("explodenull").FullName;
        try
        {
            string path = Path.Combine(dir, "semantic.json");
            File.WriteAllText(path,
                """{"Annotations":[],"Mappings":[],"Version":1,"ExplodeEvidence":null}""");
            var loaded = SemanticOverlay.Load(path);
            Assert.NotNull(loaded.ExplodeEvidence);
            Assert.Empty(loaded.ExplodeEvidence);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Overlay_UpsertExplode_DifferentHandle_KeepsBoth()
    {
        var o = new SemanticOverlay();
        o.UpsertExplode(E("21E00")); o.UpsertExplode(E("321A"));
        Assert.Equal(2, o.ExplodeEvidence.Count);
    }

    [Fact]
    public void Overlay_Json_Additive_And_RoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("explode").FullName;
        try
        {
            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacy, """{"Annotations":[],"Mappings":[],"Version":1}""");
            Assert.Empty(SemanticOverlay.Load(legacy).ExplodeEvidence);

            string path = Path.Combine(dir, "semantic.json");
            var o = new SemanticOverlay(); o.UpsertExplode(E("21E00")); o.Save(path);
            var loaded = SemanticOverlay.Load(path);
            Assert.Single(loaded.ExplodeEvidence);
            Assert.Equal("0dt", loaded.ExplodeEvidence[0].Layer);
            Assert.Equal("28", loaded.ExplodeEvidence[0].XData["HYDRANT_RADIUS"]);
            Assert.Equal(1, loaded.ExplodeEvidence[0].Primitives[0].Points[3]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
