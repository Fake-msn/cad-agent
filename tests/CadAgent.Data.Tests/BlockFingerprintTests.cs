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

    // ---- PR#21 合并前 ISTQB 修复波 2（I2 + 择要 Minor，纯测试新增）----

    /// <summary>
    /// I2（核心不变量无条件守卫）：哈希实体顺序无关——Symbol() 反转 + 多实体混合
    /// 固定置换（2 线 2 圆 1 弧）→ 哈希逐位相等。不依赖真实标本，无条件运行。
    /// </summary>
    [Fact]
    public void Extract_EntityOrderPermuted_SameHash()
    {
        var baseline = BlockFingerprint.Extract(Symbol())!;
        var reversed = Symbol();
        reversed.Reverse();
        Assert.Equal(baseline.Hash, BlockFingerprint.Extract(reversed)!.Hash);
        // 多实体混合打乱：类内规范排序同样顺序无关（固定置换，非随机，确定性）
        static List<Entity> Multi() => new()
        {
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) },
            new Circle { Center = new XYZ(5, 3, 0), Radius = 2 },
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(0, 10, 0) },
            new Arc { Center = new XYZ(2, 2, 0), Radius = 1, StartAngle = 0, EndAngle = Math.PI / 2 },
            new Circle { Center = new XYZ(8, 8, 0), Radius = 1 },
        };
        var m = BlockFingerprint.Extract(Multi())!;
        var shuffled = Multi();
        shuffled.Reverse();
        (shuffled[0], shuffled[4]) = (shuffled[4], shuffled[0]);
        Assert.Equal(m.Hash, BlockFingerprint.Extract(shuffled)!.Hash);
    }

    /// <summary>M1（点状退化）：零长线 / 重合微半径圆 → bbox scale&lt;1e-9 → Extract null 钉住。</summary>
    [Fact]
    public void Extract_PointDegenerate_Null()
    {
        // 零长线：bbox 0×0 → scale=0&lt;1e-9 → null
        Assert.Null(BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(3, 7, 0), EndPoint = new XYZ(3, 7, 0) },
        }));
        // 重合微半径圆（ACadSharp 要求 Radius>0，零半径不可构造）：外接矩形 2e-12 → 同样退化 null
        Assert.Null(BlockFingerprint.Extract(new List<Entity>
        {
            new Circle { Center = new XYZ(1, 1, 0), Radius = 1e-12 },
            new Circle { Center = new XYZ(1, 1, 0), Radius = 1e-12 },
        }));
    }

    /// <summary>
    /// M5（边界择要）：Compatible |ΔAspect|=0.2 恰接受（&lt;= 口径）；恰超拒绝。
    /// 夹具纵横比 0.2（线 10×2，2.0/10.0 与字面量 0.2 同双精度）vs 0（横线）→ 差恰 0.2。
    /// </summary>
    [Fact]
    public void Compatible_AspectDeltaExactly02_Accepted_JustOverRejected()
    {
        var a = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 2, 0) },
        })!;
        var b = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) },
        })!;
        Assert.Equal(0.2, a.Geom.Aspect, 9);
        Assert.Equal(0, b.Geom.Aspect, 9);
        Assert.True(BlockFingerprint.Compatible(a, b)); // |0.2-0|=0.2 恰接受
        var over = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 2.001, 0) },
        })!;
        Assert.False(BlockFingerprint.Compatible(b, over)); // 0.2001>0.2 恰超拒绝
    }

    /// <summary>
    /// M5（边界择要）：Distance 恰=NearThreshold(0.15) 值钉住（&lt;= 接受口径的算法侧）。
    /// 双圆夹具：外圆 C1(5,5)r5 固定 bbox 10×10；内圆 C2 平移 3（6→3）→ 归一化圆心差
    /// 0.3（0.6-0.3 双精度精确）→ 两圆平均 d=0.3/2=0.15 精确。恰超夹具（C2→2）d≈0.2&gt;阈值。
    /// </summary>
    [Fact]
    public void Distance_ExactlyAtNearThreshold_ValuePinned()
    {
        static List<Entity> BlockCircles(double innerX) => new()
        {
            new Circle { Center = new XYZ(5, 5, 0), Radius = 5 },
            new Circle { Center = new XYZ(innerX, 5, 0), Radius = 1 },
        };
        var a = BlockFingerprint.Extract(BlockCircles(6))!;
        var b = BlockFingerprint.Extract(BlockCircles(3))!;
        Assert.NotEqual(a.Hash, b.Hash); // 非精确 → 走 Distance
        Assert.True(BlockFingerprint.Compatible(a, b));
        double d = BlockFingerprint.Distance(a, b);
        Assert.Equal(BlockFingerprint.NearThreshold, d); // 精确双精度 0.15（<= 恰接受）
        var over = BlockFingerprint.Extract(BlockCircles(2))!;
        double dOver = BlockFingerprint.Distance(a, over);
        Assert.InRange(dOver, 0.19, 0.21);
        Assert.True(dOver > BlockFingerprint.NearThreshold); // 恰超阈值
    }

    /// <summary>
    /// M6（贪心多实体占用）：两线两圆交叉最近邻——used[] 占用合成钉住。
    /// 夹具：a 线 y=0/1、圆 x=3/4；b 线 y=0/3、圆 x=3/8（bbox 同宽 10，Aspect 0.1 vs 0.3 相容）。
    /// 有占用：L2→L2'(0.2)、C2→C2'(0.4) → d=(0.2+0.4)/4=0.15；
    /// 无占用（回归判别）：L2/C2 均复用最近 L1'/C1'(各 0.1) → d=0.05。断言 0.15 钉住占用语义。
    /// </summary>
    [Fact]
    public void Distance_GreedyMultiEntity_UsedOccupancyPinned()
    {
        var a = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) },
            new Line { StartPoint = new XYZ(0, 1, 0), EndPoint = new XYZ(10, 1, 0) },
            new Circle { Center = new XYZ(3, 0.2, 0), Radius = 0.2 },
            new Circle { Center = new XYZ(4, 0.2, 0), Radius = 0.2 },
        })!;
        var b = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) },
            new Line { StartPoint = new XYZ(0, 3, 0), EndPoint = new XYZ(10, 3, 0) },
            new Circle { Center = new XYZ(3, 0.2, 0), Radius = 0.2 },
            new Circle { Center = new XYZ(8, 0.2, 0), Radius = 0.2 },
        })!;
        Assert.True(BlockFingerprint.Compatible(a, b));
        double d = BlockFingerprint.Distance(a, b);
        Assert.InRange(d, 0.14, 0.16); // 有占用=0.15；无占用会落到 0.05 → 回红
        Assert.True(d > 0.1);
    }

    // ---- R9②③ 诊断披露补全（spec §13.4 NaN/∞ 剔除披露计数）----

    /// <summary>③ DroppedNonFinite：1 NaN 线 → 计 1（整条剔除口径，Finite 守卫命中即计 1）；
    /// 加性字段不破既有哈希（与无 NaN 基准逐位相等）。</summary>
    [Fact]
    public void Extract_NaNLine_DroppedNonFiniteOne()
    {
        var ents = Symbol();
        ents.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(1, 1, 0) });
        var fp = BlockFingerprint.Extract(ents)!;
        Assert.Equal(1, fp.DroppedNonFinite);
        Assert.Equal(BlockFingerprint.Extract(Symbol())!.Hash, fp.Hash); // 哈希口径不变
    }

    /// <summary>③ 全有限实体 → DroppedNonFinite=0（加性字段默认口径）。</summary>
    [Fact]
    public void Extract_AllFinite_DroppedNonFiniteZero()
    {
        Assert.Equal(0, BlockFingerprint.Extract(Symbol())!.DroppedNonFinite);
    }

    /// <summary>③ ∞ 半径圆 + NaN 圆心弧 → 各计 1 合计 2（NaN 与 ∞ 同守卫口径）。</summary>
    [Fact]
    public void Extract_InfinityCircleAndNaNArc_DroppedNonFiniteTwo()
    {
        var ents = Symbol();
        ents.Add(new Circle { Center = new XYZ(1, 1, 0), Radius = double.PositiveInfinity });
        ents.Add(new Arc { Center = new XYZ(double.NaN, 0, 0), Radius = 5, StartAngle = 0, EndAngle = 1 });
        var fp = BlockFingerprint.Extract(ents)!;
        Assert.Equal(2, fp.DroppedNonFinite);
    }

    /// <summary>③ out 重载：NaN-only 块 Extract→null 但剔除数不丢（代理块披露通道）；
    /// 非 null 路径 out 值与 record 字段一致。</summary>
    [Fact]
    public void Extract_OutOverload_NaNOnlyNullFp_DroppedStillCounted()
    {
        var fp = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(1, 1, 0) },
        }, out int dropped);
        Assert.Null(fp); // 零白名单几何（NaN 线整条剔除后为空）
        Assert.Equal(1, dropped);
        var ents = Symbol();
        ents.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(1, 1, 0) });
        var fp2 = BlockFingerprint.Extract(ents, out int dropped2)!;
        Assert.Equal(1, dropped2);
        Assert.Equal(fp2.DroppedNonFinite, dropped2);
    }

    /// <summary>③ out 重载第二 null 分支（退化 bbox）dropped 判别：零长 Line（白名单存活但
    /// bbox 0×0 → scale&lt;1e-9）+ NaN Line（剔除计 1）→ fp null 且 dropped=1——
    /// 钉住退化 null 路径剔除数同样不丢（与零白名单 null 分支测试形成判别对）。</summary>
    [Fact]
    public void Extract_OutOverload_DegenerateBboxNullFp_DroppedStillCounted()
    {
        var fp = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new XYZ(3, 7, 0), EndPoint = new XYZ(3, 7, 0) }, // 零长线：白名单存活，bbox 退化
            new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(1, 1, 0) }, // NaN 线：整条剔除计 1
        }, out int dropped);
        Assert.Null(fp); // 退化 bbox null（非零白名单 null——白名单计数=1）
        Assert.Equal(1, dropped);
    }
}
