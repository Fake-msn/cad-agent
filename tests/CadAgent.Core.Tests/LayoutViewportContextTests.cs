using CadAgent.Core;

namespace CadAgent.Core.Tests;

public class LayoutViewportContextTests
{
    [Fact]
    public void PaperToModel_RoundTrips_ModelToPaper()
    {
        var model = (123.4, -56.7);
        var target = (100.0, 100.0);
        var center = (500.0, 350.0);
        var paper = ViewportTransform.ModelToPaper(model, target, 200, center, 700);
        var back = ViewportTransform.PaperToModel(paper, target, 200, center, 700);
        Assert.Equal(model.Item1, back.X, 9);
        Assert.Equal(model.Item2, back.Y, 9);
    }

    [Fact]
    public void Context_TwistRefusesModelMapping_AndPaperRectMatchesViewport()
    {
        var ctx = new LayoutViewportContext("5F", (500, 350), 1000, 700, 200, (0, 0), 0.3);
        Assert.False(ctx.CanMapModel);
        Assert.Equal(3.5, ctx.Scale, 9);
        Assert.Equal((0.0, 0.0, 1000.0, 700.0), ctx.PaperRect);
        Assert.True(new LayoutViewportContext("5F", (500, 350), 1000, 700, 200, (0, 0), 0).CanMapModel);
    }

    [Fact]
    public void PaperToModel_GoldenDirection_NonCenterPoint()
    {
        // s=3.5；纸面中心右移 350 → 模型右移 100
        var p = ViewportTransform.PaperToModel((850, 350), (0, 0), 200, (500, 350), 700);
        Assert.Equal(100, p.X, 9);
        Assert.Equal(0, p.Y, 9);
    }

    [Fact]
    public void Context_DegenerateViewHeight_IsObservable_ScaleOne_MapsToTarget()
    {
        var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, 0, (7, 8), 0);
        Assert.True(ctx.IsDegenerate);
        Assert.False(ctx.CanMapModel);
        Assert.Equal(1.0, ctx.Scale);
        Assert.Equal((7.0, 8.0), ctx.PaperToModel((500, 350)));
    }

    [Theory]
    [InlineData(1e-9, true)]
    [InlineData(-1e-9, true)]
    [InlineData(1.0000001e-9, false)]
    [InlineData(-1.0000001e-9, false)]
    public void Context_TwistBoundary(double twist, bool canMap)
    {
        var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), twist);
        Assert.Equal(canMap, ctx.CanMapModel);
    }

    [Theory]
    [InlineData(0.0, 700.0)]   // 纸宽退化
    [InlineData(1000.0, 0.0)]  // 纸高退化
    [InlineData(-10.0, 700.0)]
    public void Context_DegeneratePaperDims_IsObservable_ScaleOne(double paperWidth, double paperHeight)
    {
        var ctx = new LayoutViewportContext("1", (500, 350), paperWidth, paperHeight, 200, (0, 0), 0);
        Assert.True(ctx.IsDegenerate);
        Assert.False(ctx.CanMapModel);
        Assert.Equal(1.0, ctx.Scale);
    }

    [Theory]
    [InlineData(700.0, 200.0, 3.5)]
    [InlineData(700.0, 0.0, 1.0)]
    [InlineData(700.0, 1e-13, 1.0)]
    public void ScaleOf_MatchesViewportScale_AndGuardsDegenerateViewHeight(
        double paperHeight, double viewHeight, double expected)
    {
        Assert.Equal(expected, ViewportTransform.ScaleOf(paperHeight, viewHeight), 9);
    }

    // ---- 复审第 2 轮：实例映射不得与 Scale 诊断口径分叉 ----

    [Fact]
    public void Context_PaperDimDegenerate_MapsToTargetFinite_ConsistentWithScale()
    {
        // 纸高退化但视高有效（ScaleOf 数学层会得 0 → 除零）；实例必须整体走退化快路径。
        var ctx = new LayoutViewportContext("1", (500, 350), 1000, 0, 200, (7, 8), 0);
        Assert.True(ctx.IsDegenerate);
        Assert.Equal(1.0, ctx.Scale);
        var p = ctx.PaperToModel((500, 350));
        Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
        Assert.Equal((7.0, 8.0), p);
        // 与「Scale==1 且原点映射到 ViewTarget」自洽：任意纸面点亦返回 ViewTarget。
        Assert.Equal((7.0, 8.0), ctx.PaperToModel((1500, -250)));
    }

    [Fact]
    public void Context_NaNViewHeight_IsDegenerate_NaNTwistRefused()
    {
        var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, double.NaN, (7, 8), 0);
        Assert.True(ctx.IsDegenerate);
        Assert.False(ctx.CanMapModel);
        Assert.Equal(1.0, ctx.Scale);
        Assert.Equal((7.0, 8.0), ctx.PaperToModel((500, 350)));
        Assert.False(new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), double.NaN).CanMapModel);
    }

    [Theory]
    [InlineData(1e-12, true)]        // 阈值上界含：视高 == MinViewHeight 即退化
    [InlineData(1.0000001e-12, false)]
    public void Context_ViewHeightThreshold_IsDegenerate(double viewHeight, bool degenerate)
    {
        var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, viewHeight, (0, 0), 0);
        Assert.Equal(degenerate, ctx.IsDegenerate);
        Assert.Equal(degenerate, !ctx.CanMapModel);
    }

    [Theory]
    [InlineData(1e-12, true)]
    [InlineData(1.0000001e-12, false)]
    public void ScaleOf_Boundary(double viewHeight, bool isOne)
    {
        Assert.Equal(isOne ? 1 : 700 / viewHeight, ViewportTransform.ScaleOf(700, viewHeight), 6);
    }
}
