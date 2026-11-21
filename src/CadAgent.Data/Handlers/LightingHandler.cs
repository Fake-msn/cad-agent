using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Data.Handlers;

/// <summary>
/// layout-lighting（3d，第七处理器）：房间内确定性网格布灯。
/// params {x,y,width,depth,spacing,block,layer?}——点位从 (x+spacing/2, y+spacing/2)
/// 起步、步长 spacing、不越区域边界；每点 Insert(block)（块不存在→失败）。
/// ISemanticAware 声明 Room：无已确认房间语义时闸口拒执行（操作级，无 handle 参数）。
/// 点位数上限 10000（防脏参数爆炸）；照度计算不做（GB 50034 未提供，spec §12）。
/// </summary>
public sealed class LayoutLightingHandler : IStepHandler, ISemanticAware
{
    private const int MaxPoints = 10000;

    public string Kind => "layout-lighting";
    public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Room };

    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        string blockName = p.GetProperty("block").GetString() ?? "";
        if (!s.Doc.BlockRecords.TryGetValue(blockName, out var block))
            return new StepResult(false, $"块不存在: {blockName}（跨文档符号导入留后续）", "");

        double x = HandlerHelpers.Req(p, "x"), y = HandlerHelpers.Req(p, "y");
        double width = HandlerHelpers.Req(p, "width"), depth = HandlerHelpers.Req(p, "depth");
        double spacing = HandlerHelpers.Req(p, "spacing");
        if (spacing <= 0 || width <= 0 || depth <= 0)
            return new StepResult(false, "spacing/width/depth 须为正数", "");

        int cols = 0, rows = 0;
        for (double px = x + spacing / 2; px < x + width; px += spacing) cols++;
        for (double py = y + spacing / 2; py < y + depth; py += spacing) rows++;
        if (cols == 0 || rows == 0)
            return new StepResult(false, "区域小于间距，无可用点位", "");
        if ((long)cols * rows > MaxPoints)
            return new StepResult(false, $"点位数量超限（{cols}×{rows} > {MaxPoints}），请增大 spacing", "");

        Layer? layer = p.TryGetProperty("layer", out var lv)
            ? HandlerHelpers.ResolveLayer(s, lv.GetString()!)
            : null;
        string firstHandle = "";
        for (double px = x + spacing / 2; px < x + width; px += spacing)
        {
            for (double py = y + spacing / 2; py < y + depth; py += spacing)
            {
                var insert = new Insert(block) { InsertPoint = (XYZ)new XY(px, py) };
                if (layer != null) insert.Layer = layer;
                s.Doc.Entities.Add(insert);
                if (firstHandle == "") firstHandle = insert.Handle.ToString();
            }
        }
        return new StepResult(true, "", firstHandle);
    }
}
