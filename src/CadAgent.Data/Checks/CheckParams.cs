using System.Globalization;

namespace CadAgent.Data.Checks;

/// <summary>
/// 检查器参数/数值辅助：CheckSpec.Params 来自 YamlDotNet——标量可能是 string 或 double，
/// 序列为 List&lt;object&gt;；统一以 InvariantCulture 转换。键为 camelCase（hanVector/other/groups）。
/// 系列归属容差 0.01mm（字高/线宽共用）；缺键按空系列处理（并集语义）。
/// </summary>
internal static class CheckParams
{
    public const double ToleranceMm = 0.01;

    public static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    public static double[] ToDoubleList(object value) => value is IEnumerable<object> seq
        ? seq.Select(ToDouble).ToArray()
        : throw new InvalidOperationException($"期望数值列表参数，实际类型 {value.GetType().Name}");

    public static double[] Series(Dictionary<string, object> parameters, string key) =>
        parameters.TryGetValue(key, out var raw) ? ToDoubleList(raw) : Array.Empty<double>();

    public static bool InSeries(double value, double[] series) =>
        series.Any(s => Math.Abs(value - s) <= ToleranceMm);

    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string FormatSeries(double[] series) => string.Join("、", series.Select(Format));
}
