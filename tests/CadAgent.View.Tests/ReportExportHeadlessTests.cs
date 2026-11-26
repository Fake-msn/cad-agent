using System.Text;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 审图报告导出（View 入口）headless 覆盖：点击「导出报告」→ 重评（check-run 审计）
/// → PDF 落盘 &lt;项目&gt;/reports/ + 缩略图真实渲染（audit params thumbnail=true）
/// → report-exported 审计 + 链验证 → 行内成功状态。
/// </summary>
public class ReportExportHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-report-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ProjectDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("W-墙体");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(2000, 0, 0), Layer = wall });
                // 字高 4.2 → R-50001-5.0.2 recommended issue（报告问题区非空）
                doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(10, 10, 0), Value = "字高违规", Height = 4.2, Layer = wall });
            });
        }

        public void Dispose()
        {
            try { File.Delete(DxfPath); } catch { }
            try { if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true); } catch { }
        }
    }

    private static (MainWindow Window, Fixture F) ShowWindow()
    {
        var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, f);
    }

    private static void Cleanup(MainWindow window, Fixture f)
    {
        window.Close();
        // 报告导出会创建 audit.db（check-run/report-exported）；SQLite 连接池需清后再删目录
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        f.Dispose();
    }

    /// <summary>点击导出并等待状态发布（导出完成才写 ReportStatus）。</summary>
    private static async Task ClickExportAndWaitAsync(MainWindow window)
    {
        Assert.NotNull(window.ExportReportButton);
        window.ExportReportButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (window.ReportStatus?.IsVisible == true) return;
            await Task.Delay(50);
        }
        Assert.Fail("报告导出未在 30s 内完成（ReportStatus 未发布）");
    }

    [AvaloniaFact]
    public async Task ExportButton_WritesPdfAuditsAndShowsSuccess()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            await ClickExportAndWaitAsync(window);

            Assert.NotNull(window.ReportStatus);
            Assert.Contains("已导出", window.ReportStatus!.Text);
            string path = window.ReportStatus.Text!.Split("已导出: ")[1].Trim();
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.Combine(f.ProjectDir, "reports"), path);
            Assert.EndsWith("-review.pdf", path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 1000);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

            using var store = new SqliteAuditStore(Path.Combine(f.ProjectDir, ".cadagent", "audit.db"));
            var records = store.ReadAll();
            Assert.Contains(records, r => r.Entry.Kind == "check-run");
            AuditRecord exported = Assert.Single(records.Where(r => r.Entry.Kind == "report-exported"));
            Assert.Equal("view-report", exported.Entry.Source);
            using var pars = JsonDocument.Parse(exported.Entry.ParamsJson);
            Assert.True(pars.RootElement.GetProperty("thumbnail").GetBoolean(),
                "当前幅面缩略图应真实渲染并嵌入报告");
            Assert.True(pars.RootElement.GetProperty("issueCount").GetInt32() >= 1);
            Assert.True(store.VerifyChain(out string chainError), chainError);
        }
        finally
        {
            Cleanup(window, f);
        }
    }

    [AvaloniaFact]
    public void Window_ExposesExportButton_InitiallyEnabled()
    {
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.NotNull(window.ExportReportButton);
            Assert.True(window.ExportReportButton!.IsEnabled);
            Assert.NotNull(window.ReportStatus);
            Assert.False(window.ReportStatus!.IsVisible);
        }
        finally
        {
            Cleanup(window, f);
        }
    }
}
