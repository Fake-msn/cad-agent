using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Data.Tests;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 5：状态栏注记接线（DrawingCanvas.LastRenderNote → MainWindow.Status）——生产时序：
/// 注记在 Render 末尾产出，经 RenderNoteChanged 驱动状态栏刷新（RenderInvalidated 早于渲染，不能读注记）；
/// 精绘清空注记同样触发事件 → 状态栏注记消失。
/// </summary>
public class MainWindowStatusNoteTests
{
    [AvaloniaFact]
    public void Status_TracksCanvasRenderNote_AfterRender_ThenClearedByRefined()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), "statusnote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        string path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        var window = (MainWindow?)null;
        try
        {
            var vm = new MainViewModel(path, projectDir);
            Assert.Null(vm.ErrorMessage);
            window = new MainWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DrawingCanvas canvas = window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
            Assert.NotNull(window.Status);
            canvas.IdleRefineMs = 5_000;

            // 首帧（Refined）：注记为空，状态栏无注记。
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(canvas.LastRenderNote);
            Assert.DoesNotContain("概览", window.Status!.Text);
            Assert.DoesNotContain("包围盒", window.Status.Text);

            // 交互档 → Render 末尾产出「概览」→ 事件刷新状态栏（不依赖 RenderInvalidated 早读）。
            canvas.SetQualityForTests(RenderQuality.Interactive);
            window.Canvas!.Invalidate();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("概览", canvas.LastRenderNote);
            Assert.Contains("| 概览", window.Status.Text);
            Assert.DoesNotContain("包围盒", window.Status.Text);

            // 精绘（Refined 重建）→ 注记清空事件 → 状态栏注记消失。
            canvas.ForceRefinedForTests();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(canvas.LastRenderNote);
            Assert.DoesNotContain("概览", window.Status.Text);
            Assert.DoesNotContain("包围盒", window.Status.Text);
        }
        finally
        {
            window?.Close();
            File.Delete(path);
            Directory.Delete(projectDir, true);
        }
    }
}
