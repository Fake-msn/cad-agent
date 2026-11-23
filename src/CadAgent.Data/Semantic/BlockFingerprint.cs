using System.Security.Cryptography;
using System.Text;
using ACadSharp.Entities;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 块几何指纹（spec §13.2）：块定义直接几何 LINE/CIRCLE/ARC → bbox 平移归零+最长边归一
/// → 量化 1e-3 → 规范排序 SHA256。TEXT 不入哈希（注记随实例变化）。
/// 插入 Scale/Rotation 作用于实例不进块定义——指纹天然插入变换不变。
/// R1 API 定型：点属性为 CSMath.XYZ；Arc.StartAngle/EndAngle 为弧度
/// （DoorArcDetector 已对 ACadSharp 3.8.35 核实）；Arc : Circle 继承，
/// switch 用 <c>when e is not Arc</c> 守卫防止圆分支吞掉弧。
/// </summary>
public static class BlockFingerprint
{
    /// <summary>近似匹配阈值：几何距离 ≤ 0.15 视为近配（三级匹配第二档）。</summary>
    public const double NearThreshold = 0.15;

    /// <summary>置信公式：d=0 → 0.9；d=NearThreshold → 0.6（闸下保证 &lt; 0.8）。</summary>
    public static double Confidence(double d) => 0.9 - 2 * d;

    /// <summary>归一化几何：坐标已平移归零+最长边归一+量化；Aspect 为归一化前原始 bbox 的 min(w,h)/max(w,h)∈[0,1]。</summary>
    public sealed record NormGeom(
        IReadOnlyList<(double X1, double Y1, double X2, double Y2)> Lines,
        IReadOnlyList<(double CX, double CY, double R)> Circles,
        IReadOnlyList<(double CX, double CY, double R, double A0, double A1)> Arcs,
        double Aspect);

    /// <summary>指纹 = 规范串 SHA256 大写 hex + 归一化几何（供结构签名与距离计算消费）。</summary>
    public sealed record Fingerprint(string Hash, NormGeom Geom);

    /// <summary>
    /// 提取块几何指纹。仅 LINE/CIRCLE/ARC 入哈希；NaN/∞ 坐标实体整条剔除；
    /// 无白名单几何或最长边 &lt; 1e-9（退化）→ null。
    /// </summary>
    public static Fingerprint? Extract(IEnumerable<Entity> entities)
    {
        var lines = new List<(double, double, double, double)>();
        var circles = new List<(double, double, double)>();
        var arcs = new List<(double, double, double, double, double)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        // bbox 累积（仅有限坐标；圆/弧按外接矩形四角贡献）
        void Pt(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            if (x < minX) minX = x; if (y < minY) minY = y;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }
        foreach (var e in entities)
        {
            switch (e)
            {
                case Line l:
                    if (!Finite(l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y)) continue;
                    lines.Add((l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y));
                    Pt(l.StartPoint.X, l.StartPoint.Y); Pt(l.EndPoint.X, l.EndPoint.Y); break;
                case Circle c when e is not Arc: // Arc : Circle——守卫排除弧，弧走下方分支
                    if (!Finite(c.Center.X, c.Center.Y, c.Radius)) continue;
                    circles.Add((c.Center.X, c.Center.Y, c.Radius));
                    Pt(c.Center.X - c.Radius, c.Center.Y - c.Radius); Pt(c.Center.X + c.Radius, c.Center.Y + c.Radius); break;
                case Arc a:
                    if (!Finite(a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle)) continue;
                    arcs.Add((a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle));
                    Pt(a.Center.X - a.Radius, a.Center.Y - a.Radius); Pt(a.Center.X + a.Radius, a.Center.Y + a.Radius); break;
            }
        }
        if (lines.Count + circles.Count + arcs.Count == 0) return null; // 无白名单几何
        double w = maxX - minX, h = maxY - minY, scale = Math.Max(w, h);
        if (scale < 1e-9) return null; // 退化 bbox（点状/数值噪声）
        double aspect = Math.Min(w, h) / scale; // 原始 bbox 纵横比（归一化前）
        // 量化 1e-3：消除浮点噪声，保证平移/均匀缩放后逐位相等
        double Q(double v) => Math.Round(v, 3);
        var nl = lines.Select(t => (Q((t.Item1 - minX) / scale), Q((t.Item2 - minY) / scale), Q((t.Item3 - minX) / scale), Q((t.Item4 - minY) / scale)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ToList();
        var nc = circles.Select(t => (Q((t.Item1 - minX) / scale), Q((t.Item2 - minY) / scale), Q(t.Item3 / scale)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ToList();
        // 弧角为弧度，旋转不变量仅半径/圆心；角度原样量化入串（旋转块定义会改角度→哈希不同，符合预期）
        var na = arcs.Select(t => (Q((t.Item1 - minX) / scale), Q((t.Item2 - minY) / scale), Q(t.Item3 / scale), Q(t.Item4), Q(t.Item5)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ThenBy(t => t.Item5).ToList();
        // 规范串：类前缀 + 不变文化格式（避免区域设置小数点差异）；排序已保证实体顺序无关
        var sb = new StringBuilder();
        foreach (var t in nl) sb.Append(FormattableString.Invariant($"L{t.Item1},{t.Item2},{t.Item3},{t.Item4};"));
        foreach (var t in nc) sb.Append(FormattableString.Invariant($"C{t.Item1},{t.Item2},{t.Item3};"));
        foreach (var t in na) sb.Append(FormattableString.Invariant($"A{t.Item1},{t.Item2},{t.Item3},{t.Item4},{t.Item5};"));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))); // 大写 hex
        return new Fingerprint(hash, new NormGeom(nl, nc, na, aspect));
    }

    // 全部分量有限（NaN/∞ 任一 → 整条实体剔除）
    private static bool Finite(params double[] vs) => vs.All(double.IsFinite);

    /// <summary>结构签名相容：三类计数逐等 && |Aspect 差| ≤ 0.2。</summary>
    public static bool Compatible(Fingerprint a, Fingerprint b) =>
        a.Geom.Lines.Count == b.Geom.Lines.Count
        && a.Geom.Circles.Count == b.Geom.Circles.Count
        && a.Geom.Arcs.Count == b.Geom.Arcs.Count
        && Math.Abs(a.Geom.Aspect - b.Geom.Aspect) <= 0.2;

    /// <summary>贪心逐类最近邻匹配平均距离（不 Compatible→1.0）。线允许方向反转；弧角差权重 0.5。</summary>
    public static double Distance(Fingerprint a, Fingerprint b)
    {
        if (!Compatible(a, b)) return 1.0;
        double sum = 0; int n = 0;
        sum += MatchLines(a.Geom.Lines, b.Geom.Lines); n += a.Geom.Lines.Count;
        sum += MatchCircles(a.Geom.Circles, b.Geom.Circles); n += a.Geom.Circles.Count;
        sum += MatchArcs(a.Geom.Arcs, b.Geom.Arcs); n += a.Geom.Arcs.Count;
        return n == 0 ? 1.0 : Math.Min(1.0, sum / n);
    }

    // 平面欧氏距离（归一化坐标空间）
    private static double PtDist(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    // 线匹配：端点对平均距离，取正向/反向较小者（方向反转不变）；贪心占用防重复匹配
    private static double MatchLines(IReadOnlyList<(double X1, double Y1, double X2, double Y2)> a, IReadOnlyList<(double X1, double Y1, double X2, double Y2)> b)
    {
        var used = new bool[b.Count]; double sum = 0;
        foreach (var x in a)
        {
            double best = double.MaxValue; int bi = -1;
            for (int j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var y = b[j];
                double d = Math.Min(
                    (PtDist(x.X1, x.Y1, y.X1, y.Y1) + PtDist(x.X2, x.Y2, y.X2, y.Y2)) / 2,
                    (PtDist(x.X1, x.Y1, y.X2, y.Y2) + PtDist(x.X2, x.Y2, y.X1, y.Y1)) / 2);
                if (d < best) { best = d; bi = j; }
            }
            if (bi >= 0) { used[bi] = true; sum += best; }
        }
        return sum;
    }

    // 圆匹配：圆心距 + 半径差
    private static double MatchCircles(IReadOnlyList<(double CX, double CY, double R)> a, IReadOnlyList<(double CX, double CY, double R)> b)
    {
        var used = new bool[b.Count]; double sum = 0;
        foreach (var x in a)
        {
            double best = double.MaxValue; int bi = -1;
            for (int j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var y = b[j];
                double d = PtDist(x.CX, x.CY, y.CX, y.CY) + Math.Abs(x.R - y.R);
                if (d < best) { best = d; bi = j; }
            }
            if (bi >= 0) { used[bi] = true; sum += best; }
        }
        return sum;
    }

    // 弧匹配：圆心距 + 半径差 + 0.5×起止角平均环形差（弧度制）
    private static double MatchArcs(IReadOnlyList<(double CX, double CY, double R, double A0, double A1)> a, IReadOnlyList<(double CX, double CY, double R, double A0, double A1)> b)
    {
        var used = new bool[b.Count]; double sum = 0;
        foreach (var x in a)
        {
            double best = double.MaxValue; int bi = -1;
            for (int j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var y = b[j];
                double d = PtDist(x.CX, x.CY, y.CX, y.CY) + Math.Abs(x.R - y.R)
                    + 0.5 * (AngDiff(x.A0, y.A0) + AngDiff(x.A1, y.A1)) / 2;
                if (d < best) { best = d; bi = j; }
            }
            if (bi >= 0) { used[bi] = true; sum += best; }
        }
        return sum;
    }

    // 环形角差（弧度）：模 2π 后取短弧，∈[0,π]
    private static double AngDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % (2 * Math.PI);
        return Math.Min(d, 2 * Math.PI - d);
    }
}
