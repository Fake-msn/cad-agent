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
}
