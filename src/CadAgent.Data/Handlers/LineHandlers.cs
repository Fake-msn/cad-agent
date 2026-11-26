using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Data.Handlers;

internal static class HandlerHelpers
{
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

    /// <summary>块内编辑属 Plan-3：含 block 参数一律拒绝（fail-closed，防静默落到模型顶层）。</summary>
    public static StepResult? RejectBlockParam(JsonElement p) =>
        p.TryGetProperty("block", out var v) && v.ValueKind != JsonValueKind.Null
            ? new StepResult(false, EditTargetResolver.BlockDeferredMessage, "")
            : null;

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
            error = "space:\"paper\" 需指定 layout";
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
