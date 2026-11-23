using ACadSharp.Entities;
using CadAgent.Data.Semantic;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// BF-T1：块几何指纹算法测试（spec §13.2）。
/// R1 API 定型：实体点属性为 CSMath.XYZ（与 BlockWalkerTests 同口径 new XYZ(x,y,0)）；
/// Arc.StartAngle/EndAngle 为弧度（DoorArcDetector 已对 ACadSharp 3.8.35 核实）。
/// </summary>
public class BlockFingerprintTests
{
    // 基准符号：横线 (0,0)-(10,0) + 圆 (5,3) r=2；bbox 10×5，Aspect=0.5。
    private static List<Entity> Symbol() => new()
    {
        new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) },
        new Circle { Center = new XYZ(5, 3, 0), Radius = 2 },
    };

    [Fact]
    public void Extract_CloneAndScaledAndTranslated_SameHash()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var scaled = Symbol();
        ((Line)scaled[0]).StartPoint = new XYZ(25, 25, 0);
        ((Line)scaled[0]).EndPoint = new XYZ(50, 25, 0);
        ((Circle)scaled[1]).Center = new XYZ(37.5, 32.5, 0);
        ((Circle)scaled[1]).Radius = 5;
        var b = BlockFingerprint.Extract(scaled)!;
        Assert.Equal(a.Hash, b.Hash); // 平移+均匀缩放不变
    }

    [Fact]
    public void Extract_TextDifference_SameHash()
    {
        var withText = Symbol();
        withText.Add(new TextEntity { InsertPoint = new XYZ(1, 1, 0), Height = 2, Value = "注记" });
        Assert.Equal(BlockFingerprint.Extract(Symbol())!.Hash, BlockFingerprint.Extract(withText)!.Hash);
    }

    [Fact]
    public void Extract_NoWhitelistGeometry_Null()
    {
        Assert.Null(BlockFingerprint.Extract(new List<Entity> { new TextEntity { Height = 1 } }));
        Assert.Null(BlockFingerprint.Extract(new List<Entity>()));
    }

    [Fact]
    public void Extract_DegenerateSingleAxis_Normalizes()
    {
        var fp = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(3, 7, 0), EndPoint = new XYZ(3, 17, 0) },
        })!;
        Assert.NotNull(fp); // 最长边归一，退化 bbox 安全
        Assert.Equal(0, fp.Geom.Aspect, 3);
    }

    [Fact]
    public void Extract_NonFiniteCoordinate_EntityDropped()
    {
        var ents = Symbol();
        ents.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(1, 1, 0) });
        var fp = BlockFingerprint.Extract(ents)!;
        Assert.Equal(BlockFingerprint.Extract(Symbol())!.Hash, fp.Hash);
    }

    [Fact]
    public void Distance_PerturbedEndpoint_SmallPositive()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var perturbed = Symbol();
        ((Line)perturbed[0]).EndPoint = new XYZ(10.5, 0, 0); // 归一化后偏移 0.05
        var b = BlockFingerprint.Extract(perturbed)!;
        Assert.NotEqual(a.Hash, b.Hash);
        double d = BlockFingerprint.Distance(a, b);
        Assert.InRange(d, 0.001, BlockFingerprint.NearThreshold);
    }

    [Fact]
    public void Distance_HistogramMismatch_One()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var extra = Symbol();
        extra.Add(new Circle { Center = new XYZ(1, 1, 0), Radius = 1 });
        var b = BlockFingerprint.Extract(extra)!;
        Assert.False(BlockFingerprint.Compatible(a, b));
        Assert.Equal(1.0, BlockFingerprint.Distance(a, b));
    }

    [Fact]
    public void Distance_Identical_Zero_AndConfidenceFormula()
    {
        // R3 机械守卫（fix wave）：阈值字面量钉住——静默改阈必须显式改此断言，禁止无声漂移。
        Assert.Equal(0.15, BlockFingerprint.NearThreshold);
        var a = BlockFingerprint.Extract(Symbol())!;
        Assert.Equal(0, BlockFingerprint.Distance(a, a), 9);
        Assert.Equal(0.9, BlockFingerprint.Confidence(0), 9);
        Assert.Equal(0.6, BlockFingerprint.Confidence(BlockFingerprint.NearThreshold), 9);
        Assert.True(BlockFingerprint.Confidence(BlockFingerprint.NearThreshold) < 0.8); // 闸下（d=0.15≥0.05 限定域）
    }

    [Fact]
    public void Distance_LineDirectionReversal_Invariant()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var rev = Symbol();
        var l = (Line)rev[0];
        (l.StartPoint, l.EndPoint) = (l.EndPoint, l.StartPoint);
        Assert.Equal(0, BlockFingerprint.Distance(a, BlockFingerprint.Extract(rev)!), 9);
    }

    // BF-T2 顺带闭合 T1 遗留 minor：弧夹具覆盖 MatchArcs/AngDiff 绕回。
    // 弧角跨 0/2π：起角 2π-0.1（≈355°）vs 0.05（≈3°）——环形短弧差≈0.15 rad，
    // 而非朴素差≈6.13 rad；若无绕回处理距离会被钳到 1.0。
    [Fact]
    public void Distance_ArcAnglesAcrossZeroWrap_ShortCircularDiff()
    {
        var a = BlockFingerprint.Extract(new List<Entity>
        {
            new Arc { Center = new XYZ(0, 0, 0), Radius = 5, StartAngle = 2 * Math.PI - 0.1, EndAngle = 2 * Math.PI - 0.05 },
        })!;
        var b = BlockFingerprint.Extract(new List<Entity>
        {
            new Arc { Center = new XYZ(0, 0, 0), Radius = 5, StartAngle = 0.05, EndAngle = 0.1 },
        })!;
        Assert.NotEqual(a.Hash, b.Hash); // 角度原样入哈希——跨 0 表示不同串
        Assert.True(BlockFingerprint.Compatible(a, b));
        double d = BlockFingerprint.Distance(a, b);
        // 量化后角差各≈0.1502 → d = 0.5×(0.1502+0.1502)/2 ≈ 0.0751（绕回短弧）
        Assert.InRange(d, 0.07, 0.08);
        Assert.True(d < BlockFingerprint.NearThreshold);
    }
}
