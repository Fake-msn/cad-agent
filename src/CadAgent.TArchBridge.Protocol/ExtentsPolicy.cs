using System;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// 包围盒退化判据（技术债批次A·协议下沉）：桥内 A1 口径从命令实现提到协议层，
/// 宿主/测试与桥共用**一份**判据（此前判据只活在 net45 桥里，测试无从钉住）。
/// 退化 = 零尺寸/无几何实体给出的伪包围盒——不得冒充"0,0,0..0,0,0"的可用包围盒。
/// </summary>
public static class ExtentsPolicy
{
    /// <summary>六分量全部近零即退化：**恰好** 6 个分量、每个均为有限值、且每个 |v| &lt; 1e-6。</summary>
    /// <remarks>
    /// 边界为严格小于：恰在阈值（1e-6）**不**算退化（阈值上的盒仍是可核验的零尺寸盒）。
    /// null / 长度≠6 / 非有限（NaN、±∞）一律 false——"读不到"与"读到的就是零"是不同事实，
    /// 不得靠退化判据互相冒充（前者由宿主按 bbox=null + FAIL 原因另行披露）。
    /// </remarks>
    public static bool IsDegenerate(double[]? bbox6)
    {
        if (bbox6 == null || bbox6.Length != 6) return false;
        for (int i = 0; i < bbox6.Length; i++)
        {
            double v = bbox6[i];
            // netstandard2.0/net45 无 double.IsFinite（netstandard2.1 才引入）：显式判 NaN/∞。
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            if (!(Math.Abs(v) < 1e-6)) return false;
        }
        return true;
    }
}
