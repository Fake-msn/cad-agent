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
                // Preserve Z elevation: real drawings carry elevations; XY cast would flatten them.
                line.StartPoint = new XYZ(line.StartPoint.X + dx, line.StartPoint.Y + dy, line.StartPoint.Z);
                line.EndPoint = new XYZ(line.EndPoint.X + dx, line.EndPoint.Y + dy, line.EndPoint.Z);
                break;
            case TextEntity text:
                text.InsertPoint = new XYZ(text.InsertPoint.X + dx, text.InsertPoint.Y + dy, text.InsertPoint.Z);
                break;
            // Arc : Circle（ACadSharp 3.8.35 继承）——Arc 分支必须先于 Circle，否则不可达。
            case Arc arc:
                // Z 高程保持（同 Line 先例）：真实图纸带标高，XY 压平会丢高程。
                arc.Center = new XYZ(arc.Center.X + dx, arc.Center.Y + dy, arc.Center.Z);
                break;
            case Circle circle:
                circle.Center = new XYZ(circle.Center.X + dx, circle.Center.Y + dy, circle.Center.Z);
                break;
            case MText mtext:
                mtext.InsertPoint = new XYZ(mtext.InsertPoint.X + dx, mtext.InsertPoint.Y + dy, mtext.InsertPoint.Z);
                break;
            case Insert insert:
                insert.InsertPoint = new XYZ(insert.InsertPoint.X + dx, insert.InsertPoint.Y + dy, insert.InsertPoint.Z);
                break;
            case LwPolyline lw:
                // 顶点为 XY；Z 高程经 Elevation 承载，平移不动它（保持）。
                foreach (LwPolyline.Vertex v in lw.Vertices)
                    v.Location = new XY(v.Location.X + dx, v.Location.Y + dy);
                break;
            case Polyline2D p2:
                foreach (Vertex2D v in p2.Vertices)
                    v.Location = new XYZ(v.Location.X + dx, v.Location.Y + dy, v.Location.Z);
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
        new DeleteEntityHandler(), new MoveEntityHandler(), new ChangeLayerHandler(),
        new InsertBlockHandler(), new LayoutLightingHandler(), new LabelLuminairesHandler(),
        new AddCircleHandler()
    };
}
