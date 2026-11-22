using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Handlers;

/// <summary>
/// label-luminaires（3e，第八处理器）：灯具设备索引符号+编号。
/// JGJ/T 244-2011 3.6.5/3.6.6-4：正六边形+水平内径线、细实线（0.25mm）、
/// 长轴 8~12mm、内注设备编号及品种代号。品种代号标准明示归院标（3.6.5 条文说明）
/// →prefix 必填无默认。编号确定性：Insert 按 Handle 升序，prefix+序号。
/// ISemanticAware 声明 Lighting（Lighting 类首次实战）：无已确认灯具语义拒执行。
/// 文本近似居中（确定性估算，精确排版留视图层）；字高≥2.5mm（50001 字母数字底线）。
/// </summary>
public sealed class LabelLuminairesHandler : IStepHandler, ISemanticAware
{
    public string Kind => "label-luminaires";
    public IReadOnlyList<SemanticClass> RequiredSemantics => new[] { SemanticClass.Lighting };

    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        string prefix = p.TryGetProperty("prefix", out var pv) ? pv.GetString() ?? "" : "";
        if (prefix.Length == 0) return new StepResult(false, "prefix（品种代号）必填——归院标 skill，无默认", "");
        double size = p.TryGetProperty("symbolSize", out var sv) ? sv.GetDouble() : 10;
        if (size < 8 || size > 12)
            return new StepResult(false, "符号长轴须在 8~12mm（JGJ/T 244-2011 3.6.6-4）", "");
        int start = p.TryGetProperty("start", out var iv) ? iv.GetInt32() : 1;
        string? blockFilter = p.TryGetProperty("block", out var bv) ? bv.GetString() : null;
        string? layerName = p.TryGetProperty("layer", out var lv) ? lv.GetString() : null;
        // R10：params 含 sheetId（string）→ 候选 Insert 限世界插入点归属 declared sheet
        // （防 plan 声明 S1 却给 S2 灯具编号）；无 sheetId → 全图 legacy 行为逐字保持。
        string? sheetId = HandlerHelpers.OptString(p, "sheetId");

        var candidates = s.Doc.Entities.OfType<Insert>()
            .Where(i => blockFilter == null
                || string.Equals(i.Block?.Name, blockFilter, StringComparison.OrdinalIgnoreCase));
        if (sheetId != null)
        {
            var sheets = s.Sheets; // 一次捕获，避免逐 Insert 重算指纹
            candidates = candidates.Where(i =>
                SheetRegistryBuilder.SheetIdForPoint(sheets, i.InsertPoint.X, i.InsertPoint.Y) == sheetId);
        }
        var inserts = candidates.OrderBy(i => i.Handle).ToList();
        if (inserts.Count == 0) return new StepResult(false, "无灯具 Insert 可编号", "");

        double height = System.Math.Max(2.5, size / 4);
        string firstHandle = "";
        int n = start;
        foreach (var insert in inserts)
        {
            Layer layer = layerName != null
                ? HandlerHelpers.ResolveLayer(s, layerName)
                : insert.Layer ?? s.Doc.Layers.First(l => l.Name == "0");
            double cx = insert.InsertPoint.X + size, cy = insert.InsertPoint.Y + size;
            foreach (var (x1, y1, x2, y2) in GeometryKit.HexagonLines(cx, cy, size))
                s.Doc.Entities.Add(new Line
                {
                    StartPoint = new XYZ(x1, y1, 0),
                    EndPoint = new XYZ(x2, y2, 0),
                    Layer = layer,
                    LineWeight = LineWeightType.W25
                });
            string label = $"{prefix}{n}";
            var (tx, ty, th) = GeometryKit.LabelPlacement(cx, cy, size, label);
            var text = new TextEntity
            {
                Value = label,
                Height = th,
                InsertPoint = new XYZ(tx, ty, 0),
                Layer = layer
            };
            s.Doc.Entities.Add(text);
            if (firstHandle == "") firstHandle = text.Handle.ToString();
            n++;
        }
        return new StepResult(true, "", firstHandle);
    }
}
