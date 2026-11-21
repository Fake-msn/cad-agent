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
    public static string? OptString(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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