using CadAgent.Core;

namespace CadAgent.Data;

public enum GateLevel { Green, Yellow, Red }

public sealed record GateReport(
    GateLevel Level,
    IReadOnlyList<UnknownBrief> Unsupported,
    string Guidance);

public static class GateDetector
{
    public const string T3Guidance =
        "图纸含未完整解析的自定义对象（如天正 TCH_*）。请在天正中执行【文件布图→图形导出】，" +
        "保存类型选择天正T3文件(*.t3)后重新导入；或等待解析支持矩阵点亮对应对象类。";

    public static GateReport Inspect(DrawingSummary summary, ISupportMatrix matrix)
    {
        if (summary.Unknowns.Count == 0)
            return new GateReport(GateLevel.Green, Array.Empty<UnknownBrief>(), "");

        var unsupported = summary.Unknowns
            .Where(u => !matrix.IsSupported(u.CppClassName))
            .ToList();

        if (unsupported.Count == 0)
            return new GateReport(GateLevel.Green, unsupported, "");
        if (unsupported.Count == summary.Unknowns.Count)
            return new GateReport(GateLevel.Red, unsupported, T3Guidance);
        return new GateReport(GateLevel.Yellow, unsupported, T3Guidance);
    }
}