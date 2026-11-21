using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 4: LayersViewModel (pure C#, no Avalonia). Layers ordered by name
/// (case-insensitive, CAD convention), EntityCount = render items on the layer,
/// ColorBrush = "#RRGGBB" swatch string from the first item's resolved color.
/// Toggle/Isolate/ShowAll mutate CanvasViewModel.HiddenLayers and raise
/// RenderInvalidated; LayerEntry.IsVisible is a live view over HiddenLayers.
/// </summary>
public class LayersViewModelTests
{
    private static RenderItem Line(string layer, uint color = 0xFFFFFF) =>
        new(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 1.0) }, layer, color, null, 0, 0, "1");

    private static (LayersViewModel Layers, CanvasViewModel Canvas) Make()
    {
        var items = new[]
        {
            Line("WALL", 0xFF0000),
            Line("AXIS", 0x00FF00),
            Line("WALL", 0xFF0000),
            Line("TEXT", 0x0000FF),
        };
        var canvas = new CanvasViewModel(items);
        return (new LayersViewModel(items, canvas), canvas);
    }

    [Fact]
    public void Ctor_OrdersLayersByName_WithCounts()
    {
        (LayersViewModel layers, _) = Make();
        Assert.Equal(new[] { "AXIS", "TEXT", "WALL" }, layers.Layers.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 1, 1, 2 }, layers.Layers.Select(l => l.EntityCount).ToArray());
    }

    [Fact]
    public void Ctor_ColorBrush_HexFromFirstItemColor()
    {
        (LayersViewModel layers, _) = Make();
        Assert.Equal("#00FF00", layers.Layers[0].ColorBrush); // AXIS
        Assert.Equal("#0000FF", layers.Layers[1].ColorBrush); // TEXT
        Assert.Equal("#FF0000", layers.Layers[2].ColorBrush); // WALL
    }

    [Fact]
    public void Ctor_GroupsCaseInsensitively()
    {
        var items = new[] { Line("WALL"), Line("wall"), Line("Wall") };
        var canvas = new CanvasViewModel(items);
        var layers = new LayersViewModel(items, canvas);
        LayerEntry entry = Assert.Single(layers.Layers);
        Assert.Equal(3, entry.EntityCount);
    }

    [Fact]
    public void Ctor_EmptyItems_EmptyLayers()
    {
        var canvas = new CanvasViewModel(Array.Empty<RenderItem>());
        var layers = new LayersViewModel(Array.Empty<RenderItem>(), canvas);
        Assert.Empty(layers.Layers);
    }

    [Fact]
    public void IsVisible_DefaultsTrue_LiveViewOverHiddenLayers()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        Assert.All(layers.Layers, l => Assert.True(l.IsVisible));
        canvas.HiddenLayers.Add("WALL"); // external mutation reflected without rebuild
        Assert.False(layers.Layers[2].IsVisible);
        Assert.True(layers.Layers[0].IsVisible);
    }

    [Fact]
    public void Toggle_HidesLayer_RaisesRenderInvalidated()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        int raised = 0;
        canvas.RenderInvalidated += () => raised++;
        layers.Toggle("WALL");
        Assert.Contains("WALL", canvas.HiddenLayers);
        Assert.False(layers.Layers[2].IsVisible);
        Assert.Equal(2, canvas.VisibleItems.Count()); // AXIS + TEXT remain
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Toggle_Twice_RestoresVisibility()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        layers.Toggle("WALL");
        layers.Toggle("WALL");
        Assert.Empty(canvas.HiddenLayers);
        Assert.True(layers.Layers[2].IsVisible);
        Assert.Equal(4, canvas.VisibleItems.Count());
    }

    [Fact]
    public void Toggle_CaseInsensitive()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        layers.Toggle("wall");
        Assert.False(layers.Layers[2].IsVisible);
        layers.Toggle("Wall");
        Assert.True(layers.Layers[2].IsVisible);
    }

    [Fact]
    public void Toggle_UnknownName_NoOp()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        int raised = 0;
        canvas.RenderInvalidated += () => raised++;
        layers.Toggle("NO-SUCH");
        Assert.Empty(canvas.HiddenLayers);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Isolate_HidesAllOtherLayers()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        int raised = 0;
        canvas.RenderInvalidated += () => raised++;
        layers.Isolate("WALL");
        Assert.True(layers.Layers[2].IsVisible);
        Assert.False(layers.Layers[0].IsVisible); // AXIS
        Assert.False(layers.Layers[1].IsVisible); // TEXT
        Assert.All(canvas.VisibleItems, i => Assert.Equal("WALL", i.Layer));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Isolate_CaseInsensitive_AndReplacesPreviousHiddenSet()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        layers.Toggle("WALL");
        layers.Isolate("wall");
        Assert.True(layers.Layers[2].IsVisible); // previously hidden WALL now the only visible
        Assert.Equal(2, canvas.HiddenLayers.Count);
    }

    [Fact]
    public void Isolate_UnknownName_NoOp()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        layers.Isolate("NO-SUCH");
        Assert.Empty(canvas.HiddenLayers);
    }

    [Fact]
    public void ShowAll_ClearsHiddenLayers_RaisesEvent()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        layers.Isolate("AXIS");
        int raised = 0;
        canvas.RenderInvalidated += () => raised++;
        layers.ShowAll();
        Assert.Empty(canvas.HiddenLayers);
        Assert.All(layers.Layers, l => Assert.True(l.IsVisible));
        Assert.Equal(1, raised);
    }

    // ---- T9 carryover: Refresh rebuilds the layer list after an edit ----

    [Fact]
    public void Refresh_RebuildsNamesAndCounts_KeepsVisibility_RaisesEvent()
    {
        (LayersViewModel layers, CanvasViewModel canvas) = Make();
        layers.Toggle("WALL"); // hide WALL before the edit
        var newItems = new[]
        {
            Line("WALL", 0xFF0000),
            Line("0", 0xFFFFFF), // layer created by the edit
            Line("0", 0xFFFFFF),
        };
        int raised = 0;
        canvas.RenderInvalidated += () => raised++;

        layers.Refresh(newItems);

        Assert.Equal(new[] { "0", "WALL" }, layers.Layers.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { 2, 1 }, layers.Layers.Select(l => l.EntityCount).ToArray());
        Assert.False(layers.Layers.Single(l => l.Name == "WALL").IsVisible); // hidden state survives
        Assert.True(layers.Layers.Single(l => l.Name == "0").IsVisible);
        Assert.Equal(1, raised);
        // Refreshed names are live for Toggle/Isolate (old snapshot names are gone).
        layers.Toggle("0");
        Assert.Contains("0", canvas.HiddenLayers);
        layers.Isolate("TEXT"); // no longer a known layer -> no-op (hidden set unchanged)
        Assert.Equal(2, canvas.HiddenLayers.Count);
        Assert.Contains("WALL", canvas.HiddenLayers);
    }
}
