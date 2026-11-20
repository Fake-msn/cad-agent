namespace CadAgent.Core.Tests;

public class SemanticOverlayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sem-{Guid.NewGuid():N}.json");
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (Directory.Exists(_path + ".tmp")) Directory.Delete(_path + ".tmp", true);
    }

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

    [Fact]
    public void Save_Atomic_NoTmpLeftAfterSuccess_AndRoundTrips()
    {
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        o.Save(_path);
        Assert.False(File.Exists(_path + ".tmp")); // 成功保存后不得残留临时文件
        var loaded = SemanticOverlay.Load(_path);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("h1", loaded.Annotations.Single().TargetHandle);
    }

    [Fact]
    public void Save_Twice_LeavesExactlyOneFile_NoTmpAccumulation()
    {
        var o = new SemanticOverlay();
        o.Save(_path);
        o.Save(_path);
        string dir = Path.GetDirectoryName(_path)!;
        string stem = Path.GetFileName(_path);
        var related = Directory.GetFiles(dir, stem + "*");
        Assert.Single(related);
        Assert.Equal(_path, related[0]);
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Equal(2, SemanticOverlay.Load(_path).Version);
    }

    [Fact]
    public void Save_Atomic_OldFileIntactWhenWriteFails()
    {
        // 原子写先写 path+".tmp" 再 Move。用目录占住 tmp 路径使写入必然失败：
        // 旧文件必须完好（非原子实现会直接写目标路径成功——本测试先 RED）。
        var o = new SemanticOverlay();
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "first"));
        o.Save(_path); // v1 落盘
        string original = File.ReadAllText(_path);

        Directory.CreateDirectory(_path + ".tmp"); // 占住临时文件路径 → 写入失败
        var o2 = SemanticOverlay.Load(_path);
        o2.UpsertAnnotation(new SemanticAnnotation("h2", SemanticClass.Door, Provenance.HumanConfirmed, 1.0, "second"));
        Assert.ThrowsAny<Exception>(() => o2.Save(_path));
        // 原文件完好无损（原子写：失败时旧文件不被破坏）。
        Assert.Equal(original, File.ReadAllText(_path));
        Assert.Equal(1, SemanticOverlay.Load(_path).Version);
    }
}
