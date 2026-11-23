using System.Text.Json.Serialization;

namespace CadAgent.Data;

public sealed record EntityBrief(string Handle, string Kind, string Layer);
public sealed record UnknownBrief(string Handle, string ObjectName, string CppClassName, string ApplicationName);
public sealed record DrawingSummary(
    string SourcePath,
    string Version,
    IReadOnlyList<string> Layers,
    IReadOnlyList<EntityBrief> Entities,
    IReadOnlyList<UnknownBrief> Unknowns,
    // 通知侧 census briefs（防假 Green）：与图内 Unknowns 分离；null=旧口径（无 census 信息）。
    // WhenWritingNull：null 时整个字段省略（不改全局 JsonOpts）——无 TCH 图纸 intake stdout 逐字不变。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<UnknownBrief>? Unparsed = null);
