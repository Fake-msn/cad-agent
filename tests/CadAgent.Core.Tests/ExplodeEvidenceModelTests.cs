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
    public void Overlay_UpsertExplode_MixedCaseHandle_SingleRow()
    {
        // M6（复审修复波）：句柄是 hex 标识（AutoCAD 原样大写，计划/手写来源可能小写）——
        // 大小写不同即同一实体，必须覆盖而不是并存两行（消费侧 FirstOrDefault 会读到陈旧行）。
        var o = new SemanticOverlay();
        o.UpsertExplode(E("21E00"));
        o.UpsertExplode(E("21e00", full: false));
        var row = Assert.Single(o.ExplodeEvidence);
        Assert.Equal("21e00", row.Handle);        // 后写整条替换
        Assert.False(row.FullyExplodable);
    }

    // A2（边界补齐）：FailReason 是可选尾部参数——旧构造/旧 JSON 一律 null，不破坏既有形状。
    [Fact]
    public void ExplodeEvidence_FailReason_DefaultNull_AndRoundTrip()
    {
        Assert.Null(E("21E00").FailReason);   // 旧构造（13 参）→ null

        var dir = Directory.CreateTempSubdirectory("explodefail").FullName;
        try
        {
            string path = Path.Combine(dir, "semantic.json");
            var o = new SemanticOverlay();
            o.UpsertExplode(E("321A") with { FailReason = "no-geometry:eInvalidInput" });
            o.Save(path);

            var loaded = SemanticOverlay.Load(path);
            var row = Assert.Single(loaded.ExplodeEvidence);
            Assert.Equal("no-geometry:eInvalidInput", row.FailReason);
        }
        finally { Directory.Delete(dir, true); }
    }

    // B1（边界补齐）：块定义 TDb 独立表——JSON 加性（旧文件无此字段→空表）、显式 null 归一、往返保真。
    [Fact]
    public void Overlay_BlockDefTdb_JsonAdditive_ExplicitNull_AndRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("blockdef").FullName;
        try
        {
            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacy, """{"Annotations":[],"Mappings":[],"Version":1}""");
            Assert.Empty(SemanticOverlay.Load(legacy).BlockDefTdb);

            string nulled = Path.Combine(dir, "nulled.json");
            File.WriteAllText(nulled,
                """{"Annotations":[],"Mappings":[],"Version":1,"ExplodeEvidence":[],"BlockDefTdb":null}""");
            Assert.Empty(SemanticOverlay.Load(nulled).BlockDefTdb);

            string path = Path.Combine(dir, "semantic.json");
            var o = new SemanticOverlay();
            o.BlockDefTdb.Add(new BlockDefTdbEntry("$equip$00002832", "TDbECabletry", 1364));
            o.BlockDefTdb.Add(new BlockDefTdbEntry("门块A", "TDbEFitting", 2));
            o.Save(path);

            var loaded = SemanticOverlay.Load(path);
            Assert.Equal(2, loaded.BlockDefTdb.Count);
            Assert.Equal("$equip$00002832", loaded.BlockDefTdb[0].BlockName);
            Assert.Equal("TDbECabletry", loaded.BlockDefTdb[0].RxClass);
            Assert.Equal(1364, loaded.BlockDefTdb[0].Count);
            Assert.Equal("TDbEFitting", loaded.BlockDefTdb[1].RxClass);
        }
        finally { Directory.Delete(dir, true); }
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
