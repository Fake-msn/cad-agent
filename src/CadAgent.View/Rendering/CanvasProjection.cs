using CadAgent.View.ViewModels;

namespace CadAgent.View.Rendering;

/// <summary>
/// 绘制器专用变换快照（项3 T2）：在帧开始时冻结 ViewModel 的有效缩放/平移/视口，
/// 之后绘制不再回读 VM（Task 3 位图缓存亦复用该快照语义）。
/// 数学与 <see cref="CanvasViewModel.WorldToScreen"/> 完全一致：
/// screenX = (worldX-MinX)*Scale + PanX + OffsetX；
/// screenY = ViewportH - ((worldY-MinY)*Scale + PanY) - OffsetY（CAD Y-up → 屏幕 Y-down）。
/// <see cref="ExtraOffsetX"/>/<see cref="ExtraOffsetY"/> 仅叠加到屏幕输出（位图缓存偏移），
/// <see cref="ScreenToWorld"/> 不含额外偏移——调用方先扣除再逆变换。
/// 纯 C#（无 Avalonia 依赖），可单测。
/// </summary>
public sealed record CanvasProjection(
    double MinX,
    double MinY,
    double Scale,
    double PanX,
    double PanY,
    double OffsetX,
    double OffsetY,
    double ViewportW,
    double ViewportH,
    double ExtraOffsetX = 0,
    double ExtraOffsetY = 0)
{
    /// <summary>从画布视图模型冻结当前变换（extra 为缓存位图平移偏移，默认 0）。</summary>
    public static CanvasProjection From(CanvasViewModel vm, double extraOffsetX = 0, double extraOffsetY = 0) =>
        new(vm.OriginX, vm.OriginY, vm.Scale, vm.Pan.X, vm.Pan.Y,
            vm.OffsetX, vm.OffsetY, vm.ViewportWidth, vm.ViewportHeight,
            extraOffsetX, extraOffsetY);

    /// <summary>
    /// fit 视图投影（项3 T4 快照）：与 <see cref="CanvasViewModel.Fit"/> 同数学
    /// （留白系数、居中、Zoom=1/Pan=0），但不构造视图模型/索引（大图快照省 O(n) 索引）。
    /// 非正视口 → 缩放 1、偏移 0（同 Fit 退化口径）。
    /// </summary>
    public static CanvasProjection FitView(
        (double MinX, double MinY, double MaxX, double MaxY) bounds, double viewportW, double viewportH)
    {
        double width = bounds.MaxX - bounds.MinX;
        double height = bounds.MaxY - bounds.MinY;
        if (viewportW <= 0 || viewportH <= 0)
            return new(bounds.MinX, bounds.MinY, 1, 0, 0, 0, 0, viewportW, viewportH);

        double sx = width > 1e-12 ? viewportW / width : double.PositiveInfinity;
        double sy = height > 1e-12 ? viewportH / height : double.PositiveInfinity;
        double s = Math.Min(sx, sy);
        double scale = double.IsFinite(s) ? s * CanvasViewModel.FitMargin : 1;
        return new(bounds.MinX, bounds.MinY, scale, 0, 0,
            (viewportW - width * scale) / 2, (viewportH - height * scale) / 2, viewportW, viewportH);
    }

    /// <summary>世界 → 屏幕（Y 翻转 + 额外偏移）。</summary>
    public (double X, double Y) WorldToScreen(double x, double y) =>
        ((x - MinX) * Scale + PanX + OffsetX + ExtraOffsetX,
         ViewportH - ((y - MinY) * Scale + PanY) - OffsetY - ExtraOffsetY);

    /// <summary>屏幕 → 世界（不含额外偏移；<see cref="WorldToScreen"/> 的主变换逆）。</summary>
    public (double X, double Y) ScreenToWorld(double sx, double sy) =>
        ((sx - PanX - OffsetX) / Scale + MinX,
         (ViewportH - sy - OffsetY - PanY) / Scale + MinY);

    /// <summary>
    /// 当前视口世界矩形（归一，Min≤Max）；未 Fit（视口 ≤0）→ 反相空标记 (0,0,-1,-1)，
    /// 调用方以 Min&gt;Max 判空（与 <see cref="CanvasViewModel.WorldViewport"/> 同口径）。
    /// </summary>
    public (double MinX, double MinY, double MaxX, double MaxY) WorldViewport() =>
        WorldRectOf(0, 0, ViewportW, ViewportH);

    /// <summary>
    /// 屏幕矩形 → 世界矩形（含额外偏移，用于缓存位图的外扩视口）；归一 Min≤Max。
    /// 未 Fit（视口 ≤0）→ 反相空标记 (0,0,-1,-1)（同 <see cref="WorldViewport"/> 口径）。
    /// </summary>
    public (double MinX, double MinY, double MaxX, double MaxY) WorldRectOf(
        double sx0, double sy0, double sx1, double sy1)
    {
        if (ViewportW <= 0 || ViewportH <= 0) return (0, 0, -1, -1);
        double x0 = (sx0 - PanX - OffsetX - ExtraOffsetX) / Scale + MinX;
        double x1 = (sx1 - PanX - OffsetX - ExtraOffsetX) / Scale + MinX;
        double y0 = (ViewportH - sy0 - OffsetY - ExtraOffsetY) / Scale + MinY;
        double y1 = (ViewportH - sy1 - OffsetY - ExtraOffsetY) / Scale + MinY;
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }
}
