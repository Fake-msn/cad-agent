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
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (HandlerHelpers.RejectInvalidTargetScope(s, p) is { } scopeRejected) return scopeRejected;
        var target = HandlerHelpers.ResolveEditTarget(s, p, out string targetError);
        if (target == null)
            return new StepResult(false, targetError, "");
        if (target.Entity is Viewport) return new StepResult(false, "视口对象不支持编辑", "");
        // 移除后 ACadSharp 会清空句柄：审计/返回的目标句柄必须先取。
        string handle = target.Entity.Handle.ToString();
        // 按所有者集合移除：纸面实体不在 Doc.Entities 中，删错集合会静默无效。
        switch (target.Owner)
        {
            case EditOwnerKind.Model:
                if (!s.Doc.Entities.Remove(target.Entity))
                    return new StepResult(false, "实体移除失败", "");
                break;
            case EditOwnerKind.Paper:
            {
                var layout = EditTargetResolver.FindLayout(s, target.LayoutName);
                if (layout?.AssociatedBlock is null)
                    return new StepResult(false, $"布局不存在：{target.LayoutName}", "");
                if (!layout.AssociatedBlock.Entities.Remove(target.Entity))
                    return new StepResult(false, "实体移除失败", "");
                break;
            }
            case EditOwnerKind.BlockDefinition:
            {
                // 块名再查（同纸面布局模式）：解析后块表被改（删除/改名）→ fail-closed。
                var block = target.BlockName is null
                    ? null
                    : EditTargetResolver.FindBlock(s, target.BlockName);
                if (block is null)
                    return new StepResult(false, $"块定义不存在：{target.BlockName}", "");
                // 属性定义是实例属性模板（删除会毁全实例属性结构），定义层保护。
                if (target.Entity is AttributeDefinition)
                    return new StepResult(false, "属性定义不可删除", "");
                if (!block.Entities.Remove(target.Entity))
                    return new StepResult(false, "实体移除失败", "");
                break;
            }
            case EditOwnerKind.InsertAttribute:
                // ATTRIB 实例：Insert.Attributes（SeqendCollection<AttributeEntity>）的 Remove(T)
                // 实件存在（ACadSharp 3.8.35 反射核验），无需重建集合。
                // 显式分支而非 default：未来新增枚举成员不得落到这里的强制转换（InvalidCastException）。
                if (target.Entity is not AttributeEntity attribute || target.AttributeOwner is null)
                    return new StepResult(false, "属性所属插入缺失", "");
                if (!target.AttributeOwner.Attributes.Remove(attribute))
                    return new StepResult(false, "实体移除失败", "");
                break;
            default:
                return new StepResult(false, $"delete 暂不支持所有者 {target.Owner}", "");
        }
        return new StepResult(true, "", handle);
    }
}

public sealed class MoveEntityHandler : IStepHandler
{
    public string Kind => "move-entity";
    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (HandlerHelpers.RejectInvalidTargetScope(s, p) is { } scopeRejected) return scopeRejected;
        var target = HandlerHelpers.ResolveEditTarget(s, p, out string targetError);
        if (target == null)
            return new StepResult(false, targetError, "");
        if (target.Entity is Viewport) return new StepResult(false, "视口对象不支持编辑", "");
        Entity entity = target.Entity;
        double dx = HandlerHelpers.Req(p, "dx"), dy = HandlerHelpers.Req(p, "dy");
        switch (entity)
        {
            case Line line:
                // Preserve Z elevation: real drawings carry elevations; XY cast would flatten them.
                // Line 端点为 WCS 坐标（无法向），dx/dy 直施。
                line.StartPoint = new XYZ(line.StartPoint.X + dx, line.StartPoint.Y + dy, line.StartPoint.Z);
                line.EndPoint = new XYZ(line.EndPoint.X + dx, line.EndPoint.Y + dy, line.EndPoint.Z);
                break;
            // ATTRIB 须先于 TextEntity 分支（AttributeBase : TextEntity，否则属性走纯文本分支）。
            // 渲染锚点按对齐方式二选一（InsertPoint/AlignmentPoint，RenderModel.EmitAttribute）：
            // 两点同施增量，否则非默认对齐的属性移动后文字不回位。
            case AttributeEntity attr:
            {
                var (odx, ody) = OcsDelta(attr.Normal, dx, dy);
                attr.InsertPoint = new XYZ(attr.InsertPoint.X + odx, attr.InsertPoint.Y + ody, attr.InsertPoint.Z);
                attr.AlignmentPoint = new XYZ(attr.AlignmentPoint.X + odx, attr.AlignmentPoint.Y + ody, attr.AlignmentPoint.Z);
                break;
            }
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
                // ATTRIB 坐标为"已放置"坐标（父 INSERT 的 OCS 空间，项2 F 据此渲染）：
                // 父移动而属性不动会在图面留下孤悬文字——同施父增量（Z 高程保持）。
                foreach (AttributeEntity attr in insert.Attributes)
                {
                    attr.InsertPoint = new XYZ(attr.InsertPoint.X + odx, attr.InsertPoint.Y + ody, attr.InsertPoint.Z);
                    attr.AlignmentPoint = new XYZ(attr.AlignmentPoint.X + odx, attr.AlignmentPoint.Y + ody, attr.AlignmentPoint.Z);
                }
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
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (HandlerHelpers.RejectInvalidTargetScope(s, p) is { } scopeRejected) return scopeRejected;
        var target = HandlerHelpers.ResolveEditTarget(s, p, out string targetError);
        if (target == null)
            return new StepResult(false, targetError, "");
        if (target.Entity is Viewport) return new StepResult(false, "视口对象不支持编辑", "");
        target.Entity.Layer = HandlerHelpers.ResolveLayer(s, p.GetProperty("layer").GetString());
        return new StepResult(true, "", target.Entity.Handle.ToString());
    }
}

public static class HandlerRegistry
{
    public static IReadOnlyList<IStepHandler> All() => new IStepHandler[]
    {
        new AddLineHandler(), new AddTextHandler(),
        new DeleteEntityHandler(), new MoveEntityHandler(), new ChangeLayerHandler(),
        new SetTextHandler(), new SetAttributeHandler(),
        new InsertBlockHandler(), new LayoutLightingHandler(), new LabelLuminairesHandler(),
        new AddCircleHandler()
    };
}
