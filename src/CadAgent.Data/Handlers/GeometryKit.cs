namespace CadAgent.Data.Handlers;

/// <summary>
/// 3j 共享几何数学（handler 与 COM 重放同源，防双写漂移）：
/// 布灯网格点位 / 设备索引符号六边形线组 / 编号文本放置。
/// </summary>
public static class GeometryKit
{
    public static IReadOnlyList<(double X, double Y)> GridPoints(double x, double y, double width, double depth, double spacing)
    {
        var pts = new List<(double, double)>();
        for (double px = x + spacing / 2; px < x + width; px += spacing)
            for (double py = y + spacing / 2; py < y + depth; py += spacing)
                pts.Add((px, py));
        return pts;
    }

    /// <summary>六边形 6 边 + 水平内径线（长轴水平=size），JGJ/T 244-2011 3.6.6-4。</summary>
    public static IReadOnlyList<(double X1, double Y1, double X2, double Y2)> HexagonLines(double cx, double cy, double size)
    {
        double r = size / 2;
        var v = new (double X, double Y)[6];
        for (int k = 0; k < 6; k++)
        {
            double a = k * Math.PI / 3;
            v[k] = (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        var lines = new List<(double, double, double, double)>();
        for (int k = 0; k < 6; k++)
            lines.Add((v[k].X, v[k].Y, v[(k + 1) % 6].X, v[(k + 1) % 6].Y));
        lines.Add((v[3].X, v[3].Y, v[0].X, v[0].Y));
        return lines;
    }

    public static (double X, double Y, double Height) LabelPlacement(double cx, double cy, double size, string label)
    {
        double h = Math.Max(2.5, size / 4);
        return (cx - label.Length * h * 0.35, cy - h / 2, h);
    }
}
