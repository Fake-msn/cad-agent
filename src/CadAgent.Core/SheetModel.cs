namespace CadAgent.Core;

/// <summary>幅面登记（模型空间图框幅面或布局视口幅面统一模型）。</summary>
public sealed record SheetRecord(
    string SheetId, string Name, string? Number, string? Scale, string? Discipline, string? Date,
    (double MinX, double MinY, double MaxX, double MaxY) FrameBounds,
    string? LayoutName, IReadOnlyDictionary<string, string> TitleAttributes);

/// <summary>视口模型→纸空间变换数学（布局视口幅面用，Task 5 消费）。</summary>
public static class ViewportTransform
{
    /// <summary>模型点→纸空间点：以 ViewTarget 为中心、ViewHeight 对应纸面视口高度线性缩放。</summary>
    public static (double X, double Y) ModelToPaper(
        (double X, double Y) model, (double X, double Y) viewTarget, double viewHeight,
        (double X, double Y) paperCenter, double paperHeight)
    {
        double s = viewHeight > 1e-12 ? paperHeight / viewHeight : 1;
        return (paperCenter.X + (model.X - viewTarget.X) * s, paperCenter.Y + (model.Y - viewTarget.Y) * s);
    }

    /// <summary>纸空间点→模型点：ModelToPaper 的精确逆（同一比例约定，退化 viewHeight → s=1）。</summary>
    public static (double X, double Y) PaperToModel(
        (double X, double Y) paper, (double X, double Y) viewTarget, double viewHeight,
        (double X, double Y) paperCenter, double paperHeight)
    {
        double s = viewHeight > 1e-12 ? paperHeight / viewHeight : 1;
        return (viewTarget.X + (paper.X - paperCenter.X) / s,
                viewTarget.Y + (paper.Y - paperCenter.Y) / s);
    }
}

/// <summary>布局视口上下文（纯数值，不含 ACadSharp 依赖；由 View 层从 Viewport 装配）。</summary>
public sealed record LayoutViewportContext(
    string ViewportHandle,
    (double X, double Y) PaperCenter,
    double PaperWidth,
    double PaperHeight,
    double ViewHeight,
    (double X, double Y) ViewTarget,
    double TwistRadians)
{
    public double Scale => ViewHeight > 1e-12 ? PaperHeight / ViewHeight : 1;

    // 视图旋转非零时纸→模型逆映射不成立；非矩形裁剪区 ACadSharp 3.8.35 无法识别，不在此判定
    public bool CanMapModel => Math.Abs(TwistRadians) < 1e-9;

    public (double MinX, double MinY, double MaxX, double MaxY) PaperRect => (
        PaperCenter.X - PaperWidth / 2, PaperCenter.Y - PaperHeight / 2,
        PaperCenter.X + PaperWidth / 2, PaperCenter.Y + PaperHeight / 2);

    public (double X, double Y) PaperToModel((double X, double Y) paper) =>
        ViewportTransform.PaperToModel(paper, ViewTarget, ViewHeight, PaperCenter, PaperHeight);
}
