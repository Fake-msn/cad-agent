using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Handlers;

namespace CadAgent.Data.Cad;

/// <summary>单步重放结果（3j）。</summary>
public sealed record ReplayStepResult(string Kind, bool Ok, string? Error);

/// <summary>
/// 3j 计划级 COM 重放（"agent 操作 CAD 绘制"第二执行通道）：步骤计划→活厂商会话绘制操作。
/// 几何数学与 handler 同源（GeometryKit）；影子副本（DrawingSession）仍为审计事实源，
/// 本通道逐 step 结果交 CLI 审计（com-replay/com-replay-failed），读回计数闸口守分歧。
/// 不支持的 kind（编辑类 delete/move/change-layer）响亮失败——第一 slice 仅生成类。
/// insert-symbol 经 skill 包解析+源 DXF 白名单基元→EnsureBlock+AddInsert。
/// </summary>
public sealed class ComReplayService
{
    private readonly ICadRuntime _rt;
    private readonly IReadOnlyList<SkillPackage> _packages;

    public ComReplayService(ICadRuntime rt, IReadOnlyList<SkillPackage>? packages = null)
    {
        _rt = rt;
        _packages = packages ?? Array.Empty<SkillPackage>();
    }

    public IReadOnlyList<ReplayStepResult> Replay(StepPlan plan, DrawingSession shadow)
    {
        var results = new List<ReplayStepResult>();
        foreach (var step in plan.Steps)
        {
            try
            {
                Map(step.Kind, step.Params, shadow);
                results.Add(new ReplayStepResult(step.Kind, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new ReplayStepResult(step.Kind, false, ex.Message));
                break;
            }
        }
        return results;
    }

    private void Map(string kind, JsonElement p, DrawingSession shadow)
    {
        switch (kind)
        {
            case "add-line":
                _rt.AddLine(HandlerHelpers.Req(p, "x1"), HandlerHelpers.Req(p, "y1"),
                    HandlerHelpers.Req(p, "x2"), HandlerHelpers.Req(p, "y2"),
                    HandlerHelpers.OptString(p, "layer"));
                break;
            case "add-text":
                _rt.AddText(HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y"),
                    p.GetProperty("text").GetString() ?? "", HandlerHelpers.Req(p, "height"),
                    HandlerHelpers.OptString(p, "layer"));
                break;
            case "insert-block":
                _rt.AddInsert(p.GetProperty("block").GetString() ?? "",
                    HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y"),
                    p.TryGetProperty("scale", out var sv) ? sv.GetDouble() : 1,
                    p.TryGetProperty("rotation", out var rv) ? rv.GetDouble() : 0,
                    HandlerHelpers.OptString(p, "layer"));
                break;
            case "layout-lighting":
            {
                double x = HandlerHelpers.Req(p, "x"), y = HandlerHelpers.Req(p, "y");
                double width = HandlerHelpers.Req(p, "width"), depth = HandlerHelpers.Req(p, "depth");
                double spacing = HandlerHelpers.Req(p, "spacing");
                if (spacing <= 0 || width <= 0 || depth <= 0)
                    throw new CadRuntimeException("spacing/width/depth 须为正数");
                string block = p.GetProperty("block").GetString() ?? "";
                string? layer = HandlerHelpers.OptString(p, "layer");
                foreach (var (px, py) in GeometryKit.GridPoints(x, y, width, depth, spacing))
                    _rt.AddInsert(block, px, py, 1, 0, layer);
                break;
            }
            case "label-luminaires":
            {
                string prefix = p.TryGetProperty("prefix", out var pv) ? pv.GetString() ?? "" : "";
                if (prefix.Length == 0) throw new CadRuntimeException("prefix（品种代号）必填");
                double size = p.TryGetProperty("symbolSize", out var szv) ? szv.GetDouble() : 10;
                if (size < 8 || size > 12) throw new CadRuntimeException("符号长轴须在 8~12mm");
                int start = p.TryGetProperty("start", out var iv) ? iv.GetInt32() : 1;
                string? blockFilter = HandlerHelpers.OptString(p, "block");
                string? layer = HandlerHelpers.OptString(p, "layer");
                var inserts = shadow.Doc.Entities.OfType<Insert>()
                    .Where(i => blockFilter == null
                        || string.Equals(i.Block?.Name, blockFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Handle)
                    .ToList();
                if (inserts.Count == 0) throw new CadRuntimeException("无灯具 Insert 可编号");
                int n = start;
                foreach (var insert in inserts)
                {
                    double cx = insert.InsertPoint.X + size, cy = insert.InsertPoint.Y + size;
                    foreach (var (x1, y1, x2, y2) in GeometryKit.HexagonLines(cx, cy, size))
                        _rt.AddLine(x1, y1, x2, y2, layer);
                    string label = $"{prefix}{n}";
                    var (tx, ty, th) = GeometryKit.LabelPlacement(cx, cy, size, label);
                    _rt.AddText(tx, ty, label, th, layer);
                    n++;
                }
                break;
            }
            case "insert-symbol":
            {
                string name = p.GetProperty("symbol").GetString() ?? "";
                SymbolMeta? meta = null;
                SkillPackage? pkg = null;
                foreach (var pk in _packages)
                {
                    meta = pk.Symbols.FirstOrDefault(m =>
                        string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (meta != null) { pkg = pk; break; }
                }
                if (meta == null || pkg == null)
                    throw new CadRuntimeException($"符号不存在于 skill 包: {name}");
                string path = Path.Combine(pkg.SourceDir, "symbols", meta.File);
                if (!File.Exists(path)) throw new CadRuntimeException($"符号文件缺失: {path}");
                CadDocument src;
                using (var fs = File.OpenRead(path)) src = new DxfReader(fs).Read();
                var primitives = new List<BlockPrimitive>();
                foreach (var e in src.Entities)
                {
                    switch (e)
                    {
                        case Line l: primitives.Add(new BlockLine(l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y)); break;
                        case Circle c: primitives.Add(new BlockCircle(c.Center.X, c.Center.Y, c.Radius)); break;
                        case TextEntity t: primitives.Add(new BlockText(t.InsertPoint.X, t.InsertPoint.Y, t.Value ?? "", t.Height)); break;
                        default: throw new CadRuntimeException($"不支持的实体类型 {e.GetType().Name}");
                    }
                }
                _rt.EnsureBlock(meta.Name, primitives);
                _rt.AddInsert(meta.Name, HandlerHelpers.Req(p, "x"), HandlerHelpers.Req(p, "y"),
                    p.TryGetProperty("scale", out var scv) ? scv.GetDouble() : 1,
                    p.TryGetProperty("rotation", out var rotv) ? rotv.GetDouble() : 0,
                    HandlerHelpers.OptString(p, "layer"));
                break;
            }
            default:
                throw new CadRuntimeException($"COM 重放不支持步骤类型 {kind}（第一 slice 仅生成类）");
        }
    }
}
