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
}