using System.Text.Json;
using ACadSharp.Entities;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Data.Handlers;

public sealed class DeleteEntityHandler : IStepHandler
{
    public string Kind => "delete-entity";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var entity = HandlerHelpers.Find(s, p);
        if (entity == null) return new StepResult(false, "句柄不存在", "");
        string handle = entity.Handle.ToString();
        s.Doc.Entities.Remove(entity);
        return new StepResult(true, "", handle);
    }
}

public sealed class MoveEntityHandler : IStepHandler
{
    public string Kind => "move-entity";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var entity = HandlerHelpers.Find(s, p);
        if (entity == null) return new StepResult(false, "句柄不存在", "");
        double dx = HandlerHelpers.Req(p, "dx"), dy = HandlerHelpers.Req(p, "dy");
        switch (entity)
        {
            case Line line:
                line.StartPoint = (XYZ)new XY(line.StartPoint.X + dx, line.StartPoint.Y + dy);
                line.EndPoint = (XYZ)new XY(line.EndPoint.X + dx, line.EndPoint.Y + dy);
                break;
            case TextEntity text:
                text.InsertPoint = (XYZ)new XY(text.InsertPoint.X + dx, text.InsertPoint.Y + dy);
                break;
            default:
                return new StepResult(false, $"move 暂不支持实体类型 {entity.GetType().Name}", "");
        }
        return new StepResult(true, "", entity.Handle.ToString());
    }
}

public sealed class ChangeLayerHandler : IStepHandler
{
    public string Kind => "change-layer";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        var entity = HandlerHelpers.Find(s, p);
        if (entity == null) return new StepResult(false, "句柄不存在", "");
        entity.Layer = HandlerHelpers.ResolveLayer(s, p.GetProperty("layer").GetString());
        return new StepResult(true, "", entity.Handle.ToString());
    }
}

public static class HandlerRegistry
{
    public static IReadOnlyList<IStepHandler> All() => new IStepHandler[]
    {
        new AddLineHandler(), new AddTextHandler(),
        new DeleteEntityHandler(), new MoveEntityHandler(), new ChangeLayerHandler()
    };
}