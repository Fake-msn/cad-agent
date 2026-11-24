namespace CadAgent.Core.Cad;

/// <summary>一实体一次 COM 原生读参结果（spec 切片A §3）。Core 零 COM——纯数据。
/// Layer 末位可选（change-layer 读回校验需要；COM 标准 Layer 属性对 TDb 可读）。</summary>
public sealed record NativeEntityParam(
    string Handle,
    string ObjectName,
    SemanticClass Class,
    IReadOnlyDictionary<string, string> Params,
    double[]? Bbox,
    DateTimeOffset ReadAt,
    bool ReadOk,
    string? Layer = null);
