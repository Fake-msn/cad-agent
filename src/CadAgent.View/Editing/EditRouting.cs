using System.Globalization;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.View.Rendering;

namespace CadAgent.View.Editing;

/// <summary>编辑空间意图：模型（视口内）、纸面、只读、块子图元、不可用（保留位）。</summary>
public enum EditSpaceIntent { Model, Paper, Locked, BlockChild, Unavailable }

/// <summary>路由结果：意图 + 纸→模型是否可映射 + 不可映射时的用户可读原因。</summary>
public sealed record EditRoute(EditSpaceIntent Intent, bool CanMapModel, string? Reason);

/// <summary>
/// 编辑空间路由纯函数（项2 A）：按命中图元（锁定 &gt; 块子图元 &gt; 空间）分类编辑意图；
/// 未命中时以纸面点是否落在视口 PaperRect 内判定。可映射性统一委托
/// <see cref="LayoutViewportContext.CanMapModel"/>（无上下文/旋转 → false + 原因）。
/// 纯逻辑：不依赖 Avalonia/WPF/IO，可 headless 单测。
/// </summary>
public static class EditRouting
{
    /// <summary>
    /// 判定序（固定）：Locked → BlockChild → 命中空间（Model/Paper）→ 未命中按点在视口矩形内。
    /// <paramref name="paperPoint"/> 仅在未命中时参与判定（命中图元的空间优先于点位置）。
    /// </summary>
    public static EditRoute Route(LayoutViewportContext? vp, RenderItem? hit, (double X, double Y) paperPoint)
    {
        if (hit?.Locked == true)
            return new EditRoute(EditSpaceIntent.Locked, false, "视口边框只读");

        // 块子图元：Plan-1 调用方按父实体整体处理，路由只分类不映射。
        if (hit?.BlockName is not null)
            return new EditRoute(EditSpaceIntent.BlockChild, false, "块子图元");

        if (hit is not null)
        {
            return hit.Space == RenderSpace.ModelSpace
                ? ModelRoute(vp)
                : new EditRoute(EditSpaceIntent.Paper, false, null);
        }

        if (vp is not null)
        {
            var r = vp.PaperRect;
            if (paperPoint.X >= r.MinX && paperPoint.X <= r.MaxX
                && paperPoint.Y >= r.MinY && paperPoint.Y <= r.MaxY)
                return ModelRoute(vp);
        }

        return new EditRoute(EditSpaceIntent.Paper, false, null);
    }

    /// <summary>
    /// 布局视口 → 纯数值上下文：纸边界视口（仅边界、不含模型视图）与退化视口返回 null
    /// （退化判据唯一来源 <see cref="LayoutViewportContext.IsDegenerate"/>，不重复阈值字面量）。
    /// </summary>
    public static LayoutViewportContext? ContextFrom(Viewport vp)
    {
        ArgumentNullException.ThrowIfNull(vp);
        if (vp.RepresentsPaper) return null; // R9：纸边界视口不是可编辑布局视口

        var ctx = new LayoutViewportContext(
            vp.Handle.ToString(CultureInfo.InvariantCulture),
            (vp.Center.X, vp.Center.Y),
            vp.Width, vp.Height, vp.ViewHeight,
            (vp.ViewTarget.X, vp.ViewTarget.Y),
            vp.TwistAngle);
        return ctx.IsDegenerate ? null : ctx;
    }

    private static EditRoute ModelRoute(LayoutViewportContext? vp) => new(
        EditSpaceIntent.Model,
        vp is not null && vp.CanMapModel,
        MapReason(vp));

    private static string? MapReason(LayoutViewportContext? vp)
    {
        if (vp is null) return "当前布局无有效视口";
        if (vp.CanMapModel) return null;
        // 非退化仍不可映射 = 视口旋转（CanMapModel 已含 |Twist| 阈值，此处不再重复字面量）。
        return vp.IsDegenerate
            ? "当前布局无有效视口"
            : "视口带旋转，纸→模型映射未支持；纸面编辑仍可用";
    }
}
