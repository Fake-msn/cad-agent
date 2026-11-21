using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Data.Handlers;

/// <summary>
/// insert-symbol（3h，第九处理器）：跨文档符号导入——skill 包 symbols.yaml 按名解析
/// （OrdinalIgnoreCase，首包命中；**不接受任意路径参数**，边界受控）→源 DXF 读取→
/// 白名单重建克隆（Line/Circle/Arc/TextEntity；库内容即此四类）→建块（块名=符号名，
/// 已存在则复用，幂等）→Insert(scale/rotation/layer)。
/// 不支持类型/缺文件/未知符号→响亮失败（先校验后变更，不半改）。
/// 几何导入不声明 ISemanticAware：语义确认仍走 scan/map；执行经 StepEngine 审计/快照管线。
/// 诚实边界：重建克隆非 Entity.Clone（外来 Layer/LineType 表引用风险不赌）；
/// 四类之外类型（MText/LwPolyline/Hatch 等）当前拒收，随符号库扩展再扩白名单。
/// </summary>
public sealed class ImportSymbolHandler : IStepHandler
{
    private readonly IReadOnlyList<SkillPackage> _packages;

    public ImportSymbolHandler(IReadOnlyList<SkillPackage> packages) => _packages = packages;

    public string Kind => "insert-symbol";

    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
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
            return new StepResult(false, $"符号不存在于 skill 包: {name}", "");
        string path = Path.Combine(pkg.SourceDir, "symbols", meta.File);
        if (!File.Exists(path))
            return new StepResult(false, $"符号文件缺失: {path}", "");

        CadDocument src;
        using (var fs = File.OpenRead(path)) src = new DxfReader(fs).Read();
        foreach (var e in src.Entities)
            if (e is not (Line or Circle or Arc or TextEntity))
                return new StepResult(false,
                    $"不支持的实体类型 {e.GetType().Name}（符号导入当前范围: Line/Circle/Arc/Text）", "");

        if (!s.Doc.BlockRecords.TryGetValue(meta.Name, out var block))
        {
            block = new BlockRecord(meta.Name);
            foreach (var e in src.Entities) block.Entities.Add(Rebuild(e));
            s.Doc.BlockRecords.Add(block);
        }

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
        if (p.TryGetProperty("layer", out var lv))
            insert.Layer = HandlerHelpers.ResolveLayer(s, lv.GetString());
        s.Doc.Entities.Add(insert);
        return new StepResult(true, "", insert.Handle.ToString());
    }

    private static Entity Rebuild(Entity e) => e switch
    {
        Line l => new Line { StartPoint = l.StartPoint, EndPoint = l.EndPoint },
        Arc a => new Arc { Center = a.Center, Radius = a.Radius, StartAngle = a.StartAngle, EndAngle = a.EndAngle },
        Circle c => new Circle { Center = c.Center, Radius = c.Radius },
        TextEntity t => new TextEntity { Value = t.Value, Height = t.Height, InsertPoint = t.InsertPoint, Rotation = t.Rotation },
        _ => throw new InvalidOperationException($"不支持的实体类型 {e.GetType().Name}")
    };
}
