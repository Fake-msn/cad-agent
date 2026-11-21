using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Cad;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3j T2：ComReplayService 计划级 COM 重放映射（fake runtime 记录调用）——
/// 生成类 kind 映射/几何同源/不支持 kind 响亮失败/缺块失败。
/// </summary>
public class ComReplayServiceTests : IDisposable
{
    private readonly string _path;
    private readonly RecordingRuntime _rt = new();

    public ComReplayServiceTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("LUM");
        block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-1, 0), EndPoint = (XYZ)new XY(1, 0) });
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(0, 0) });
        doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(2000, 0) });
    });
    public void Dispose() => File.Delete(_path);

    private sealed class RecordingRuntime : ICadRuntime
    {
        public List<string> Calls { get; } = new();
        public HashSet<string> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase) { "LUM" };
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) => Calls.Add("open");
        public void SaveAs(string p) => Calls.Add("saveas");
        public void Close(bool s) => Calls.Add("close");
        public void Restart() => Calls.Add("restart");
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) =>
            Calls.Add($"line:{x1},{y1},{x2},{y2},{layer}");
        public void AddText(double x, double y, string text, double height, string? layer = null) =>
            Calls.Add($"text:{x},{y},{text},{height},{layer}");
        public void AddCircle(double x, double y, double r, string? layer = null) =>
            Calls.Add($"circle:{x},{y},{r}");
        public void AddInsert(string block, double x, double y, double scale = 1, double rot = 0, string? layer = null)
        {
            if (!Blocks.Contains(block)) throw new CadRuntimeException($"块不存在: {block}");
            Calls.Add($"insert:{block},{x},{y},{scale},{rot},{layer}");
        }
        public void EnsureBlock(string name, IReadOnlyList<BlockPrimitive> prims)
        {
            Blocks.Add(name);
            Calls.Add($"block:{name},{prims.Count}");
        }
        public int EntityCount() => Calls.Count(c => c.StartsWith("line:") || c.StartsWith("text:") || c.StartsWith("insert:") || c.StartsWith("circle:"));
        public void Dispose() { }
    }

    private static StepPlan Plan(string json) =>
        StepPlanParser.Parse("{\"planId\":\"T\",\"steps\":" + json + "}");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void AddLineAddText_MapCoordinates()
    {
        var svc = new ComReplayService(_rt);
        var res = svc.Replay(Plan("[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10,\"layer\":\"L1\"}}," +
            "{\"kind\":\"add-text\",\"params\":{\"x\":5,\"y\":5,\"text\":\"标注\",\"height\":3.5}}]"),
            DrawingSession.Load(_path));
        Assert.All(res, r => Assert.True(r.Ok));
        Assert.Contains("line:0,0,10,10,L1", _rt.Calls);
        Assert.Contains("text:5,5,标注,3.5,", _rt.Calls);
    }

    [Fact]
    public void LayoutLighting_GridSixInserts()
    {
        var svc = new ComReplayService(_rt);
        var res = svc.Replay(Plan("[{\"kind\":\"layout-lighting\",\"params\":{\"x\":0,\"y\":0,\"width\":2000,\"depth\":3000,\"spacing\":1000,\"block\":\"LUM\"}}]"),
            DrawingSession.Load(_path));
        Assert.True(res[0].Ok);
        Assert.Equal(6, _rt.Calls.Count(c => c.StartsWith("insert:LUM,")));
        Assert.Contains("insert:LUM,500,500,1,0,", _rt.Calls);
    }

    [Fact]
    public void LabelLuminaires_FromShadowInserts()
    {
        var svc = new ComReplayService(_rt);
        var res = svc.Replay(Plan("[{\"kind\":\"label-luminaires\",\"params\":{\"prefix\":\"L\"}}]"),
            DrawingSession.Load(_path));
        Assert.True(res[0].Ok);
        Assert.Equal(14, _rt.Calls.Count(c => c.StartsWith("line:")));
        Assert.Equal(2, _rt.Calls.Count(c => c.StartsWith("text:")));
        Assert.Contains(_rt.Calls, c => c.StartsWith("text:") && c.Contains(",L1,"));
        Assert.Contains(_rt.Calls, c => c.StartsWith("text:") && c.Contains(",L2,"));
    }

    [Fact]
    public void InsertSymbol_EnsureBlockThenInsert()
    {
        var pkg = SkillLoader.LoadDirectory(Path.Combine(RepoRoot(), "examples", "skills", "electrical"));
        var svc = new ComReplayService(_rt, new[] { pkg });
        var res = svc.Replay(Plan("[{\"kind\":\"insert-symbol\",\"params\":{\"symbol\":\"端子\",\"x\":10,\"y\":20}}]"),
            DrawingSession.Load(_path));
        Assert.True(res[0].Ok);
        Assert.Contains("block:端子,1", _rt.Calls);
        Assert.Contains("insert:端子,10,20,1,0,", _rt.Calls);
    }

    [Fact]
    public void UnsupportedKind_FailsLoudly()
    {
        var svc = new ComReplayService(_rt);
        var res = svc.Replay(Plan("[{\"kind\":\"delete-entity\",\"params\":{\"handle\":1}}]"),
            DrawingSession.Load(_path));
        Assert.False(res[0].Ok);
        Assert.Contains("不支持步骤类型", res[0].Error);
    }

    [Fact]
    public void MissingBlockInVendor_Fails()
    {
        var svc = new ComReplayService(_rt);
        var res = svc.Replay(Plan("[{\"kind\":\"layout-lighting\",\"params\":{\"x\":0,\"y\":0,\"width\":2000,\"depth\":3000,\"spacing\":1000,\"block\":\"NOPE\"}}]"),
            DrawingSession.Load(_path));
        Assert.False(res[0].Ok);
        Assert.Contains("块不存在", res[0].Error);
    }
}
