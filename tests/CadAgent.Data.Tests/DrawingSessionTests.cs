using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
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
        session.InvalidateHash(); // 直接改 Doc：须显式失效（项3 §5）
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

    // ===== R9：陈旧 working 假 Green 旁路闭合（切片前物化的 working 无 TCH 对象 + 无 sidecar）=====

    /// <summary>在指定路径写一份无 TCH 对象的 working DXF（模拟切片前物化的陈旧副本）。</summary>
    private static void WriteStaleWorking(string workingPath)
    {
        var doc = new CadDocument();
        DxfFixture.AddSampleLine(doc);
        using var fs = File.Create(workingPath);
        DxfWriter.Write(fs, doc);
    }

    [Fact]
    public void Load_Dwg_StaleWorking_SourceMissing_ThrowsInvalidOperation()
    {
        // working-only 无源且无 census → 无法核验完整性，必须拒绝（不得假 Green 放行）。
        string dir = Directory.CreateTempSubdirectory("cadagent-r9-").FullName;
        try
        {
            string dwg = Path.Combine(dir, "ghost.dwg"); // 源不存在
            WriteStaleWorking(DrawingSession.WorkingPathFor(dwg));
            var ex = Assert.Throws<InvalidOperationException>(() => DrawingSession.Load(dwg));
            Assert.Contains("无法核验完整性", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_Dwg_StaleWorking_NoPersisted_RereadsSourceForCensus()
    {
        // persisted Empty/缺 + 源存在 → 必须从源 DWG 通知重建 census（一次性，doc 丢弃）。
        // 合成源无 TCH → census 恒 Empty，无法直接断言非空（真实标本 skip-gated 测试钉住）；
        // 此处以"源损坏→Load 抛"证明源重读确实发生（修复前 working 直载成功 = 假 Green 旁路）。
        string dwg = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
        string working = DrawingSession.WorkingPathFor(dwg);
        try
        {
            DrawingSession.Load(dwg); // 首载物化 working
            File.WriteAllBytes(dwg, new byte[] { 0x00, 0x01, 0x02, 0x03 }); // 源损坏（不可读）
            // R10 收紧：源损坏（4 字节垃圾）时 DwgReader.readFileHeader 经 StreamIO.ReadBytes
            // 抛 EndOfStreamException（IOException 子类，实测核定 data-red-probe.log）。
            Assert.Throws<EndOfStreamException>(() => DrawingSession.Load(dwg));
        }
        finally
        {
            File.Delete(dwg);
            if (File.Exists(working)) File.Delete(working);
        }
    }

    [Fact]
    public void Load_Dwg_WorkingWithPersistedCensus_DoesNotRereadSource()
    {
        // persisted 非 Empty → 用之，不重读源（性能/行为保持）：源损坏也不影响加载。
        string dwg = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
        string working = DrawingSession.WorkingPathFor(dwg);
        try
        {
            DrawingSession.Load(dwg); // 首载物化 working
            File.WriteAllBytes(dwg, new byte[] { 0x00, 0x01, 0x02, 0x03 }); // 源损坏
            var persisted = new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) });
            var session = DrawingSession.Load(dwg, persisted);
            Assert.Equal(2, session.Unparsed.TotalCount); // Yellow 证据保持
            Assert.Equal(working, session.SourcePath);
            Assert.Single(session.Summarize().Entities); // working 仍是持久编辑态
        }
        finally
        {
            File.Delete(dwg);
            if (File.Exists(working)) File.Delete(working);
        }
    }

    [Fact]
    public void Load_Dwg_WorkingWithPersistedCensus_SourceDeleted_KeepsCensus()
    {
        // sidecar census 非空 + 源删除 → Load 层面仍用 persisted（Yellow 保持，不拒绝也不假 Green）。
        // （CLI 层 sidecar 源哈希核验归 UnparsedCensusStore——源缺失时 Store 诚实 Empty→拒绝，另测。）
        string dwg = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
        string working = DrawingSession.WorkingPathFor(dwg);
        try
        {
            DrawingSession.Load(dwg); // 首载物化 working
            File.Delete(dwg); // 源删除
            var persisted = new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) });
            var session = DrawingSession.Load(dwg, persisted);
            Assert.Equal(2, session.Unparsed.TotalCount);
            Assert.Equal(working, session.SourcePath);
        }
        finally
        {
            if (File.Exists(dwg)) File.Delete(dwg);
            if (File.Exists(working)) File.Delete(working);
        }
    }
}
