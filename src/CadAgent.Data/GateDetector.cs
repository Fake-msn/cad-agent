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

    // Yellow（有原生实体+未解析）guidance：T3 导出指引 + 只读审查说明（防假 Green 切片 T2）。
    public const string YellowReadOnlyGuidance =
        T3Guidance + " 当前为只读审查模式：修改操作已被拦截（未解析对象无句柄/几何，禁触区无法定位，全图级拦截）。";

    public static GateReport Inspect(DrawingSummary summary, ISupportMatrix matrix)
    {
        // census（通知侧未解析对象）并入闸口输入：Unparsed null=无 census（legacy 口径）。
        var unparsed = summary.Unparsed ?? Array.Empty<UnknownBrief>();
        var all = summary.Unknowns.Concat(unparsed).ToList();
        if (all.Count == 0)
            return new GateReport(GateLevel.Green, Array.Empty<UnknownBrief>(), "");
        var unsupported = all.Where(u => !matrix.IsSupported(u.CppClassName)).ToList();
        if (unsupported.Count == 0)
            return new GateReport(GateLevel.Green, unsupported, "");
        // 三分判据（R6）：Summarize 口径下 Entities 已排除图内 unknown/proxy（只入 Unknowns），
        // 故原生可解析实体数 = Entities.Count，不得再减 Unknowns.Count（双重扣减会把
        // mixed 图纸误判 Red、把 proxy-only 图纸误判 Yellow）。
        // Red 仅当全量未支持且无任何原生实体；有原生实体则 Yellow（只读审查，拒绝语义 T3 落地）。
        int nativeCount = summary.Entities.Count;
        if (unsupported.Count == all.Count && nativeCount == 0)
            return new GateReport(GateLevel.Red, unsupported, T3Guidance);
        return new GateReport(GateLevel.Yellow, unsupported, YellowReadOnlyGuidance);
    }
}
