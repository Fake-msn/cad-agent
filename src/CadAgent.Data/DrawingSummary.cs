namespace CadAgent.Data;

public sealed record EntityBrief(string Handle, string Kind, string Layer);
public sealed record UnknownBrief(string Handle, string ObjectName, string CppClassName, string ApplicationName);
public sealed record DrawingSummary(
    string SourcePath,
    string Version,
    IReadOnlyList<string> Layers,
    IReadOnlyList<EntityBrief> Entities,
    IReadOnlyList<UnknownBrief> Unknowns);