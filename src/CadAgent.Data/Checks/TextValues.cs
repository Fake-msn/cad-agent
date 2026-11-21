using ACadSharp;
using ACadSharp.Entities;

namespace CadAgent.Data.Checks;

/// <summary>
/// 文本值枚举辅助（2b-ii-b T5 控制器增补）：TextEntity/MText switch 三处重复
/// （CableFormat/SlotCode/TextHeightMin）→ 提取共享，LuminaireFormat 复用。
/// MText : Entity（非 TextEntity），独立分支；null Value → 空串（与原 switch 一致）；
/// 非文本实体跳过。惰性迭代器，枚举顺序/Handle/Height 取值与原 foreach+switch 逐字等价。
/// </summary>
internal static class TextValues
{
    public static IEnumerable<(string Handle, string Value, double Height)> Enumerate(CadDocument doc)
    {
        foreach (var e in doc.Entities)
        {
            switch (e)
            {
                case TextEntity text:
                    yield return (e.Handle.ToString(), text.Value ?? "", text.Height);
                    break;
                case MText mtext: // MText : Entity（非 TextEntity），独立分支
                    yield return (e.Handle.ToString(), mtext.Value ?? "", mtext.Height);
                    break;
            }
        }
    }
}
