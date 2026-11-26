using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Data.Handlers;

internal static class HandlerHelpers
{
    /// <summary>纸面限定缺 layout 的统一文案（解析器预校验/新建族/属性检索三处共用，防契约漂移）。</summary>
    public const string PaperNeedsLayoutMessage = "space:\"paper\" 须配 layout";

    public static DrawingSession AsDrawing(IDrawingSession session) =>
        session as DrawingSession ?? throw new InvalidOperationException("处理器需要 DrawingSession");

    public static double Req(JsonElement p, string name) => p.GetProperty(name).GetDouble();
    // R12：number→decimal string 镜像 handle 先例（Find :38）强制转换——sheetId 等可选字符串参数
    // 携数值时不再静默 null（解除 handler confinement/作用域）；string/缺失/其余非法形态行为保持。
    public static string? OptString(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetUInt64().ToString(),
            _ => null
        };
    }

    public static Layer ResolveLayer(DrawingSession s, string? name)
    {
        if (string.IsNullOrEmpty(name)) return s.Doc.Layers.First(l => l.Name == "0");
        // CAD layer names are case-insensitive: match existing layers regardless of case
        // so "e-lite" never creates a duplicate of "E-LITE".
        var existing = s.Doc.Layers.FirstOrDefault(l => StringComparer.OrdinalIgnoreCase.Equals(l.Name, name));
        if (existing != null) return existing;
        var layer = new Layer(name);
        s.Doc.Layers.Add(layer);
        return layer;
    }

    /// <summary>新建族（add-*）块内编辑仍不提供：含 block 参数一律拒绝（fail-closed，防静默落到模型顶层）。</summary>
    public static StepResult? RejectBlockParam(JsonElement p) =>
        HasBlockParam(p)
            ? new StepResult(false, EditTargetResolver.BlockDeferredMessage, "")
            : null;

    /// <summary>block 参数存在且非 null（与解析器 HasValue 同口径）。</summary>
    public static bool HasBlockParam(JsonElement p) =>
        p.TryGetProperty("block", out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>
    /// block 与 space/layout/attribute 互斥入口守卫：必须先于 space 通用预校验
    /// （否则 block+space:"model" 会误报"space 仅支持 paper"、block+space:"paper" 误报缺 layout）。
    /// </summary>
    public static StepResult? RejectBlockScopeConflict(JsonElement p) =>
        HasBlockParam(p) && (HasParam(p, "space") || HasParam(p, "layout") || HasParam(p, "attribute"))
            ? new StepResult(false, EditTargetResolver.BlockScopeConflictMessage, "")
            : null;

    private static bool HasParam(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>
    /// 句柄编辑统一目标解析（Plan-3）：block 显式 → 块定义专用文案（未知/受保护/互斥/非法句柄/未命中）；
    /// 否则默认三空间解析（模型→纸面→顶层属性）。
    /// </summary>
    public static EditTarget? ResolveEditTarget(DrawingSession s, JsonElement p, out string error)
    {
        error = "";
        if (!HasBlockParam(p))
        {
            EditTarget? target = EditTargetResolver.TryResolve(s, p, out string? resolveError);
            if (target is null) error = resolveError ?? EditTargetResolver.NotFoundMessage;
            return target;
        }
        EditTarget? blockTarget = EditTargetResolver.ResolveBlockTarget(s, p, out string? blockError);
        if (blockTarget is null) error = blockError ?? EditTargetResolver.NotFoundMessage;
        return blockTarget;
    }

    /// <summary>
    /// R3：space 仅支持 "paper"（大小写不敏感）。其他取值（含 "model"/未知词）在入口即拒，
    /// 不得塌陷为"句柄不存在"（解析层 fail-closed → null 会误报为未找到）。
    /// </summary>
    public static StepResult? RejectUnsupportedSpace(JsonElement p)
    {
        if (!p.TryGetProperty("space", out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return string.Equals(OptString(p, "space"), "paper", StringComparison.OrdinalIgnoreCase)
            ? null
            : new StepResult(false, "space 仅支持 paper", "");
    }

    /// <summary>
    /// 句柄类编辑（delete/move/change-layer/set-text/set-attribute 两形态）的 space/layout/attribute
    /// 语义预校验：先于 Resolver/TopLevelCollections 给出准确原因——显式限定非法时 Resolver
    /// fail-closed 返回 null，调用方会把限定错误误报成"句柄不存在"。
    /// space 取值本身仍由 <see cref="RejectUnsupportedSpace"/> 先行拒绝。
    /// </summary>
    public static StepResult? RejectInvalidTargetScope(DrawingSession s, JsonElement p)
    {
        if (p.TryGetProperty("attribute", out var attr)
            && attr.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && attr.ValueKind != JsonValueKind.True)
            return new StepResult(false, "attribute 仅支持 true", "");
        if (!string.Equals(OptString(p, "space"), "paper", StringComparison.OrdinalIgnoreCase)) return null;
        string? layoutName = OptString(p, "layout");
        if (string.IsNullOrEmpty(layoutName))
            return new StepResult(false, PaperNeedsLayoutMessage, "");
        var layout = EditTargetResolver.FindLayout(s, layoutName);
        if (layout is null || !layout.IsPaperSpace || layout.AssociatedBlock is null)
            return new StepResult(false, $"布局不存在或非纸面布局: {layoutName}", "");
        return null;
    }

    /// <summary>
    /// 新建实体目标集合：默认模型（历史行为不变）；space:"paper"+layout → 该布局纸面关联块
    /// 实体集合（layout 必须存在且 IsPaperSpace，否则 fail-closed）。
    /// </summary>
    public static bool TryResolveAddCollection(DrawingSession s, JsonElement p,
        out CadObjectCollection<Entity>? target, out string error)
    {
        target = null;
        error = "";
        bool hasSpace = p.TryGetProperty("space", out var sp) && sp.ValueKind != JsonValueKind.Null;
        bool hasLayout = p.TryGetProperty("layout", out var ly) && ly.ValueKind != JsonValueKind.Null;
        if (!hasSpace && !hasLayout)
        {
            target = s.Doc.Entities;
            return true;
        }
        if (hasSpace && !string.Equals(OptString(p, "space"), "paper", StringComparison.OrdinalIgnoreCase))
        {
            error = "space 仅支持 paper"; // 与 RejectUnsupportedSpace 同文案（入口已先拒，此处为兜底）
            return false;
        }
        string? layoutName = OptString(p, "layout");
        if (string.IsNullOrEmpty(layoutName))
        {
            error = PaperNeedsLayoutMessage;
            return false;
        }
        var layout = EditTargetResolver.FindLayout(s, layoutName);
        if (layout is null)
        {
            error = $"布局不存在：{layoutName}";
            return false;
        }
        if (!layout.IsPaperSpace || layout.AssociatedBlock is null)
        {
            error = $"布局不是纸面空间：{layoutName}";
            return false;
        }
        target = layout.AssociatedBlock.Entities;
        return true;
    }
}

public sealed class AddLineHandler : IStepHandler
{
    public string Kind => "add-line";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectBlockParam(p) is { } blockRejected) return blockRejected;
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (!HandlerHelpers.TryResolveAddCollection(s, p, out var target, out string spaceError))
            return new StepResult(false, spaceError, "");
        var line = new Line
        {
            StartPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x1"), HandlerHelpers.Req(p, "y1")),
            EndPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x2"), HandlerHelpers.Req(p, "y2")),
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        target!.Add(line);
        return new StepResult(true, "", line.Handle.ToString());
    }
}

public sealed class AddTextHandler : IStepHandler
{
    public string Kind => "add-text";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectBlockParam(p) is { } blockRejected) return blockRejected;
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (!HandlerHelpers.TryResolveAddCollection(s, p, out var target, out string spaceError))
            return new StepResult(false, spaceError, "");
        var text = new TextEntity
        {
            InsertPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y")),
            Value = p.GetProperty("text").GetString() ?? "",
            Height = HandlerHelpers.Req(p, "height"),
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        target!.Add(text);
        return new StepResult(true, "", text.Handle.ToString());
    }
}

/// <summary>add-circle（3j 生成式绘制补全）：params {x,y,radius,layer?,space?,layout?}。</summary>
public sealed class AddCircleHandler : IStepHandler
{
    public string Kind => "add-circle";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectBlockParam(p) is { } blockRejected) return blockRejected;
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        // 与 add-line/add-text 同一集合解析：默认模型；space:"paper"+layout → 布局纸面关联块。
        if (!HandlerHelpers.TryResolveAddCollection(s, p, out var target, out string spaceError))
            return new StepResult(false, spaceError, "");
        double radius = HandlerHelpers.Req(p, "radius");
        if (radius <= 0) return new StepResult(false, "radius 须为正数", "");
        var circle = new Circle
        {
            Center = (XYZ)new XY(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y")),
            Radius = radius,
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        target!.Add(circle);
        return new StepResult(true, "", circle.Handle.ToString());
    }
}
