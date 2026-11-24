namespace CadAgent.Core.Cad;

/// <summary>TDb C++ 类名→语义类静态映射（spec 切片A §3）。初版覆盖 probe 实证类，新类增量补。</summary>
public static class TDbClassMap
{
    public static SemanticClass ClassFor(string objectName) => objectName switch
    {
        "TDbWall" => SemanticClass.Wall,
        "TDbColumn" => SemanticClass.Column,
        "TDbEquipment" => SemanticClass.Equipment,
        "TDbText" or "TDbDrawingName" or "TDbSymbArrow" => SemanticClass.Annotation,
        _ => SemanticClass.Unknown,
    };
}
