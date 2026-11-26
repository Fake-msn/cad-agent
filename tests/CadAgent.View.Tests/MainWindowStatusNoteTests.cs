using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 5：状态栏注记接线（DrawingCanvas.LastRenderNote → MainWindow.Status）。
/// 「概览」/「高亮为包围盒（图元过密）」在画布 invalidate 后追加到状态栏尾部。
/// headless 直接置注记（seam），不依赖真实交互计时。
/// </summary>
public class MainWindowStatusNoteTests
{
    [AvaloniaFact]
    public void Status_AppendsCanvasRenderNote()
    {
        string path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        var vm = new MainViewModel(path, Path.GetTempPath());
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            Assert.NotNull(window.Status);

            canvas.SetLastRenderNoteForTests("概览");
            window.Canvas!.Invalidate();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("| 概览", window.Status!.Text);
            Assert.DoesNotContain("包围盒", window.Status.Text);

            canvas.SetLastRenderNoteForTests("高亮为包围盒（图元过密）");
            window.Canvas.Invalidate();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("| 高亮为包围盒（图元过密）", window.Status.Text);

            canvas.SetLastRenderNoteForTests(null);
            window.Canvas.Invalidate();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("概览", window.Status.Text);
            Assert.DoesNotContain("包围盒", window.Status.Text);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }
}
