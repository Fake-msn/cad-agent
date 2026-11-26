using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core.Cad;
using CSMath;

namespace CadAgent.Core.Tests;

/// <summary>
/// Plan-3 T2: Affine 线性部逆——世界/纸面增量换算回块局部增量（块定义编辑用）。
/// </summary>
public class AffineInverseTests
{
    [Fact]
    public void InsertAffine_Rotation30_NonUniformScale_InverseRecoversLocalDelta()
    {
        var block = new BlockRecord("B");
        var ins = new Insert(block)
        {
            InsertPoint = new XYZ(120, -40, 0),
            Rotation = Math.PI / 6,
            XScale = 2,
            YScale = 0.5,
            ZScale = 1
        };
        var t = Affine.InsertAffine(ins);
        Assert.True(t.TryInvertLinear(out var inverse));

        var deltas = new[] { (3.7, -1.9), (0.0, 0.0), (-12.5, 8.25), (1e-4, 1e3) };
        foreach (var (dx, dy) in deltas)
        {
            var p0 = t.Apply(1.5, -2.5);
            var p1 = t.Apply(1.5 + dx, -2.5 + dy);
            var back = inverse.Apply(p1.X - p0.X, p1.Y - p0.Y);
            Assert.Equal(dx, back.X, 9);
            Assert.Equal(dy, back.Y, 9);
        }
    }

    [Fact]
    public void InsertAffine_MirroredScale_DetMinusHalf_InverseRecoversLocalDelta()
    {
        // XScale=2, YScale=-0.25（镜像）→ det = 2*-0.25 = -0.5：缺 /det 或用 |det| 的实现必失败。
        var block = new BlockRecord("B");
        var ins = new Insert(block)
        {
            InsertPoint = new XYZ(-7.25, 33.5, 0),
            Rotation = Math.PI / 5,
            XScale = 2,
            YScale = -0.25,
            ZScale = 1
        };
        var t = Affine.InsertAffine(ins);
        Assert.True(t.TryInvertLinear(out var inverse));

        var deltas = new[] { (3.7, -1.9), (0.0, 0.0), (-12.5, 8.25), (1e-4, 1e3) };
        foreach (var (dx, dy) in deltas)
        {
            var p0 = t.Apply(1.5, -2.5);
            var p1 = t.Apply(1.5 + dx, -2.5 + dy);
            var back = inverse.Apply(p1.X - p0.X, p1.Y - p0.Y);
            Assert.Equal(dx, back.X, 9);
            Assert.Equal(dy, back.Y, 9);
        }
    }

    [Fact]
    public void ZeroScale_ReturnsFalse()
    {
        // Insert.XScale 的 setter 拒绝 0（ACadSharp 抛出越界），故退化用例直接以仿射构造：
        // 全零线性部与秩 1（det=0，两列共线）。
        Assert.False(new Affine(0, 0, 0, 0, 7, 9).TryInvertLinear(out var inv));
        Assert.Equal(default, inv);

        Assert.False(new Affine(2, 1, 4, 2, 0, 0).TryInvertLinear(out var rank1));
        Assert.Equal(default, rank1);
    }

    [Fact]
    public void Identity_InvertsToIdentity_WithTranslationZeroed()
    {
        Assert.True(Affine.Identity.TryInvertLinear(out var inv));
        Assert.Equal(Affine.Identity, inv);

        Assert.True(new Affine(1, 0, 0, 1, 99, -42).TryInvertLinear(out var noTx));
        Assert.Equal(Affine.Identity, noTx);
    }
}
