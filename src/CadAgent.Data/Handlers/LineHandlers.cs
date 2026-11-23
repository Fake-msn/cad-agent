using System.Text.Json;
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

    public static Entity? Find(DrawingSession s, JsonElement p)
    {
        // handle 契约（计划 3a）：JSON 数字或十进制字符串皆可（Summarize 暴露字符串，
        // LLM 计划编写层可直接回灌）；非法形态→null→"句柄不存在"优雅失败。
        var hEl = p.GetProperty("handle");
        ulong handle;
        switch (hEl.ValueKind)
        {
            case JsonValueKind.Number:
                handle = hEl.GetUInt64();
                break;
            case JsonValueKind.String:
                if (!ulong.TryParse(hEl.GetString(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out handle))
                    return null;
                break;
            default:
                return null;
        }
        return s.Doc.Entities.FirstOrDefault(e => e.Handle == handle);
    }
}

public sealed class AddLineHandler : IStepHandler
{
    public string Kind => "add-line";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var line = new Line
        {
            StartPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x1"), HandlerHelpers.Req(p, "y1")),
            EndPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x2"), HandlerHelpers.Req(p, "y2")),
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        s.Doc.Entities.Add(line);
        return new StepResult(true, "", line.Handle.ToString());
    }
}

public sealed class AddTextHandler : IStepHandler
{
    public string Kind => "add-text";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var text = new TextEntity
        {
            InsertPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y")),
            Value = p.GetProperty("text").GetString() ?? "",
            Height = HandlerHelpers.Req(p, "height"),
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        s.Doc.Entities.Add(text);
        return new StepResult(true, "", text.Handle.ToString());
    }
}

/// <summary>add-circle（3j 生成式绘制补全）：params {x,y,radius,layer?}。</summary>
public sealed class AddCircleHandler : IStepHandler
{
    public string Kind => "add-circle";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        double radius = HandlerHelpers.Req(p, "radius");
        if (radius <= 0) return new StepResult(false, "radius 须为正数", "");
        var circle = new Circle
        {
            Center = (XYZ)new XY(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y")),
            Radius = radius,
            Layer = HandlerHelpers.ResolveLayer(s, HandlerHelpers.OptString(p, "layer"))
        };
        s.Doc.Entities.Add(circle);
        return new StepResult(true, "", circle.Handle.ToString());
    }
}
