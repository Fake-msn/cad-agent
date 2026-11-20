namespace CadAgent.Core.Tests;

public class SemanticOverlayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sem-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyVersionZero()
    {
        var o = SemanticOverlay.Load(_path);
        Assert.Equal(0, o.Version);
        Assert.Empty(o.Annotations);
        Assert.Empty(o.Mappings);
    }

    [Fact]
    public void Save_IncrementsVersion_AndRoundTrips()
    {
        var o = SemanticOverlay.Load(_path);
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.6, "平行线对 d=200"));
        o.Save(_path);
        var loaded = SemanticOverlay.Load(_path);
        Assert.Equal(1, loaded.Version);
        Assert.Single(loaded.Annotations);
        Assert.Equal("平行线对 d=200", loaded.Annotations[0].Evidence);
        loaded.Save(_path);
        Assert.Equal(2, SemanticOverlay.Load(_path).Version);
    }

    [Fact]
    public void Upsert_SameHandleAndProvenance_Replaces()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.6, "e1"));
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.GeometryInference, 0.7, "e2"));
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.LayerInference, 0.8, "e3"));
        Assert.Equal(2, o.Annotations.Count);
    }

    [Fact]
    public void ConfirmMapping_ReplacesSameLayer()
    {
        var o = new SemanticOverlay();
        o.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Wall, "u1", DateTimeOffset.UnixEpoch));
        o.ConfirmMapping(new LayerMapping("W-新墙", SemanticClass.Column, "u2", DateTimeOffset.UnixEpoch));
        Assert.Single(o.Mappings);
        Assert.Equal(SemanticClass.Column, o.Mappings[0].Class);
    }

    [Fact]
    public void ApplyMapping_UpgradesAnnotationsToHumanConfirmed()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Unknown, Provenance.LayerInference, 0.3, "未识别"));
        o.ApplyMappingToAnnotations("W-新墙", SemanticClass.Wall, new[] { "h1" });
        var a = o.Annotations.Single(x => x.TargetHandle == "h1");
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
        Assert.Equal(1.0, a.Confidence);
    }
}