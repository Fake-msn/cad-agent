using System.Globalization;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CadAgent.Core;
using CadAgent.View.Editing;
using CadAgent.View.Rendering;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 项2 编辑空间路由（纯函数）：命中图元的空间/锁定/块归属决定编辑意图；
/// 未命中时按纸面点是否落在视口 PaperRect 内判定；纸→模型可映射性由
/// LayoutViewportContext.CanMapModel 统一裁决（无上下文/旋转→不可映射+原因）。
/// ContextFrom：ACadSharp Viewport → 纯数值上下文，纸边界视口与退化视口返回 null。
/// </summary>
public class EditRoutingTests
{
    private static LayoutViewportContext Vp(double twist = 0) =>
        new("1", (500, 350), 1000, 700, 200, (0, 0), twist);

    private static LayoutViewportContext DegenerateVp() =>
        new("1", (500, 350), 1000, 700, 0, (0, 0), 0);

    private static RenderItem Hit(RenderSpace space, bool locked = false, string? blockName = null) =>
        new(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0", 0, null, 0, 0, "2",
            Locked: locked, Space: space, BlockName: blockName);

    [Fact]
    public void Route_EmptyPointInsideViewport_IsModel()
    {
        var r = EditRouting.Route(Vp(), null, (500, 350));
        Assert.Equal(EditSpaceIntent.Model, r.Intent);
        Assert.True(r.CanMapModel);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Route_EmptyPointOutsideViewport_IsPaper()
    {
        Assert.Equal(EditSpaceIntent.Paper, EditRouting.Route(Vp(), null, (1200, 350)).Intent);
    }

    [Fact]
    public void Route_ModelHitWithoutContext_IsModelButCannotMap_WithReason()
    {
        var r = EditRouting.Route(null, Hit(RenderSpace.ModelSpace), (500, 350));
        Assert.Equal(EditSpaceIntent.Model, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Contains("无有效视口", r.Reason);
    }

    [Fact]
    public void Route_ModelHitWithValidContext_CanMapModelWithoutReason()
    {
        var r = EditRouting.Route(Vp(), Hit(RenderSpace.ModelSpace), (500, 350));
        Assert.Equal(EditSpaceIntent.Model, r.Intent);
        Assert.True(r.CanMapModel);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Route_ModelHitWithTwistedContext_IsModelButCannotMap_WithReason()
    {
        var r = EditRouting.Route(Vp(0.3), Hit(RenderSpace.ModelSpace), (500, 350));
        Assert.Equal(EditSpaceIntent.Model, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Contains("旋转", r.Reason);
    }

    [Fact]
    public void Route_LockedFrameBeatsSpace()
    {
        Assert.Equal(EditSpaceIntent.Locked,
            EditRouting.Route(Vp(), Hit(RenderSpace.PaperSpace, locked: true), (0, 0)).Intent);
    }

    [Fact]
    public void Route_BlockChildInPaperSpace_BeatsSpaceAndCannotMap()
    {
        var r = EditRouting.Route(Vp(), Hit(RenderSpace.PaperSpace, blockName: "图框"), (500, 350));
        Assert.Equal(EditSpaceIntent.BlockChild, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Equal("块子图元", r.Reason);
    }

    /// <summary>模型空间子图元按父实体整体处理（Plan-1 Imp#3）：必须保留可映射性，供调用方取模型增量。</summary>
    [Fact]
    public void Route_BlockChildInModelSpace_KeepsMappabilityWithoutReason()
    {
        var r = EditRouting.Route(Vp(), Hit(RenderSpace.ModelSpace, blockName: "灯"), (500, 350));
        Assert.Equal(EditSpaceIntent.BlockChild, r.Intent);
        Assert.True(r.CanMapModel);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Route_BlockChildInModelSpaceWithoutContext_CannotMapWithReason()
    {
        var r = EditRouting.Route(null, Hit(RenderSpace.ModelSpace, blockName: "灯"), (500, 350));
        Assert.Equal(EditSpaceIntent.BlockChild, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Contains("无有效视口", r.Reason);
    }

    [Fact]
    public void Route_BlockChildInModelSpaceWithTwist_CannotMapWithReason()
    {
        var r = EditRouting.Route(Vp(0.3), Hit(RenderSpace.ModelSpace, blockName: "灯"), (500, 350));
        Assert.Equal(EditSpaceIntent.BlockChild, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Contains("旋转", r.Reason);
    }

    [Fact]
    public void Route_PaperHit_IsPaper()
    {
        var r = EditRouting.Route(Vp(), Hit(RenderSpace.PaperSpace), (500, 350));
        Assert.Equal(EditSpaceIntent.Paper, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Null(r.Reason);
    }

    // ===== 矩阵缺口（评审 Minor 6）：空上下文 × 命中种类 / 边界 / 退化 =====

    [Fact]
    public void Route_LockedHitWithoutContext_IsLocked()
    {
        var r = EditRouting.Route(null, Hit(RenderSpace.PaperSpace, locked: true), (500, 350));
        Assert.Equal(EditSpaceIntent.Locked, r.Intent);
        Assert.False(r.CanMapModel);
    }

    [Fact]
    public void Route_NoHitWithoutContext_IsPaper()
    {
        var r = EditRouting.Route(null, null, (500, 350));
        Assert.Equal(EditSpaceIntent.Paper, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Null(r.Reason);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 700)]
    [InlineData(500, 0)]
    [InlineData(0, 350)]
    public void Route_PointOnPaperRectBoundary_IsModel(double x, double y)
    {
        // 闭区间：与仓库 Inside 口径一致，矩形相接视为命中
        var r = EditRouting.Route(Vp(), null, (x, y));
        Assert.Equal(EditSpaceIntent.Model, r.Intent);
        Assert.True(r.CanMapModel);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void Route_NoHitWithDegenerateContext_IsModelButCannotMap()
    {
        var r = EditRouting.Route(DegenerateVp(), null, (500, 350));
        Assert.Equal(EditSpaceIntent.Model, r.Intent);
        Assert.False(r.CanMapModel);
        Assert.Contains("无有效视口", r.Reason);
    }

    /// <summary>Unavailable 为保留位（Plan-1 不产出）；Reason 仅 Locked / 不可映射的 Model|BlockChild 非 null。</summary>
    [Fact]
    public void Route_Matrix_NeverProducesUnavailable_AndReasonFollowsMappability()
    {
        var contexts = new LayoutViewportContext?[] { null, Vp(), Vp(0.3), DegenerateVp() };
        var hits = new RenderItem?[]
        {
            null,
            Hit(RenderSpace.ModelSpace),
            Hit(RenderSpace.PaperSpace),
            Hit(RenderSpace.ModelSpace, locked: true),
            Hit(RenderSpace.PaperSpace, locked: true),
            Hit(RenderSpace.ModelSpace, blockName: "灯"),
            Hit(RenderSpace.PaperSpace, blockName: "图框"),
        };
        var points = new[] { (500.0, 350.0), (0.0, 0.0), (1000.0, 700.0), (1200.0, 350.0) };

        foreach (var vp in contexts)
        {
            foreach (var hit in hits)
            {
                foreach (var p in points)
                {
                    var r = EditRouting.Route(vp, hit, p);
                    Assert.NotEqual(EditSpaceIntent.Unavailable, r.Intent);
                    if (r.Intent is EditSpaceIntent.Model or EditSpaceIntent.BlockChild)
                        Assert.Equal(r.CanMapModel, r.Reason is null); // 可映射 ⇔ 无原因
                    if (r.Intent == EditSpaceIntent.Paper)
                        Assert.Null(r.Reason);
                }
            }
        }
    }

    [Fact]
    public void ContextFrom_PaperBoundaryViewport_ReturnsNull_EvenWhenNotDegenerate()
    {
        // RepresentsPaper 语义 = 纸面布局 && Id == Viewport.PaperViewId（块内首个视口）。
        // 给它合法数值（非退化），使 null 只能归因于纸边界门，而非「ViewHeight=0 退化」共因。
        var layout = new Layout("布局1"); // 构造时自动附加纸边界视口（块内首个）
        var paperVp = layout.AssociatedBlock!.Entities.OfType<Viewport>().Single();
        paperVp.Center = new XYZ(500, 350, 0);
        paperVp.Width = 1000;
        paperVp.Height = 700;
        paperVp.ViewHeight = 200;

        Assert.Equal(Viewport.PaperViewId, paperVp.Id);
        Assert.True(paperVp.RepresentsPaper);
        // 反证：同数值非纸边界视口不退化（ContextFrom 会返回上下文）
        Assert.NotNull(EditRouting.ContextFrom(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewHeight = 200,
        }));

        Assert.Null(EditRouting.ContextFrom(paperVp));
    }

    [Fact]
    public void ContextFrom_DegenerateViewport_ReturnsNull()
    {
        // ViewHeight=0 → 退化（IsDegenerate 唯一判据），不作为可编辑布局视口
        var vp = new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 0,
        };
        Assert.Null(EditRouting.ContextFrom(vp));
    }

    [Fact]
    public void ContextFrom_ValidViewport_CarriesMappingCriticalFields()
    {
        // ViewTarget 取非默认值，证明映射字段逐个透传（而非只在 (0,0) 巧合成立）
        var vp = new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(123, 45, 0),
            ViewHeight = 200,
        };
        var ctx = EditRouting.ContextFrom(vp);
        Assert.NotNull(ctx);
        Assert.Equal(vp.Handle.ToString(CultureInfo.InvariantCulture), ctx.ViewportHandle);
        Assert.Equal(500, ctx.PaperCenter.X, 6);
        Assert.Equal(350, ctx.PaperCenter.Y, 6);
        Assert.Equal(1000, ctx.PaperWidth, 6);
        Assert.Equal(700, ctx.PaperHeight, 6);
        Assert.Equal(200, ctx.ViewHeight, 6);
        Assert.Equal(123, ctx.ViewTarget.X, 6);
        Assert.Equal(45, ctx.ViewTarget.Y, 6);
        Assert.Equal(3.5, ctx.Scale, 6);
        Assert.True(ctx.CanMapModel);
    }

    [Fact]
    public void ContextFrom_TwistedViewport_CarriesTwistAndCannotMap()
    {
        // 旋转不构成退化：上下文仍产出，仅 CanMapModel=false（Route 据此附原因）
        var vp = new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200,
            TwistAngle = 0.3,
        };
        var ctx = EditRouting.ContextFrom(vp);
        Assert.NotNull(ctx);
        Assert.Equal(0.3, ctx.TwistRadians, 9);
        Assert.False(ctx.CanMapModel);
    }
}
