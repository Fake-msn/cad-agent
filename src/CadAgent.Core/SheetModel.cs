namespace CadAgent.Core;

/// <summary>幅面登记（模型空间图框幅面或布局视口幅面统一模型）。</summary>
public sealed record SheetRecord(
    string SheetId, string Name, string? Number, string? Scale, string? Discipline, string? Date,
    (double MinX, double MinY, double MaxX, double MaxY) FrameBounds,
    string? LayoutName, IReadOnlyDictionary<string, string> TitleAttributes);

/// <summary>视口模型→纸空间变换数学（布局视口幅面用，Task 5 消费）。</summary>
public static class ViewportTransform
{
    /// <summary>纸面/模型比例 s = 纸高/视高；视高退化时恒为 1（正逆两向同约定，仍互逆）。</summary>
    public static double ScaleOf(double paperHeight, double viewHeight) =>
        viewHeight > LayoutViewportContext.MinViewHeight ? paperHeight / viewHeight : 1;

    /// <summary>模型点→纸空间点：以 ViewTarget 为中心、ViewHeight 对应纸面视口高度线性缩放。</summary>
    public static (double X, double Y) ModelToPaper(
        (double X, double Y) model, (double X, double Y) viewTarget, double viewHeight,
        (double X, double Y) paperCenter, double paperHeight)
    {
        double s = ScaleOf(paperHeight, viewHeight);
        return (paperCenter.X + (model.X - viewTarget.X) * s, paperCenter.Y + (model.Y - viewTarget.Y) * s);
    }

    /// <summary>纸空间点→模型点：ModelToPaper 的精确逆（同一比例约定，退化视高 → s=1）。</summary>
    public static (double X, double Y) PaperToModel(
        (double X, double Y) paper, (double X, double Y) viewTarget, double viewHeight,
        (double X, double Y) paperCenter, double paperHeight)
    {
        double s = ScaleOf(paperHeight, viewHeight);
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
    /// <summary>视高退化阈值；装配层复用之，勿再硬编码 1e-12。</summary>
    public const double MinViewHeight = 1e-12;

    /// <summary>退化视口（视高/纸宽/纸高非正）：Scale 与模型映射不可信，装配方（View 层 ContextFrom）须拒绝。</summary>
    public bool IsDegenerate =>
        !(ViewHeight > MinViewHeight) || !(PaperWidth > 0) || !(PaperHeight > 0);

    /// <summary>纸面/模型比例；退化视口返回 1（不可映射，仅供诊断）。</summary>
    public double Scale => IsDegenerate ? 1 : ViewportTransform.ScaleOf(PaperHeight, ViewHeight);

    /// <summary>
    /// 模型映射可用：非退化且视图扭转为零（|Twist| &lt;= 1e-9，边界含）。
    /// 非矩形裁剪区 ACadSharp 3.8.35 无法识别，不在此判定；需要时由装配方从原始组码另行排除。
    /// </summary>
    public bool CanMapModel => !IsDegenerate && Math.Abs(TwistRadians) <= 1e-9;

    public (double MinX, double MinY, double MaxX, double MaxY) PaperRect => (
        PaperCenter.X - PaperWidth / 2, PaperCenter.Y - PaperHeight / 2,
        PaperCenter.X + PaperWidth / 2, PaperCenter.Y + PaperHeight / 2);

    public (double X, double Y) PaperToModel((double X, double Y) paper) =>
        ViewportTransform.PaperToModel(paper, ViewTarget, ViewHeight, PaperCenter, PaperHeight);
}
