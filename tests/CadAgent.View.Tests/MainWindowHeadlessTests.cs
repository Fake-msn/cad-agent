using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 4 headless coverage ([AvaloniaFact], real Skia frames):
/// MainWindow layer panel builds ordered entries (checkbox + swatch + count),
/// checkbox click toggles layer visibility out of CanvasViewModel.VisibleItems,
/// double-click isolates, Show all restores, and canvas left-click selection
/// updates the status bar with the real DXF handle / kind / layer.
/// </summary>
public class MainWindowHeadlessTests
{
    /// <summary>ALPHA: 2 red lines (y=0, y=2); BETA: 1 green line (y=20).</summary>
    private static string TwoLayerDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var alpha = new Layer("ALPHA") { Color = new ACadSharp.Color(1) };
        var beta = new Layer("BETA") { Color = new ACadSharp.Color(3) };
        doc.Layers.Add(alpha);
        doc.Layers.Add(beta);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0), Layer = alpha });
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 2, 0), EndPoint = new XYZ(10, 2, 0), Layer = alpha });
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(10, 20, 0), Layer = beta });
    });

    private static (MainWindow Window, MainViewModel Vm, string Path) ShowWindow()
    {
        string path = TwoLayerDxf();
        var vm = new MainViewModel(path, Path.GetTempPath());
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, path);
    }

    private static void Cleanup(MainWindow window, string path)
    {
        window.Close();
        File.Delete(path);
    }

    [AvaloniaFact]
    public void LayerPanel_BuildsOrderedEntries_WithCheckboxesAndCounts()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            Assert.NotNull(window.Layers);
            Assert.NotNull(window.LayerList);
            Assert.Equal(new[] { "ALPHA", "BETA" }, window.Layers!.Layers.Select(l => l.Name).ToArray());
            Assert.Equal(new[] { 2, 1 }, window.Layers.Layers.Select(l => l.EntityCount).ToArray());
            Assert.Equal("#FF0000", window.Layers.Layers[0].ColorBrush);
            Assert.Equal("#00FF00", window.Layers.Layers[1].ColorBrush);
            Assert.Equal(2, window.LayerList!.ItemCount);
            List<CheckBox> checks = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(2, checks.Count);
            Assert.All(checks, c => Assert.True(c.IsChecked));
            Assert.Contains("render items", window.Status!.Text);
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_CheckboxClick_TogglesLayerOutOfVisibleItems()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            CheckBox first = window.GetVisualDescendants().OfType<CheckBox>().First(); // ALPHA
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("ALPHA", window.Canvas!.HiddenLayers);
            Assert.False(window.Layers!.Layers[0].IsVisible);
            Assert.False(first.IsChecked); // refreshed from entry state
            Assert.All(window.Canvas.VisibleItems, i => Assert.Equal("BETA", i.Layer));
            Assert.Single(window.Canvas.VisibleItems);

            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // toggle back
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Canvas.HiddenLayers);
            Assert.True(first.IsChecked);
            Assert.Equal(3, window.Canvas.VisibleItems.Count());
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_DoubleClick_IsolatesSelectedLayer()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            // Real double-click on the BETA row (right side, away from the checkbox):
            // two rapid click pairs at the same point -> MouseDevice click count 2
            // -> DoubleTapped bubbles from ListBoxItem to the ListBox.
            var row = (Control)window.LayerList!.ContainerFromIndex(1)!;
            Point p = row.TranslatePoint(new Point(row.Bounds.Width - 20, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Layers!.Layers[0].IsVisible); // ALPHA hidden
            Assert.True(window.Layers.Layers[1].IsVisible);
            Assert.All(window.Canvas!.VisibleItems, i => Assert.Equal("BETA", i.Layer));
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void LayerPanel_ShowAllButton_RestoresAllLayers()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            window.Layers!.Isolate("BETA");
            Dispatcher.UIThread.RunJobs();
            Button showAll = window.GetVisualDescendants().OfType<Button>().First(b => b is not CheckBox);
            showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.Canvas!.HiddenLayers);
            Assert.All(window.Layers.Layers, l => Assert.True(l.IsVisible));
            Assert.Equal(3, window.Canvas.VisibleItems.Count());
        }
        finally { Cleanup(window, path); }
    }

    [AvaloniaFact]
    public void CanvasSelection_UpdatesStatusBar_WithHandleKindLayer()
    {
        (MainWindow window, MainViewModel vm, string path) = ShowWindow();
        try
        {
            DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            string expectedHandle = vm.Session.Doc.Entities.First().Handle.ToString();

            (double sx, double sy) = window.Canvas!.WorldToScreen(5, 0); // on first ALPHA line
            Point onLine = canvas.TranslatePoint(new Point(sx, sy), window)!.Value;
            window.MouseDown(onLine, MouseButton.Left);
            window.MouseUp(onLine, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(expectedHandle, window.Canvas.SelectedHandle);
            Assert.Contains(expectedHandle, window.Status!.Text);
            Assert.Contains("Line", window.Status.Text);
            Assert.Contains("ALPHA", window.Status.Text);

            Point empty = canvas.TranslatePoint(new Point(5, 5), window)!.Value; // background corner
            window.MouseDown(empty, MouseButton.Left);
            window.MouseUp(empty, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.Canvas.SelectedHandle);
            Assert.DoesNotContain("Selected", window.Status.Text);
        }
        finally { Cleanup(window, path); }
    }
}
