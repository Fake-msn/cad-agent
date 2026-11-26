using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using CadAgent.Data;
using CadAgent.Data.Vision;

namespace CadAgent.View.Rendering;

/// <summary>
/// 离屏渲染 provider（生产接线 T2）：幅面瓦片矩形→PNG。Avalonia.Headless 平台
/// （UseHeadlessDrawing=false+UseSkia，HeadlessTestApp 同模式）一次初始化；
/// 模型幅面=CropToFrame 裁剪；布局幅面=BuildViewport 纸面坐标再裁剪。
/// R1 平台守卫：会话已初始化平台（View.Tests headless / 生产窗口模式 Application.Current）
/// → 复用现有平台跳过 SetupWithoutStarting；仅未初始化时（生产 headless 入口进程）才建平台。
/// 窗口模式不受影响——守卫仅 provider 调用触发。
/// 项3 T4：绘制不再经 DrawingCanvas（无位图缓存/计时器/交互状态），改由
/// <see cref="SnapshotControl"/> 以 <see cref="CanvasProjection.FitView"/> + Refined 绘制器直出；
/// 快照纯函数（同输入同像素），须 UI 线程调用（Avalonia 平台约束）。
/// </summary>
public sealed class RenderSnapshotProvider : IRenderSnapshot
{
    private static bool _platformReady;
    private static readonly object _initLock = new();
    private readonly DrawingSession _session;
    private readonly IReadOnlyList<RenderItem> _modelItems;

    public RenderSnapshotProvider(DrawingSession session)
    {
        _session = session;
        _modelItems = RenderModelBuilder.Build(session);
    }

    private sealed class SnapshotApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    /// <summary>
    /// 快照专用离屏控件（项3 T4）：深色背景 + Refined 档全量绘制。
    /// 刻意不含位图交互缓存/静止计时器/选中高亮/中心线等叠加——确定性输出。
    /// </summary>
    private sealed class SnapshotControl : Control
    {
        private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        private readonly IReadOnlyList<RenderItem> _items;
        private readonly CanvasProjection _projection;
        private readonly RenderItemDrawer _drawer = new();
        private readonly PenResolver _pens = new();
        private readonly TextCache _texts = new();

        public SnapshotControl(IReadOnlyList<RenderItem> items, CanvasProjection projection)
        {
            _items = items;
            _projection = projection;
            ClipToBounds = true;
        }

        protected override Size MeasureOverride(Size availableSize) => availableSize;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
            _drawer.BeginFrame();
            // 图元已按 rect 裁剪（锚点/几何相交），快照无需再 cull；无选中/高亮。
            _drawer.Draw(context, _projection, _items, indices: null, RenderQuality.Refined,
                _pens, _texts, selected: null, highlighted: null,
                selectionBudgetExceeded: false, cull: false);
        }
    }

    private static void EnsurePlatform()
    {
        if (_platformReady) return;
        lock (_initLock)
        {
            if (_platformReady) return;
            // R1：检测已初始化平台（headless 测试会话 / 窗口模式）→ 复用，绝不重复 Setup
            // （重复 SetupWithoutStarting 会抛平台已注册异常，破坏既有会话）。
            // 检测口径=Application.Current：Avalonia 11.3.2 ref 程序集中 AvaloniaLocator
            // .Current/.CurrentMutable 访问器为 internal（元数据探针实证，外部程序集 CS0117），
            // 而 headless 测试会话与窗口模式 Setup 均置 Application.Current——单判据即覆盖
            // R1 两分支（平台已初始化⇔Application.Current 非空，本仓库两入口皆然）。
            if (Application.Current != null)
            {
                _platformReady = true;
                return;
            }
            AppBuilder.Configure<SnapshotApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            _platformReady = true;
        }
    }

    public byte[] RenderPng(string sheetId, (double MinX, double MinY, double MaxX, double MaxY) rect, int w, int h)
    {
        var sheet = _session.Sheets.FirstOrDefault(s => s.SheetId == sheetId)
            ?? throw new ArgumentException($"未知幅面: {sheetId}", nameof(sheetId));
        EnsurePlatform();
        IReadOnlyList<RenderItem> source;
        if (sheet.LayoutName == null)
        {
            source = _modelItems;
        }
        else
        {
            string? vpHandle = sheet.TitleAttributes.GetValueOrDefault("viewportHandle");
            var vp = _session.Doc.Layouts.Select(l => l.AssociatedBlock)
                .Where(b => b != null).SelectMany(b => b!.Entities)
                .OfType<ACadSharp.Entities.Viewport>()
                .FirstOrDefault(v => v.Handle.ToString() == vpHandle)
                ?? throw new ArgumentException($"布局幅面缺视口: {sheetId}", nameof(sheetId));
            source = RenderModelBuilder.BuildViewport(_session.Doc, vp);
        }
        // R6（视觉接线 T3）：瓦片级（rect≠幅面 bounds）用几何相交裁剪——穿越瓦片的长导线
        // 锚点在瓦片外，锚点裁剪会整体缺失（电缆可见性缺口）；幅面级（rect==FrameBounds，
        // overview/整幅）保持锚点语义（与幅面归属同口径）。
        IReadOnlyList<RenderItem> items = rect == sheet.FrameBounds
            ? RenderModelBuilder.CropToFrame(source, rect)
            : RenderModelBuilder.CropToFrameIntersect(source, rect);

        // 投影以请求 rect 为准（世界/纸面矩形→w×h 像素，Fit 同口径：0.9 留白+居中）。
        CanvasProjection projection = CanvasProjection.FitView(rect, w, h);
        var control = new SnapshotControl(items, projection);
        control.Measure(new Size(w, h));
        control.Arrange(new Rect(0, 0, w, h));
        // R11③：using 确定性释放——RenderTargetBitmap 非托管像素面（约 19.6MB/瓦×82 瓦级）
        // 不得靠终结器延迟回收（内存峰值面）；Save 完毕即 Dispose。
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h));
        rtb.Render(control);
        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }
}
