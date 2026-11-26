using System.Diagnostics;
using System.Runtime.InteropServices;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.Rendering;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 T4：快照/缩略图改走 Refined + LOD 绘制器（CanvasProjection.FitView + SnapshotControl）。
/// 功能回归夹具：小混合图元（线/圆）+ 文字密集（800 Text）+ 超大 Hatch（&gt;2000 路径点）。
/// 耗时只记日志（CI 不硬断言绝对耗时，spec §6）；确定性 = 同输入两次渲染字节一致。
/// 投影口径与 CanvasViewModel.Fit 一致（0.9 留白+居中），探针坐标按此推导。
/// </summary>
public class RenderSnapshotPerfTests
{
    private const int Background = 0x1E;

    /// <summary>图框 4 边钉住内容范围 (0,0)-(1000,700)（FrameBounds 确定性）。</summary>
    private static void AddFrame(CadDocument doc, Layer layer)
    {
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0), Layer = layer });
        doc.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0), Layer = layer });
        doc.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0), Layer = layer });
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0), Layer = layer });
    }

    /// <summary>400×300 请求下 (0,0)-(1000,700) 的 Fit 映射：scale=0.36, offset=(20,24)。</summary>
    private const double Scale400 = 0.36;

    private static (int X, int Y) ToScreen400(double wx, double wy) =>
        ((int)Math.Round(wx * Scale400 + 20), (int)Math.Round(300 - wy * Scale400 - 24));

    /// <summary>小混合夹具：白框 + 粗线（贯穿水平线）+ 圆（中心 500,350 R=100）。</summary>
    private static string MixedDxf() => DxfFixture.CreateTempDxf(doc =>
    {
        var white = new Layer("E-WIRE") { Color = new Color(7) };
        AddFrame(doc, white);
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 350, 0), EndPoint = new XYZ(1000, 350, 0), Layer = white });
        doc.Entities.Add(new Circle { Center = new XYZ(500, 350, 0), Radius = 100, Layer = white });
    });

    [AvaloniaFact]
    public void RenderPng_MixedFixture_PrimitivesAtExpectedPixels_BackgroundAtCorner()
    {
        string path = MixedDxf();
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            var sheet = session.Sheets[0];
            byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 400, 300);

            Assert.True(png.Length > 100);
            using var bmp = new Bitmap(new MemoryStream(png));
            Assert.Equal(new PixelSize(400, 300), bmp.PixelSize);
            bmp.Dispose();

            // 水平中线 (0,350)-(1000,350) → 屏幕 y=150、x∈[20,380]
            (int lx, int ly) = ToScreen400(500, 350);
            Assert.True(NeighborhoodBrightness(png, lx, ly) > 100, "中线须出现在映射像素处");
            // 圆顶点 (500,450) → 屏幕 y=114；圆内 (560,400) 无图元 → 背景
            (int cx, int cy) = ToScreen400(500, 450);
            Assert.True(NeighborhoodBrightness(png, cx, cy) > 100, "圆轮廓须出现在映射像素处");
            (int ix, int iy) = ToScreen400(560, 400);
            Assert.True(NeighborhoodBrightness(png, ix, iy) < 80, "圆内部（未填充）应为背景");
            // 空角（Fit 留白内，无图元）
            (int r, int g, int b) = Pixel(png, 4, 4);
            Assert.True(Math.Abs(r - Background) <= 8 && Math.Abs(g - Background) <= 8 && Math.Abs(b - Background) <= 8,
                $"左上角应为背景 #1E1E1E，实际 ({r},{g},{b})");
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_TextHeavyFixture_TextPixelsPresent()
    {
        // 800 TextEntity（40×20 网格，字高 20）→ 800×600 渲染 scale=0.72，字高 14.4px（Refined 全字形）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var white = new Layer("E-WIRE") { Color = new Color(7) };
            AddFrame(doc, white);
            for (int row = 0; row < 20; row++)
                for (int col = 0; col < 40; col++)
                {
                    doc.Entities.Add(new TextEntity
                    {
                        InsertPoint = new XYZ(20 + col * 24, 20 + row * 33, 0),
                        Value = $"T{col:00}",
                        Height = 20,
                        Layer = white,
                    });
                }
        });
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            var sheet = session.Sheets[0];
            var sw = Stopwatch.StartNew();
            byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 800, 600);
            sw.Stop();
            Console.WriteLine($"[perf] RenderPng 800 texts 800x600: {sw.ElapsedMilliseconds}ms, {png.Length} bytes");

            Assert.True(png.Length > 100);
            // 800×600 over (0,0)-(1000,700): scale=0.72, offset=(40,48)。
            // 首字 (20,20) 基线屏幕 (54.4, 537.6)；字形盒 x∈[54,80], y∈[523,538]。
            int bright = BrightPixelsIn(png, 52, 520, 84, 540);
            Assert.True(bright > 10, $"文字区域应有字形像素（亮像素数 {bright}）");
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_HatchOver2000Points_OutlinePresent_DoesNotThrow()
    {
        // 2100 条竖边（锯齿带 100..~835 × 500..600）→ GetPoints 输出 >2000 点；
        // Refined 档 DecideHatch 判 Simplified → 只绘首路径轮廓（仍非空）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var white = new Layer("E-WIRE") { Color = new Color(7) };
            AddFrame(doc, white);
            var hatch = new Hatch { IsSolid = false };
            var edges = new List<Hatch.BoundaryPath.Edge>();
            for (int i = 0; i < 2100; i++)
            {
                double x = 100 + i * 0.35;
                edges.Add(new Hatch.BoundaryPath.Line(new Line
                {
                    StartPoint = new XYZ(x, 600, 0),
                    EndPoint = new XYZ(x, 500, 0),
                }));
            }
            hatch.Paths.Add(new Hatch.BoundaryPath(edges));
            doc.Entities.Add(hatch);
        });
        try
        {
            var session = DrawingSession.Load(path);
            var hatchItem = Assert.Single(RenderModelBuilder.Build(session), i => i.Kind == RenderKind.Hatch);
            Assert.True(hatchItem.Points.Count > 2000, $"夹具须 >2000 路径点，实际 {hatchItem.Points.Count}");

            var provider = new RenderSnapshotProvider(session);
            var sheet = session.Sheets[0];
            byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 400, 300);

            Assert.True(png.Length > 100);
            // 齿带内部（首边 x=100 右侧 75 → 屏幕 ~83，y=550 → ~78）：Refined 轮廓可见；
            // 若按 Interactive/Bbox 档仅画包围盒，此内部点应为背景。
            (int hx, int hy) = ToScreen400(175, 550);
            Assert.True(NeighborhoodBrightness(png, hx, hy) > 100,
                "超大 Hatch 在 Refined 档须绘轮廓（内部齿带像素应亮）");
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_SameInputTwice_IdenticalBytes()
    {
        string path = MixedDxf();
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            var sheet = session.Sheets[0];
            byte[] a = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 320, 240);
            byte[] b = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 320, 240);
            Assert.Equal(a, b); // 确定性：无计时器/缓存状态影响输出
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_Synthetic20kItems_TimingLogged()
    {
        // 耗时仅入日志（不硬断言）：20k 线 + 幅面 Fit 渲染 800×600。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            for (int i = 0; i < 20_000; i++)
            {
                double x = (i % 200) * 5;
                double y = (i / 200) * 5;
                doc.Entities.Add(new Line
                {
                    StartPoint = new XYZ(x, y, 0),
                    EndPoint = new XYZ(x + 4, y + 1, 0),
                });
            }
        });
        try
        {
            var swLoad = Stopwatch.StartNew();
            var session = DrawingSession.Load(path);
            swLoad.Stop();
            var provider = new RenderSnapshotProvider(session);
            var sheet = session.Sheets[0];
            var sw = Stopwatch.StartNew();
            byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 800, 600);
            sw.Stop();
            Console.WriteLine($"[perf] 20k items: load={swLoad.ElapsedMilliseconds}ms, RenderPng 800x600={sw.ElapsedMilliseconds}ms, {png.Length} bytes");
            Assert.True(png.Length > 100);
        }
        finally { File.Delete(path); }
    }

    // ---- 像素探针 ----

    private static (int R, int G, int B) Pixel(byte[] png, int x, int y)
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        using var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer fb = wb.Lock();
        bmp.CopyPixels(fb, AlphaFormat.Premul);
        int offset = y * fb.RowBytes + x * 4;
        return (Marshal.ReadByte(fb.Address, offset + 2),
                Marshal.ReadByte(fb.Address, offset + 1),
                Marshal.ReadByte(fb.Address, offset));
    }

    /// <summary>5×5 邻域最大通道（1px 抗锯齿描边不全覆盖数学点；同既有探针口径）。</summary>
    private static int NeighborhoodBrightness(byte[] png, int x, int y)
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        using var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer fb = wb.Lock();
        bmp.CopyPixels(fb, AlphaFormat.Premul);
        int max = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= fb.Size.Width || py >= fb.Size.Height) continue;
                int offset = py * fb.RowBytes + px * 4;
                for (int c = 0; c < 3; c++)
                    max = Math.Max(max, Marshal.ReadByte(fb.Address, offset + c));
            }
        return max;
    }

    /// <summary>区域内明显高于背景（&gt;60）的像素数。</summary>
    private static int BrightPixelsIn(byte[] png, int x0, int y0, int x1, int y1)
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        using var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer fb = wb.Lock();
        bmp.CopyPixels(fb, AlphaFormat.Premul);
        int count = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(fb.Size.Height - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(fb.Size.Width - 1, x1); x++)
            {
                int offset = y * fb.RowBytes + x * 4;
                for (int c = 0; c < 3; c++)
                    if (Marshal.ReadByte(fb.Address, offset + c) > 60) { count++; break; }
            }
        return count;
    }
}
