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
    public void AllUnsupportedUnknowns_WithNativeEntity_IsYellow_WithGuidance()
    {
        // R6 修正：Summary 夹具含 1 条原生 Line + 1 条图内 unknown（mixed）。
        // Summarize 口径下 Entities 已排除图内 unknown → nativeCount=1>0，
        // 即使全量未支持也判 Yellow（spec 🟡 本义），不得误判 Red。
        var report = GateDetector.Inspect(
            Summary(new UnknownBrief("2", "TCH_WALL", "TCH_WALL", "Tangent")),
            new EmptySupportMatrix());
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Single(report.Unsupported);
        Assert.Equal("TCH_WALL", report.Unsupported[0].CppClassName);
        Assert.Contains("T3", report.Guidance);
        Assert.Contains("只读审查", report.Guidance);
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
    public void Inspect_MixedNativeAndGraphUnknowns_AllUnsupported_Yellow()
    {
        // R6 覆盖：Entities 2 条原生 + Unknowns 2（全未支持）+ Unparsed null。
        // Summarize 口径下 Entities 已排除图内 unknown → nativeCount=2>0，
        // mixed 全未支持 → Yellow（旧实现 Entities-Unknowns=0 双重扣减误判 Red）。
        var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
            new[]
            {
                new EntityBrief("1", "Line", "0"),
                new EntityBrief("2", "Circle", "0"),
            },
            new[]
            {
                new UnknownBrief("3", "TCH_WALL", "TCH_WALL", "Tangent"),
                new UnknownBrief("4", "TCH_WALL", "TCH_WALL", "Tangent"),
            });
        var report = GateDetector.Inspect(summary, new EmptySupportMatrix());
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Equal(2, report.Unsupported.Count);
        Assert.All(report.Unsupported, u => Assert.Equal("TCH_WALL", u.CppClassName));
        Assert.Contains("只读审查", report.Guidance);
        Assert.Contains("T3", report.Guidance);
    }

    [Fact]
    public void Inspect_ProxyOnlyUnknowns_NoNative_Red()
    {
        // R6 覆盖：Entities 空（proxy-only 图纸）+ Unknowns 2 全未支持 + Unparsed null
        // → nativeCount=0 且全量未支持 → Red + T3Guidance 逐字
        // （旧实现 Entities-Unknowns=-2≠0 误判 Yellow）。
        var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
            Array.Empty<EntityBrief>(),
            new[]
            {
                new UnknownBrief("1", "TCH_WALL", "TCH_WALL", "Tangent"),
                new UnknownBrief("2", "TCH_WALL", "TCH_WALL", "Tangent"),
            });
        var report = GateDetector.Inspect(summary, new EmptySupportMatrix());
        Assert.Equal(GateLevel.Red, report.Level);
        Assert.Equal(2, report.Unsupported.Count);
        Assert.Equal(GateDetector.T3Guidance, report.Guidance);
    }

    [Fact]
    public void Inspect_GraphUnknownsPartialSupport_Yellow_LegacyMatrixPath()
    {
        // 图内 Unknowns 2（1 支持 1 不支持）+ Entities 仅 1 条原生（Summarize 实际口径：
        // unknown/proxy 只入 Unknowns、不入 Entities）→ nativeCount=Entities.Count=1>0
        // → Yellow（legacy 矩阵路径保持）。
        var unknowns = new[]
        {
            new UnknownBrief("2", "TCH_TEXT", "TCH_TEXT", "Tangent"),
            new UnknownBrief("3", "TCH_WALL", "TCH_WALL", "Tangent"),
        };
        var summary = new DrawingSummary("test.dxf", "AC1027", new[] { "0" },
            new[] { new EntityBrief("1", "Line", "0") },
            unknowns);
        var report = GateDetector.Inspect(summary, new FakeMatrix("TCH_TEXT"));
        Assert.Equal(GateLevel.Yellow, report.Level);
        Assert.Single(report.Unsupported);
        Assert.Equal("TCH_WALL", report.Unsupported[0].CppClassName);
        Assert.Contains("只读审查", report.Guidance);
    }
}
