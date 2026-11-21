using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

/// <summary>
/// 合并前审查 Critical#1 回归：SaveWorking 原子写（tmp+Move）——落盘完整、无 tmp 残留、重载一致。
/// </summary>
public class SaveWorkingAtomicTests : IDisposable
{
    private readonly string _path;
    public SaveWorkingAtomicTests() => _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose()
    {
        File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }

    [Fact]
    public void SaveWorking_Atomic_ReloadConsistent_NoTmpLeft()
    {
        var s = DrawingSession.Load(_path);
        s.Doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(1, 1), EndPoint = (XYZ)new XY(2, 2) });
        s.SaveWorking();
        Assert.False(File.Exists(_path + ".tmp"));
        var re = DrawingSession.Load(_path);
        Assert.Equal(2, re.Doc.Entities.Count());
    }
}
