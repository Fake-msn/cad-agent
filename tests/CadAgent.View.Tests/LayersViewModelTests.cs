using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;

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

    // ---- 视图闭环 T8：空图层显示（图层表并集）----
    // 夹具沿用 RenderModelTests 的 WithSession/DxfFixture 模式：经真实 DXF 写/读往返，
    // 空图层（图层表有记录、无实体）必须在往返后仍存在于 doc.Layers。

    private static void WithSession(Action<CadDocument> populate, Action<DrawingSession> test)
    {
        string path = DxfFixture.CreateTempDxf(populate);
        try { test(DrawingSession.Load(path)); }
        finally { File.Delete(path); }
    }

    /// <summary>EMPTY（无实体，ACI 3 绿）+ USED（1 线，ACI 1 红）。</summary>
    private static void EmptyAndUsed(CadDocument doc)
    {
        doc.Layers.Add(new Layer("EMPTY") { Color = new Color(3) });
        var used = new Layer("USED") { Color = new Color(1) };
        doc.Layers.Add(used);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 1, 0), Layer = used });
    }

    [Fact]
    public void Layers_EmptyLayerInDoc_ShownWithZeroCount()
    {
        WithSession(EmptyAndUsed, s =>
        {
            IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(s);
            var canvas = new CanvasViewModel(items);
            var layers = new LayersViewModel(items, canvas, s);

            // 空图层 EMPTY 出现（计数 0），USED 计数 1。
            LayerEntry empty = layers.Layers.Single(l => l.Name == "EMPTY");
            LayerEntry used = layers.Layers.Single(l => l.Name == "USED");
            Assert.Equal(0, empty.EntityCount);
            Assert.Equal(1, used.EntityCount);
            // 空层色样 = 图层表颜色（LayerColorRgb，ACI 3 绿），非白 fallback。
            Layer emptyTable = s.Doc.Layers.First(l => l.Name == "EMPTY");
            Assert.Equal("#" + RenderModelBuilder.LayerColorRgb(emptyTable).ToString("X6"), empty.ColorBrush);
            Assert.NotEqual("#FFFFFF", empty.ColorBrush);
            // 并集不破坏名序（含默认层 "0"，同为空层计数 0）。
            Assert.Equal(
                s.Doc.Layers.Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                layers.Layers.Select(l => l.Name).ToArray());
            Assert.Equal(0, layers.Layers.Single(l => l.Name == "0").EntityCount);
        });
    }

    [Fact]
    public void Layers_NoSession_LegacyBehaviorUnchanged()
    {
        WithSession(EmptyAndUsed, s =>
        {
            IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(s);
            var canvas = new CanvasViewModel(items);
            var layers = new LayersViewModel(items, canvas); // 不传 session：既有口径
            // 仅 renderItems 图层出现；空图层（EMPTY/0）不进面板（legacy 行为保持）。
            Assert.Equal(new[] { "USED" }, layers.Layers.Select(l => l.Name).ToArray());
            Assert.Equal(1, layers.Layers[0].EntityCount);
        });
    }

    [Fact]
    public void Refresh_WithSession_NewEmptyLayerAppears()
    {
        WithSession(EmptyAndUsed, s =>
        {
            IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(s);
            var canvas = new CanvasViewModel(items);
            var layers = new LayersViewModel(items, canvas, s);
            layers.Toggle("USED"); // 编辑前隐藏 USED

            // 真实编辑路径：ChangeLayer 把唯一一线移到新层 E-NEW（handler 建图层表记录）。
            string handle = s.Doc.Entities.OfType<Line>().First().Handle.ToString();
            StepResult r = new ChangeLayerHandler().Apply(s,
                JsonDocument.Parse($"{{\"handle\":{handle},\"layer\":\"E-NEW\"}}").RootElement);
            Assert.True(r.Ok, r.Error);

            layers.Refresh(RenderModelBuilder.Build(s), s);

            // 新层出现（计数 1）；USED 变空层仍留在面板（计数 0），隐藏状态原样保留。
            Assert.Equal(1, layers.Layers.Single(l => l.Name == "E-NEW").EntityCount);
            LayerEntry used = layers.Layers.Single(l => l.Name == "USED");
            Assert.Equal(0, used.EntityCount);
            Assert.False(used.IsVisible);
            Assert.Contains("USED", canvas.HiddenLayers);
            // EMPTY 仍显示（Refresh 不丢空层）。
            Assert.Equal(0, layers.Layers.Single(l => l.Name == "EMPTY").EntityCount);
        });
    }
}
