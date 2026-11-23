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
}
