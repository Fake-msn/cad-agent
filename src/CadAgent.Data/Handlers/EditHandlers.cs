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
                // Line 端点为 WCS 坐标（无法向），dx/dy 直施。
                line.StartPoint = new XYZ(line.StartPoint.X + dx, line.StartPoint.Y + dy, line.StartPoint.Z);
                line.EndPoint = new XYZ(line.EndPoint.X + dx, line.EndPoint.Y + dy, line.EndPoint.Z);
                break;
            case TextEntity text:
            {
                // OCS 原值坐标：WCS dx/dy 须逆投影到 OCS（见 OcsDelta 注释）。
                var (odx, ody) = OcsDelta(text.Normal, dx, dy);
                text.InsertPoint = new XYZ(text.InsertPoint.X + odx, text.InsertPoint.Y + ody, text.InsertPoint.Z);
                break;
            }
            // Arc : Circle（ACadSharp 3.8.35 继承）——Arc 分支必须先于 Circle，否则不可达。
            case Arc arc:
            {
                // Z 高程保持（同 Line 先例）：真实图纸带标高，XY 压平会丢高程。
                var (odx, ody) = OcsDelta(arc.Normal, dx, dy);
                arc.Center = new XYZ(arc.Center.X + odx, arc.Center.Y + ody, arc.Center.Z);
                break;
            }
            case Circle circle:
            {
                var (odx, ody) = OcsDelta(circle.Normal, dx, dy);
                circle.Center = new XYZ(circle.Center.X + odx, circle.Center.Y + ody, circle.Center.Z);
                break;
            }
            case MText mtext:
            {
                var (odx, ody) = OcsDelta(mtext.Normal, dx, dy);
                mtext.InsertPoint = new XYZ(mtext.InsertPoint.X + odx, mtext.InsertPoint.Y + ody, mtext.InsertPoint.Z);
                break;
            }
            case Insert insert:
            {
                var (odx, ody) = OcsDelta(insert.Normal, dx, dy);
                insert.InsertPoint = new XYZ(insert.InsertPoint.X + odx, insert.InsertPoint.Y + ody, insert.InsertPoint.Z);
                break;
            }
            case LwPolyline lw:
            {
                // 顶点为 OCS XY；Z 高程经 Elevation 承载，平移不动它（保持）。
                var (odx, ody) = OcsDelta(lw.Normal, dx, dy);
                foreach (LwPolyline.Vertex v in lw.Vertices)
                    v.Location = new XY(v.Location.X + odx, v.Location.Y + ody);
                break;
            }
            case Polyline2D p2:
            {
                var (odx, ody) = OcsDelta(p2.Normal, dx, dy);
                foreach (Vertex2D v in p2.Vertices)
                    v.Location = new XYZ(v.Location.X + odx, v.Location.Y + ody, v.Location.Z);
                break;
            }
            default:
                return new StepResult(false, $"move 暂不支持实体类型 {entity.GetType().Name}", "");
        }
        return new StepResult(true, "", entity.Handle.ToString());
    }

    /// <summary>
    /// WCS 平移增量 (dx,dy) → OCS 增量 (odx,ody)（AutoCAD 任意轴算法逆映射）。
    /// Circle/Arc/MText/Insert/LwPolyline/Polyline2D/TextEntity 的坐标为 OCS 原值
    /// （ACadSharp 3.8.35 不转换，DXF group 10/11 即 OCS）——WCS dx/dy 直施会致
    /// 镜像法向实体实际位移 -dx（最终审查 Imp#1）。算法与 View RenderModel.OcsMapper
    /// 同款（注释互指，防双处漂移）：法向接近 +Z → identity 快路径；否则
    /// Ax = (|Nx|,|Ny| 均 &lt; 1/64 ? (0,1,0)×N : (0,0,1)×N) 归一，Ay = N×Ax 归一。
    /// 镜像法向 N=(0,0,-1)：Ax=(-1,0,0), Ay=(0,1,0) → odx=-dx, ody=dy（精确逆映射）。
    /// **倾斜法向边界**：WCS delta 投影到 OCS XY 轴取面内分量（odx=Ax·d, ody=Ay·d），
    /// 丢弃法向分量——为面内投影近似（与渲染侧倾斜投影近似同口径，spec §12 明示）。
    /// </summary>
    private static (double Odx, double Ody) OcsDelta(XYZ normal, double dx, double dy)
    {
        XYZ n = Normalize(normal);
        if (Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64 && n.Z > 0)
            return (dx, dy); // 默认法向：OCS≡WCS XY，行为保持
        XYZ ax = Normalize(Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64
            ? Cross(new XYZ(0, 1, 0), n) : Cross(new XYZ(0, 0, 1), n));
        XYZ ay = Normalize(Cross(n, ax));
        // 正交归一 Ax/Ay 的逆映射 = 转置：OCS 分量 = WCS delta 在各轴上的投影。
        return (ax.X * dx + ax.Y * dy, ay.X * dx + ay.Y * dy);
    }

    private static XYZ Normalize(XYZ v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-12 ? new XYZ(0, 0, 1) : new XYZ(v.X / len, v.Y / len, v.Z / len);
    }

    private static XYZ Cross(XYZ a, XYZ b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
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
