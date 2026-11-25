using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// A1（技术债批次A·协议下沉）：退化包围盒判据 <see cref="ExtentsPolicy.IsDegenerate"/> 的边界面。
/// 契约：**恰好** 6 个有限分量且**每个** |v| &lt; 1e-6 才为退化（true）；
/// null / 长度≠6 / 非有限（NaN、∞）一律 false（"读不到"不得冒充"读到的就是零"）。
/// 边界按等价类划三点：紧邻下方（0.999e-6）真、恰在阈值（1e-6）假、紧邻上方（1.000001e-6）假。
/// </summary>
public class ExtentsPolicyTests
{
    private static double[] Six(double v) => new[] { v, v, v, v, v, v };

    /// <summary>边界表：判据名 / 输入 / 期望（判据名只用于失败信息，不参与断言）。</summary>
    public static TheoryData<string, double[]?, bool> Cases() => new()
    {
        { "全零 → 退化", Six(0.0), true },
        { "0.999e-6（紧邻下方） → 退化", Six(0.999e-6), true },
        { "-0.999e-6（负向紧邻下方） → 退化", Six(-0.999e-6), true },
        { "恰在阈值 1e-6 → 不退化（严格小于）", Six(1e-6), false },
        { "1.000001e-6（紧邻上方） → 不退化", Six(1.000001e-6), false },
        { "单个分量 1e-5 → 不退化", new[] { 0.0, 0.0, 0.0, 0.0, 1e-5, 0.0 }, false },
        { "null → 不退化（读不到 ≠ 读到的就是零）", null, false },
        { "长度 5 → 不退化（分量数不足非 6）", new[] { 0.0, 0.0, 0.0, 0.0, 0.0 }, false },
        { "长度 7 → 不退化（分量数超出非 6）", new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }, false },
        { "含 NaN → 不退化（非有限不得当零）", new[] { 0.0, 0.0, double.NaN, 0.0, 0.0, 0.0 }, false },
        { "含 +∞ → 不退化", new[] { 0.0, 0.0, 0.0, double.PositiveInfinity, 0.0, 0.0 }, false },
        { "全 -∞ → 不退化", Six(double.NegativeInfinity), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsDegenerate_BoundaryTable(string label, double[]? bbox6, bool expected)
    {
        Assert.True(expected == ExtentsPolicy.IsDegenerate(bbox6), $"判据不符：{label}");
    }
}
