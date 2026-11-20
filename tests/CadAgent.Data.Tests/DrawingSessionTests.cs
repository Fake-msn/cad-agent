using ACadSharp.Entities;
using CSMath;

namespace CadAgent.Data.Tests;

public class DrawingSessionTests : IDisposable
{
    private readonly string _path;
    public DrawingSessionTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_ReadsEntitiesAndLayers()
    {
        var session = DrawingSession.Load(_path);
        var summary = session.Summarize();
        Assert.Single(summary.Entities);
        Assert.Equal("Line", summary.Entities[0].Kind);
        Assert.Contains("0", summary.Layers);
        Assert.Empty(summary.Unknowns);
    }

    [Fact]
    public void ComputeHash_IsStableForSameContent()
    {
        var s1 = DrawingSession.Load(_path);
        var s2 = DrawingSession.Load(_path);
        Assert.Equal(s1.ComputeHash(), s2.ComputeHash());
    }

    [Fact]
    public void SaveWorking_And_Reload_PersistChanges()
    {
        var session = DrawingSession.Load(_path);
        session.Doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(1, 2), EndPoint = (XYZ)new XY(3, 4) });
        session.SaveWorking();
        session.Reload();
        Assert.Equal(2, session.Summarize().Entities.Count);
    }

    [Fact]
    public void Load_UnsupportedExtension_Throws()
    {
        Assert.Throws<NotSupportedException>(() => DrawingSession.Load("foo.pdf"));
    }
}