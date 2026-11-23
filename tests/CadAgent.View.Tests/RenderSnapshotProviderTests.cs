using System.Runtime.InteropServices;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
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
/// 视觉接线 T2：RenderSnapshotProvider（IRenderSnapshot 生产实现，离屏 Skia PNG）。
/// [AvaloniaFact] 真实 Skia 帧会话已初始化 headless 平台——R1 守卫须检测已初始化跳过
/// SetupWithoutStarting（复用现有平台，不冲突）。夹具镜像 SheetRegistryTests 双幅面 doc；
/// DrawingSession ctor 为 Data internal（IVT 仅 Data.Tests）→ 循 View.Tests 既有惯例
/// DxfFixture.CreateTempDxf + DrawingSession.Load 合成 session。
/// </summary>
public class RenderSnapshotProviderTests
{
    /// <summary>双图框幅面（镜像 SheetRegistryTests.TwoSheetDoc）：框 0 (0,0)-(1000,700)、
    /// 框 1 (2000,0)-(3000,700)，各含幅面名文字 + 框内 E-WIRE 线（显式 ACI 7 白，像素探针确定性）。</summary>
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
            ins.Attributes.Add(new AttributeEntity { Tag = "比例", Value = "1:100" });
            doc.Entities.Add(ins);
            doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(i * 2000 + 500, 650, 0), Value = i == 0 ? "一层电力平面图" : "二层电力平面图", Height = 30, Layer = new Layer("图框") });
            doc.Entities.Add(new Line { StartPoint = new XYZ(i * 2000 + 100, 100, 0), EndPoint = new XYZ(i * 2000 + 200, 100, 0), Layer = new Layer("E-WIRE") { Color = new Color(7) } });
        }
    });

    /// <summary>模型两线（近/远）+ 纸空间布局视口（镜像 SheetSelectorHeadlessTests.LayoutDxf）。</summary>
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

    private static DrawingSession SessionWithOneSheet()
    {
        string path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        try { return DrawingSession.Load(path); }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_ModelSheetTile_NonEmptyPngWithSignature()
    {
        string path = TwoSheetDxf();
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            var sheet = session.Sheets[0];
            byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 400, 300);
            Assert.True(png.Length > 100);
            Assert.Equal(0x89, png[0]); Assert.Equal(0x50, png[1]); // PNG signature
            // 目标像素尺寸兑现（SheetTiler 字高反算的可读性前提）
            using var bmp = new Bitmap(new MemoryStream(png));
            Assert.Equal(new PixelSize(400, 300), bmp.PixelSize);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_TileOutsideContent_DarkPixels()
    {
        // 瓦片矩形 (400,300)-(600,500) 位于框 0 中央：图框线锚点在四角、文字 (500,650)、
        // E-WIRE 线 (100,100) 锚点均在瓦片外 → CropToFrame 空集 → 整幅背景深色 #1E1E1E。
        string path = TwoSheetDxf();
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            byte[] png = provider.RenderPng(session.Sheets[0].SheetId, (400, 300, 600, 500), 100, 100);
            (int r, int g, int b) = CenterPixel(png);
            Assert.True(Math.Abs(r - 0x1E) <= 8 && Math.Abs(g - 0x1E) <= 8 && Math.Abs(b - 0x1E) <= 8,
                $"瓦片中心像素应≈背景 #1E1E1E，实际 ({r},{g},{b})");
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_UnknownSheet_Throws()
    {
        var provider = new RenderSnapshotProvider(SessionWithOneSheet());
        Assert.ThrowsAny<ArgumentException>(() => provider.RenderPng("NOPE", (0, 0, 1, 1), 10, 10));
    }

    [AvaloniaFact]
    public void RenderPng_SubTileRect_ContentAtRectMappedPixel()
    {
        // 钉住瓦片 rect 映射语义：子瓦片 (0,0)-(500,350) 200×140px → Fit baseScale=0.36、
        // offsetX=10、offsetY=7；E-WIRE 线 (100,100)-(200,100) 应落在屏幕 y=97、x∈[46,82]。
        // 若 Arrange 触发的 SizeChanged 把 VM 重 Fit 到图元包围盒 (0,0)-(1000,100)（错误口径），
        // 线会落在 y=61、x∈[28,46] → 本探针 (64,97) 邻域暗 → 测试失败。
        string path = TwoSheetDxf();
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            byte[] png = provider.RenderPng(session.Sheets[0].SheetId, (0, 0, 500, 350), 200, 140);
            // 阈值 100：1px 抗锯齿描边半覆盖混合 ≈143（背景 30 + 0.5×225）；错误口径下
            // 探针邻域纯背景 30 → 判别充分。
            Assert.True(NeighborhoodBrightness(png, 64, 97) > 100,
                "E-WIRE 线须按请求 rect 映射出现在 (64,97) 邻域（白色 ACI 7）");
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void RenderPng_LayoutSheetTile_NonEmptyPng()
    {
        // 布局幅面分支冒烟：BuildViewport 纸面映射 + CropToFrame → PNG（视口句柄经
        // TitleAttributes["viewportHandle"] 解析）。近线入视口、远线 (5000,5000) 不入。
        string path = LayoutDxf();
        try
        {
            var session = DrawingSession.Load(path);
            var provider = new RenderSnapshotProvider(session);
            var sheet = Assert.Single(session.Sheets, s => s.LayoutName == "布局1");
            byte[] png = provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 400, 300);
            Assert.True(png.Length > 100);
            Assert.Equal(0x89, png[0]); Assert.Equal(0x50, png[1]); // PNG signature
        }
        finally { File.Delete(path); }
    }

    /// <summary>PNG 解码 → 中心像素 RGB（WriteableBitmap Bgra8888 承接 Bitmap.CopyPixels）。</summary>
    private static (int R, int G, int B) CenterPixel(byte[] png)
    {
        using var bmp = new Bitmap(new MemoryStream(png));
        using var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer fb = wb.Lock();
        bmp.CopyPixels(fb, AlphaFormat.Premul);
        int offset = (fb.Size.Height / 2) * fb.RowBytes + (fb.Size.Width / 2) * 4;
        return (Marshal.ReadByte(fb.Address, offset + 2),
                Marshal.ReadByte(fb.Address, offset + 1),
                Marshal.ReadByte(fb.Address, offset));
    }

    /// <summary>5×5 邻域最大 RGB 通道（1px 抗锯齿描边可能不全覆盖数学点；同 CanvasHeadlessTests 口径）。</summary>
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
}
