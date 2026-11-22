using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 幅面感知 T5 headless（[AvaloniaFact] 真实 Skia 帧）：MainWindow 幅面 ComboBox
/// （internal SheetSelector）——默认首幅面裁剪（R4 sanctioned 行为变更），切换幅面 →
/// Canvas.Items 刷新为该幅面图元（模型幅面=框内锚点裁剪；布局幅面=BuildViewport 纸面映射），
/// 状态栏显示幅面名。单幅面夹具无感（既有 headless 回归闸口）；多幅面夹具断言按幅面。
/// </summary>
public class SheetSelectorHeadlessTests
{
    private static string UniqueProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-sheet-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>双图框幅面：框 0 (0,0)-(1000,700)、框 1 (2000,0)-(3000,700)，各含幅面名文字 + E-WIRE 线。</summary>
    private static string TwoSheetDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        for (int i = 0; i < 2; i++)
        {
            var ins = new Insert(frame) { InsertPoint = new XYZ(i * 2000, 0, 0) };
            ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = $"Z-E0{i + 1}" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(i * 2000 + 500, 650, 0), Value = i == 0 ? "一层电力平面图" : "二层电力平面图", Height = 30, Layer = new Layer("图框") });
            doc.Entities.Add(new Line { StartPoint = new XYZ(i * 2000 + 100, 100, 0), EndPoint = new XYZ(i * 2000 + 200, 100, 0), Layer = new Layer("E-WIRE") });
        }
    });

    /// <summary>模型两线（近/远）+ 纸空间布局视口（viewTarget=(0,0) viewHeight=200 纸面 1000×700）。</summary>
    private static string LayoutDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(5000, 5000, 0), EndPoint = new XYZ(6000, 5000, 0) });
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
    });

    private static (MainWindow Window, string Path, string ProjectDir) Show(string dxfPath)
    {
        string projectDir = UniqueProjectDir();
        var vm = new MainViewModel(dxfPath, projectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, dxfPath, projectDir);
    }

    private static void Cleanup(MainWindow window, string path, string projectDir)
    {
        window.Close();
        File.Delete(path);
        if (Directory.Exists(projectDir)) Directory.Delete(projectDir, true);
    }

    [AvaloniaFact]
    public void SheetSelector_SwitchesCanvasItems()
    {
        (MainWindow window, string path, string projectDir) = Show(TwoSheetDxf());
        try
        {
            Assert.NotNull(window.SheetSelector);
            Assert.Equal(2, window.SheetSelector!.ItemCount);
            Assert.Equal(0, window.SheetSelector.SelectedIndex); // 默认首幅面
            // 首幅面：仅框 0 内实体（含一层名文字，不含二层）
            Assert.Contains(window.Canvas!.Items, i => i.Text == "一层电力平面图");
            Assert.DoesNotContain(window.Canvas.Items, i => i.Text == "二层电力平面图");
            Assert.Contains("一层电力平面图", window.Status!.Text);

            // 切换第二幅面 → 仅框 1 内实体
            window.SheetSelector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.Canvas.Items, i => i.Text == "二层电力平面图");
            Assert.DoesNotContain(window.Canvas.Items, i => i.Text == "一层电力平面图");
            Assert.Contains("二层电力平面图", window.Status.Text);
            // 框 1 内实体锚点 X 均 ≥2000（按幅面断言，R4 加强）
            Assert.All(window.Canvas.Items, i => Assert.True(i.Points[0].X >= 2000));
        }
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void SheetSelector_LayoutSheet_BuildsViewportItems()
    {
        (MainWindow window, string path, string projectDir) = Show(LayoutDxf());
        try
        {
            // sheets = S0（模型）+ 布局1（布局视口）
            Assert.Equal(2, window.SheetSelector!.ItemCount);
            // 默认模型幅面 S0：两线锚点均在 ModelBounds 内 → 全保留
            Assert.Equal(2, window.Canvas!.Items.Count);

            window.SheetSelector.SelectedIndex = 1; // 布局幅面
            Dispatcher.UIThread.RunJobs();
            // BuildViewport：仅近线入视口区域，映射到纸面 (500,350)
            var item = Assert.Single(window.Canvas.Items);
            Assert.Equal(RenderKind.Line, item.Kind);
            Assert.Equal(500, item.Points[0].X, 6);
            Assert.Equal(350, item.Points[0].Y, 6);
            Assert.Contains("布局1", window.Status!.Text);
        }
        finally { Cleanup(window, path, projectDir); }
    }

    [AvaloniaFact]
    public void SheetSelector_SingleSheet_LegacyUnaffected()
    {
        // 单幅面（无图框）夹具：默认 S0 覆盖全部实体，legacy 行为无感（R4 单幅面无感）
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            doc.Entities.Add(new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(10, 20, 0) });
        });
        (MainWindow window, _, string projectDir) = Show(path);
        try
        {
            Assert.Equal(1, window.SheetSelector!.ItemCount);
            Assert.Equal(2, window.Canvas!.Items.Count); // 全保留
        }
        finally { Cleanup(window, path, projectDir); }
    }
}
