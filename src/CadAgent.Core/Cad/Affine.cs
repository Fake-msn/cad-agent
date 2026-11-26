using ACadSharp.Entities;

namespace CadAgent.Core.Cad;

/// <summary>
/// 2D affine map x' = A*x + B*y + Tx, y' = C*x + D*y + Ty. Composed through
/// nested insert expansion so block geometry lands in drawing space.
/// （R1 数学 consolidation：自 View RenderModelBuilder 私有定义逐字移入 Core，
/// 渲染器/幅面登记簿/块遍历共用同一仿射口径。）
/// </summary>
public readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
{
    public static Affine Identity => new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);

    /// <summary>this(inner(p)) — inner transform applied first, then this one.</summary>
    public Affine Compose(Affine inner) => new(
        A * inner.A + B * inner.C,
        A * inner.B + B * inner.D,
        C * inner.A + D * inner.C,
        C * inner.B + D * inner.D,
        A * inner.Tx + B * inner.Ty + Tx,
        C * inner.Tx + D * inner.Ty + Ty);

    /// <summary>
    /// 线性部逆（Tx/Ty 置 0）：世界/纸面增量→块局部增量用。det≈0 → false 且 <paramref name="inverse"/> = default，
    /// 调用方必须检查返回值再用 <paramref name="inverse"/>。阈值 1e-12 为<b>绝对值</b>：常规块缩放比范围内足够，
    /// 近奇异的超大尺度矩阵（如 A~1e6）可能仍判为可逆（已知局限）。
    /// </summary>
    public bool TryInvertLinear(out Affine inverse)
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) { inverse = default; return false; }
        inverse = new Affine(D / det, -B / det, -C / det, A / det, 0, 0);
        return true;
    }

    /// <summary>
    /// Radius scale approximation: geometric mean of the column norms (exact
    /// singular values for a single R*S; circles under non-uniform scale become
    /// ellipses in true CAD — approximated here, documented limitation).
    /// </summary>
    public double RadiusScale => Math.Sqrt(Math.Sqrt(A * A + C * C) * Math.Sqrt(B * B + D * D));

    /// <summary>
    /// 插入变换：p' = R(rotation) * S(xScale,yScale) * (p - blockBasePoint) + insertPoint。
    /// 缩放先于旋转（与 Insert.GetTransform 矩阵一致，探针核定）；块基点可空（无 BlockEntity→0）。
    /// </summary>
    public static Affine InsertAffine(Insert ins)
    {
        double cos = Math.Cos(ins.Rotation), sin = Math.Sin(ins.Rotation);
        double a = cos * ins.XScale, b = -sin * ins.YScale;
        double c = sin * ins.XScale, d = cos * ins.YScale;
        CSMath.XYZ? bp = ins.Block?.BlockEntity?.BasePoint;
        double bx = bp?.X ?? 0, by = bp?.Y ?? 0;
        double tx = ins.InsertPoint.X - (a * bx + b * by);
        double ty = ins.InsertPoint.Y - (c * bx + d * by);
        return new Affine(a, b, c, d, tx, ty);
    }
}
