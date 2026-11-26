using ACadSharp;
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
    public void Route_BlockChildBeatsSpace()
    {
        var r = EditRouting.Route(Vp(), Hit(RenderSpace.PaperSpace, blockName: "图框"), (500, 350));
        Assert.Equal(EditSpaceIntent.BlockChild, r.Intent);
        Assert.Equal("块子图元", r.Reason);
    }

    [Fact]
    public void Route_PaperHit_IsPaper()
    {
        Assert.Equal(EditSpaceIntent.Paper,
            EditRouting.Route(Vp(), Hit(RenderSpace.PaperSpace), (500, 350)).Intent);
    }

    [Fact]
    public void ContextFrom_PaperBoundaryViewport_ReturnsNull()
    {
        // AddViewport 自动附加纸边界视口（RepresentsPaper=true，仅边界不 visualize 模型）
        var doc = new CadDocument();
        var layout = new Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200,
        });
        var paperVp = layout.AssociatedBlock!.Entities.OfType<Viewport>().First(v => v.RepresentsPaper);
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
    public void ContextFrom_ValidViewport_ReturnsContextWithScale()
    {
        var vp = new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200,
        };
        var ctx = EditRouting.ContextFrom(vp);
        Assert.NotNull(ctx);
        Assert.Equal(500, ctx.PaperCenter.X, 6);
        Assert.Equal(350, ctx.PaperCenter.Y, 6);
        Assert.Equal(700, ctx.PaperHeight, 6);
        Assert.Equal(3.5, ctx.Scale, 6);
        Assert.True(ctx.CanMapModel);
    }
}
