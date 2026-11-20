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

    [Fact]
    public void ComputeHash_StableAcrossNoChangeSaveReload()
    {
        var session = DrawingSession.Load(_path);
        string before = session.ComputeHash();
        session.SaveWorking();
        session.Reload();
        Assert.Equal(before, session.ComputeHash());
        session.SaveWorking();
        session.Reload();
        Assert.Equal(before, session.ComputeHash()); // twice: never converges was the bug
    }

    [Fact]
    public void ComputeHash_ChangesWhenContentChanges()
    {
        var session = DrawingSession.Load(_path);
        string before = session.ComputeHash();
        session.Doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(9, 9), EndPoint = (XYZ)new XY(10, 10) });
        Assert.NotEqual(before, session.ComputeHash());
    }

    [Fact]
    public void WorkingPathFor_Dwg_ReturnsSidecar_Dxf_ReturnsSame()
    {
        Assert.Equal(@"C:\x\a.dwg.working.dxf", DrawingSession.WorkingPathFor(@"C:\x\a.dwg"));
        Assert.Equal(@"C:\x\a.DWG.working.dxf", DrawingSession.WorkingPathFor(@"C:\x\a.DWG"));
        Assert.Equal(@"C:\x\a.dxf", DrawingSession.WorkingPathFor(@"C:\x\a.dxf"));
    }

    [Fact]
    public void ShouldLoadWorkingCopy_ExistsTrue_AbsentFalse()
    {
        Assert.True(DrawingSession.ShouldLoadWorkingCopy(_path));
        Assert.False(DrawingSession.ShouldLoadWorkingCopy(_path + ".missing"));
    }

    [Fact]
    public void Load_Dwg_WorkingCopyExists_LoadsIt_CommittedEditsSurviveSecondInvocation()
    {
        // Critical fix: CLI commands are separate processes. After run commits to
        // <source>.dwg.working.dxf, the next Load(a.dwg) must load THAT working copy,
        // not re-derive it from the pristine .dwg (which would wipe committed edits).
        string dwg = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
        string working = DrawingSession.WorkingPathFor(dwg);
        try
        {
            byte[] originalBytes = File.ReadAllBytes(dwg);

            // Invocation 1: materialize working copy from the .dwg, then "commit" an edit.
            var first = DrawingSession.Load(dwg);
            Assert.Single(first.Summarize().Entities);
            Assert.True(File.Exists(working));
            first.Doc.Entities.Add(new Line { StartPoint = new XYZ(1, 2, 0), EndPoint = new XYZ(3, 4, 0) });
            first.SaveWorking();

            // Invocation 2: fresh Load of the same .dwg must see the committed edit.
            var second = DrawingSession.Load(dwg);
            Assert.Equal(2, second.Summarize().Entities.Count);
            Assert.Equal(working, second.SourcePath);

            // The read-only original .dwg is never written.
            Assert.Equal(originalBytes, File.ReadAllBytes(dwg));
        }
        finally
        {
            File.Delete(dwg);
            if (File.Exists(working)) File.Delete(working);
        }
    }

    [Fact]
    public void Load_Dwg_WorkingCopyAbsent_MaterializesFromSource()
    {
        string dwg = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
        string working = DrawingSession.WorkingPathFor(dwg);
        try
        {
            Assert.False(File.Exists(working));
            var session = DrawingSession.Load(dwg);
            Assert.True(File.Exists(working));
            Assert.Single(session.Summarize().Entities);
            Assert.Equal(working, session.SourcePath);
        }
        finally
        {
            File.Delete(dwg);
            if (File.Exists(working)) File.Delete(working);
        }
    }
}