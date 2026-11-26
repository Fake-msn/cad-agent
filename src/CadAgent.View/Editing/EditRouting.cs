using System.Globalization;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.View.Rendering;

namespace CadAgent.View.Editing;

/// <summary>编辑空间意图：模型（视口内）、纸面、只读、块子图元、不可用（保留位）。</summary>
public enum EditSpaceIntent
{
    Model,
    Paper,
    Locked,
    BlockChild,

    /// <summary>保留位：Plan-1 路由不产出（twist 归 <see cref="Model"/> + 不可映射），勿据此分支。</summary>
    Unavailable,
}

/// <summary>路由结果：意图 + 纸→模型是否可映射 + 不可映射时的用户可读原因。</summary>
/// <param name="Intent">编辑意图。</param>
/// <param name="CanMapModel">纸→模型映射是否可用；仅 <see cref="EditSpaceIntent.Model"/> 与模型空间的
/// <see cref="EditSpaceIntent.BlockChild"/> 可能为 true。</param>
/// <param name="Reason">用户可读原因：仅 Locked、不可映射的 Model、不可映射的 BlockChild 非 null；
/// Paper 与可映射的 Model/BlockChild 为 null。</param>
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

        // 块子图元：Plan-1 调用方按父实体整体处理（Imp#3）。模型空间子图元需模型增量，
        // 故保留可映射性（与 Model 同判据/同原因）；纸面子图元不消费映射，仅分类。
        if (hit?.BlockName is not null)
        {
            return hit.Space == RenderSpace.ModelSpace
                ? ModelLikeRoute(EditSpaceIntent.BlockChild, vp)
                : new EditRoute(EditSpaceIntent.BlockChild, false, "块子图元");
        }

        if (hit is not null)
        {
            return hit.Space == RenderSpace.ModelSpace
                ? ModelLikeRoute(EditSpaceIntent.Model, vp)
                : new EditRoute(EditSpaceIntent.Paper, false, null);
        }

        if (vp is not null)
        {
            var r = vp.PaperRect;
            if (paperPoint.X >= r.MinX && paperPoint.X <= r.MaxX
                && paperPoint.Y >= r.MinY && paperPoint.Y <= r.MaxY)
                return ModelLikeRoute(EditSpaceIntent.Model, vp);
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

    /// <summary>Model 与模型空间 BlockChild 共用同一可映射性/原因口径。</summary>
    private static EditRoute ModelLikeRoute(EditSpaceIntent intent, LayoutViewportContext? vp) => new(
        intent,
        vp is not null && vp.CanMapModel,
        MapReason(vp));

    private static string? MapReason(LayoutViewportContext? vp)
    {
        if (vp is null) return "当前布局无有效视口";
        if (vp.CanMapModel) return null;
        // 非退化仍不可映射 = 视口旋转（CanMapModel 已含 |Twist| 阈值，此处不再重复字面量）。
        // R14：坐标无关动作同样拒绝；项2 B 起纸面编辑已启用，但本原因只服务模型意图
        // （纸面路由不经 CanMapModel），故文案只谈布局内模型编辑，不涉及纸面。
        return vp.IsDegenerate
            ? "当前布局无有效视口"
            : "视口带旋转，布局内模型编辑暂不支持";
    }
}
