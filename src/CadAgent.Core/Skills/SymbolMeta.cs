namespace CadAgent.Core;

/// <summary>
/// 符号库元数据（symbols/symbols.yaml，2b-ii-b Task 9）：名称、DXF 文件路径（包内相对）、
/// 语义类别、描述。第一 slice 仅元数据校验（name/file 非空、class 枚举名 ignoreCase、
/// 包内 name OrdinalIgnoreCase 唯一）；文件存在性不强制——跨文档符号导入留计划 3。
/// </summary>
public sealed record SymbolMeta(string Name, string File, SemanticClass Class, string Description);
