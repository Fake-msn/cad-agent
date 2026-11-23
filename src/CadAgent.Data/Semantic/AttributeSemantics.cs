using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 块属性值→语义类默认表（T3，天正电气构件语义）。
/// 仅 tag A 成类（大小写不敏感，与图层约定同口径）；N/$TEXT$ 等辅助属性不单独成类
/// （seeder 拼入 Evidence）。诚实原则：未识别值返回 Unknown，不猜。
/// 规则序=dispatch 表序：灯→箱/柜/屏→开关→插座→电动机/电机（"灯箱"类复合词取首命中）。
/// 后续 skill 别名可覆写本表（本任务不接）。
/// </summary>
public static class AttributeSemantics
{
    public static SemanticClass ClassForAttributeValue(string tag, string value)
    {
        if (!string.Equals(tag, "A", StringComparison.OrdinalIgnoreCase)) return SemanticClass.Unknown;
        if (string.IsNullOrEmpty(value)) return SemanticClass.Unknown;
        if (value.Contains("灯")) return SemanticClass.Lighting;
        if (value == "屏、台、箱、柜" || value.Contains("箱") || value.Contains("柜") || value.Contains("屏"))
            return SemanticClass.Equipment;
        if (value.Contains("开关")) return SemanticClass.Device;
        if (value.Contains("插座")) return SemanticClass.Device;
        if (value.Contains("电动机") || value.Contains("电机")) return SemanticClass.Equipment;
        return SemanticClass.Unknown;
    }
}
