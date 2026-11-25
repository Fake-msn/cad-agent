using System.Text.Json;
using System.Text.Json.Serialization;
using CadAgent.Core.Cad;

namespace CadAgent.Core;

public sealed class SemanticOverlay
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<SemanticAnnotation> Annotations { get; set; } = new();
    public List<LayerMapping> Mappings { get; set; } = new();

    // 切片A（spec §3）：COM 原生读参独立表——与 Annotations 并列，语义分类与原生参数分离。
    // JSON 加性：旧 overlay 无此字段→反序列化 null→Load 归一为空 List（消费端免判空）。
    public List<NativeEntityParam> NativeEntities { get; set; } = new();

    // 切片B（spec §3）：深度炸开取证独立表——与 NativeEntities 并列，按句柄一键。
    // JSON 加性：旧 overlay 无此字段→反序列化 null→Load 归一为空 List（消费端免判空）。
    public List<ExplodeEvidence> ExplodeEvidence { get; set; } = new();

    public int Version { get; set; }

    public static SemanticOverlay Load(string path)
    {
        var overlay = File.Exists(path)
            ? JsonSerializer.Deserialize<SemanticOverlay>(File.ReadAllText(path), JsonOpts) ?? new SemanticOverlay()
            : new SemanticOverlay();
        // JSON 显式 null 亦归一为空表（属性初始化仅覆盖字段缺失场景）。
        overlay.NativeEntities ??= new();
        overlay.ExplodeEvidence ??= new();
        return overlay;
    }

    public void Save(string path)
    {
        // 原子写（最终审查裁定：快照只覆盖图纸，semantic.json 须自保）：
        // 先写同目录临时文件再 Move 覆盖——写入中途崩溃时磁盘旧文件完整，
        // 人工确认不丢。失败时 tmp 可能残留（可接受）。Version++ 语义不变。
        Version++;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string json = JsonSerializer.Serialize(this, JsonOpts);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public void UpsertAnnotation(SemanticAnnotation a)
    {
        int i = Annotations.FindIndex(x => x.TargetHandle == a.TargetHandle && x.Provenance == a.Provenance);
        if (i >= 0) Annotations[i] = a; else Annotations.Add(a);
    }

    // 切片A：按句柄覆盖（同句柄再读参时整条替换，ReadAt/Params 随之更新）。
    public void UpsertNative(NativeEntityParam p)
    {
        int i = NativeEntities.FindIndex(x => x.Handle == p.Handle);
        if (i >= 0) NativeEntities[i] = p; else NativeEntities.Add(p);
    }

    // 切片B：按句柄覆盖（同句柄再取证时整条替换，ReadAt/基元/XData 随之更新）。
    // M6（复审修复波）：句柄比较 OrdinalIgnoreCase——句柄是 hex 标识（AutoCAD 原样大写，
    // 计划/手写来源可能小写），大小写不同即同一实体；Ordinal 曾把 "21e00" 与 "21E00" 存成两行。
    public void UpsertExplode(ExplodeEvidence e)
    {
        int i = ExplodeEvidence.FindIndex(x => string.Equals(x.Handle, e.Handle, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) ExplodeEvidence[i] = e; else ExplodeEvidence.Add(e);
    }

    public void ConfirmMapping(LayerMapping m)
    {
        int i = Mappings.FindIndex(x => string.Equals(x.LayerName, m.LayerName, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Mappings[i] = m; else Mappings.Add(m);
    }

    public void ApplyMappingToAnnotations(string layerName, SemanticClass cls, IEnumerable<string> handlesOnLayer)
    {
        foreach (string h in handlesOnLayer)
        {
            Annotations.RemoveAll(x => x.TargetHandle == h);
            UpsertAnnotation(new SemanticAnnotation(h, cls, Provenance.HumanConfirmed, 1.0, $"人工确认图层映射: {layerName}"));
        }
    }
}
