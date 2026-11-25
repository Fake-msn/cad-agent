namespace CadAgent.Core.Cad;

/// <summary>TDb C++ 类名→语义类静态映射（spec 切片A §3）。初版覆盖 probe 实证类，新类增量补。
/// 匹配口径：精确匹配（大小写敏感）；未实证类→Unknown。</summary>
public static class TDbClassMap
{
    public static SemanticClass ClassFor(string objectName) => objectName switch
    {
        "TDbWall" => SemanticClass.Wall,
        "TDbColumn" => SemanticClass.Column,
        "TDbEquipment" => SemanticClass.Equipment,
        "TDbText" or "TDbDrawingName" or "TDbSymbArrow" => SemanticClass.Annotation,
        // 切片B 保守扩类（仅实证三例；未实证类一律 Unknown，宁缺勿错）
        "TDbDimension2" or "TDbElevation" => SemanticClass.Annotation,
        "TDbSpace" => SemanticClass.Room,
        _ => SemanticClass.Unknown,
    };
}
