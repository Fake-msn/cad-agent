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
}
