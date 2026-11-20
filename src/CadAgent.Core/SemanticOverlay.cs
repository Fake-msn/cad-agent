using System.Text.Json;
using System.Text.Json.Serialization;

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
    public int Version { get; set; }

    public static SemanticOverlay Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<SemanticOverlay>(File.ReadAllText(path), JsonOpts) ?? new SemanticOverlay()
            : new SemanticOverlay();

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
