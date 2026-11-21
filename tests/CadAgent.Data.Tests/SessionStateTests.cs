using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3b-ii T1：会话状态捕获/恢复（步级 undo 基础）——内存 DXF 往返，不触磁盘文件。
/// </summary>
public class SessionStateTests : IDisposable
{
    private readonly string _path;
    public SessionStateTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void CaptureRestore_RoundTrip_UndoEntityAdd()
    {
        var s = DrawingSession.Load(_path);
        byte[] state = s.CaptureState();
        s.Doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(5, 5) });
        Assert.Equal(2, s.Summarize().Entities.Count);
        s.RestoreState(state);
        Assert.Single(s.Summarize().Entities);
    }

    [Fact]
    public void CaptureState_DoesNotMutateFile()
    {
        var s = DrawingSession.Load(_path);
        byte[] before = File.ReadAllBytes(_path);
        s.CaptureState();
        Assert.Equal(before, File.ReadAllBytes(_path));
    }
}