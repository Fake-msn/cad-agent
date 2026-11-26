using System.Text.Json;
using ACadSharp.Entities;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Data.Handlers;

/// <summary>
/// insert-block（2b-ii-b Task 9，第六处理器）：引用图纸内既有 BlockRecord 创建 Insert。
/// params {block, x, y, scale?, rotation?, space?, layout?}——scale 默认 1（三轴同值）、rotation 默认 0（弧度）。
/// 项2 Plan-2 T5：space:"paper"+layout → 写入该布局纸面关联块（与 add-line/add-text 同一集合解析）；
/// 默认模型不变。此 kind 的 block 就是被插入的块名（非 Plan-3 块定义定位），故不套 RejectBlockParam。
/// 块不存在→失败结果（跨文档符号导入留计划 3）。几何级处理器——不实现 ISemanticAware
/// （语义需求随计划 3 布灯处理器声明）。
/// ACadSharp 3.8.35（XML 文档 + 运行探针核实）：Insert(BlockRecord) 为唯一构造器，
/// InsertPoint/XScale/YScale/ZScale/Rotation 皆属性；CadDocument.BlockRecords.TryGetValue
/// 大小写不敏感（CAD 表名口径，与 ResolveLayer 先例一致）。
/// </summary>
public sealed class InsertBlockHandler : IStepHandler
{
    public string Kind => "insert-block";

    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (!HandlerHelpers.TryResolveAddCollection(s, p, out var target, out string spaceError))
            return new StepResult(false, spaceError, "");
        string blockName = p.GetProperty("block").GetString() ?? "";
        if (!s.Doc.BlockRecords.TryGetValue(blockName, out var block))
            return new StepResult(false, $"块不存在: {blockName}（跨文档符号导入留计划3）", "");

        double scale = p.TryGetProperty("scale", out var sv) ? sv.GetDouble() : 1;
        double rotation = p.TryGetProperty("rotation", out var rv) ? rv.GetDouble() : 0;
        var insert = new Insert(block)
        {
            InsertPoint = (XYZ)new XY(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y")),
            XScale = scale,
            YScale = scale,
            ZScale = scale,
            Rotation = rotation
        };
        target!.Add(insert);
        return new StepResult(true, "", insert.Handle.ToString());
    }
}
