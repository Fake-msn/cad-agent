using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class GateDetectorTests
{
    private static DrawingSummary Summary(params UnknownBrief[] unknowns) =>
        new("test.dxf", "AC1027", new[] { "0" },
            new[] { new EntityBrief("1", "Line", "0") }, unknowns);

    private sealed class FakeMatrix : ISupportMatrix
    {
        private readonly HashSet<string> _ok;
        public FakeMatrix(params string[] ok) => _ok = new HashSet<string>(ok);
        public bool IsSupported(string cppClassName) => _ok.Contains(cppClassName);
    }

    [Fact]
    public void PlainDrawing_IsGreen()
    {
        var report = GateDetector.Inspect(Summary(), new EmptySupportMatrix());
        Assert.Equal(GateLevel.Green, report.Level);
        Assert.Empty(report.Unsupported);
    }

    [Fact]
    public void AllUnsupportedUnknowns_IsRed_WithT3Guidance()
    {
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_WALL", "TCH_WALL", "Tangent")),
            new EmptySupportMatrix());
        Assert.Equal(GateLevel.Red, report.Level);
        Assert.Contains("T3", report.Guidance);
    }

    [Fact]
    public void PartiallySupported_IsYellow()
    {
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_TEXT", "TCH_TEXT", "Tangent"),
                    new UnknownBrief("3", "TCH_WALL", "TCH_WALL", "Tangent")),
            new FakeMatrix("TCH_TEXT"));
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Single(report.Unsupported);
        Assert.Equal("TCH_WALL", report.Unsupported[0].CppClassName);
    }

    [Fact]
    public void AllSupported_IsGreen()
    {
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_TEXT", "TCH_TEXT", "Tangent")),
            new FakeMatrix("TCH_TEXT"));
        Assert.Equal(GateLevel.Green, report.Level);
    }

    // ---- 防假 Green 切片 T2：闸口三分（Red=无原生实体 / Yellow=有原生+未解析）----

    /// <summary>census brief 夹具：与 Summarize 同口径（Handle=""、ObjectName=CppClassName、ApplicationName=""）。</summary>
    private static UnknownBrief UnparsedBrief(string cppClassName) =>
        new("", cppClassName, cppClassName, "");

    [Fact]
    public void Inspect_NativeEntitiesPlusUnparsed_Yellow_ReadOnlyGuidance()
    {
        // Entities 3 条原生 brief + Unknowns 空 + Unparsed TCH_WALL×2：
        // 有原生可解析实体但存在未解析对象 → Yellow（不得假 Green，也不得整图 Red）。
        var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
            new[]
            {
                new EntityBrief("1", "Line", "0"),
                new EntityBrief("2", "Line", "0"),
                new EntityBrief("3", "Circle", "0"),
            },
            Array.Empty<UnknownBrief>(),
            new[] { UnparsedBrief("TCH_WALL"), UnparsedBrief("TCH_WALL") });
        var report = GateDetector.Inspect(summary, new EmptySupportMatrix());
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Equal(2, report.Unsupported.Count);
        Assert.Contains("只读审查", report.Guidance);
        Assert.Contains("T3", report.Guidance);
    }

    [Fact]
    public void Inspect_NoNativeEntities_AllUnparsed_Red()
    {
        // Entities 空 + Unparsed TCH_WALL×2 → 原生可解析实体数==0 且全量未支持 → Red + T3Guidance。
        var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
            Array.Empty<EntityBrief>(), Array.Empty<UnknownBrief>(),
            new[] { UnparsedBrief("TCH_WALL"), UnparsedBrief("TCH_WALL") });
        var report = GateDetector.Inspect(summary, new EmptySupportMatrix());
        Assert.Equal(GateLevel.Red, report.Level);
        Assert.Equal(2, report.Unsupported.Count);
        Assert.Equal(GateDetector.T3Guidance, report.Guidance);
    }

    [Fact]
    public void Inspect_NoUnparsed_Green_Verbatim()
    {
        // Entities 3 + Unknowns 空 + Unparsed null/空 → Green（legacy 逐字：空 Unsupported + 空 guidance）。
        var entities = new[]
        {
            new EntityBrief("1", "Line", "0"),
            new EntityBrief("2", "Line", "0"),
            new EntityBrief("3", "Circle", "0"),
        };
        foreach (var unparsed in new IReadOnlyList<UnknownBrief>?[] { null, Array.Empty<UnknownBrief>() })
        {
            var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
                entities, Array.Empty<UnknownBrief>(), unparsed);
            var report = GateDetector.Inspect(summary, new EmptySupportMatrix());
            Assert.Equal(GateLevel.Green, report.Level);
            Assert.Empty(report.Unsupported);
            Assert.Equal("", report.Guidance);
        }
    }

    [Fact]
    public void Inspect_GraphUnknownsPartialSupport_Yellow_LegacyMatrixPath()
    {
        // 图内 Unknowns 2（1 支持 1 不支持）+ Entities 含该 2 条 brief + 1 条原生
        // → 原生数 = Entities.Count - Unknowns.Count = 1 > 0 → Yellow（legacy 矩阵路径保持）。
        var unknowns = new[]
        {
            new UnknownBrief("2", "TCH_TEXT", "TCH_TEXT", "Tangent"),
            new UnknownBrief("3", "TCH_WALL", "TCH_WALL", "Tangent"),
        };
        var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
            new[]
            {
                new EntityBrief("1", "Line", "0"),
                new EntityBrief("2", "TCH_TEXT", "0"),
                new EntityBrief("3", "TCH_WALL", "0"),
            },
            unknowns);
        var report = GateDetector.Inspect(summary, new FakeMatrix("TCH_TEXT"));
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Single(report.Unsupported);
        Assert.Equal("TCH_WALL", report.Unsupported[0].CppClassName);
        Assert.Contains("只读审查", report.Guidance);
    }
}
