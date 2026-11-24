using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Core.Tests;

public class SemanticOverlayNativeTests
{
    private static NativeEntityParam P(string handle, bool ok = true) => new(
        handle, "TDbWall", SemanticClass.Wall,
        new Dictionary<string, string> { ["LeftWidth"] = "100" },
        new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, ok);

    [Fact]
    public void NativeEntities_DefaultEmpty()
        => Assert.Empty(new SemanticOverlay().NativeEntities);

    [Fact]
    public void UpsertNative_SameHandle_Overwrites()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(P("21E00"));
        var updated = P("21E00") with { Bbox = new[] { 9.0, 9, 9, 9, 9, 9 } };
        o.UpsertNative(updated);
        Assert.Single(o.NativeEntities);
        Assert.Equal(9.0, o.NativeEntities[0].Bbox![0]);
    }

    [Fact]
    public void UpsertNative_DifferentHandle_KeepsBoth()
    {
        var o = new SemanticOverlay();
        o.UpsertNative(P("21E00")); o.UpsertNative(P("321A"));
        Assert.Equal(2, o.NativeEntities.Count);
    }

    [Fact]
    public void Json_LegacyFileWithoutField_LoadsEmpty()
    {
        string legacy = """{"Annotations":[],"Mappings":[],"Version":1}""";
        var o = JsonSerializer.Deserialize<SemanticOverlay>(legacy,
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.NotNull(o.NativeEntities);
        Assert.Empty(o.NativeEntities);
    }

    [Fact]
    public void Json_RoundTrip_PreservesNative()
    {
        var dir = Directory.CreateTempSubdirectory("ovnative").FullName;
        try
        {
            string path = Path.Combine(dir, "semantic.json");
            var o = new SemanticOverlay(); o.UpsertNative(P("21E00")); o.Save(path);
            var loaded = SemanticOverlay.Load(path);
            Assert.Single(loaded.NativeEntities);
            Assert.Equal("21E00", loaded.NativeEntities[0].Handle);
            Assert.Equal(SemanticClass.Wall, loaded.NativeEntities[0].Class);
        }
        finally { Directory.Delete(dir, true); }
    }
}
