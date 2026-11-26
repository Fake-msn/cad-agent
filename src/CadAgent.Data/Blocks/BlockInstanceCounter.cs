using ACadSharp;
using ACadSharp.Entities;

namespace CadAgent.Data.Blocks;

/// <summary>
/// 块实例计数（确认提示用，约值）：模型顶层 + 纸面布局（IsPaperSpace，Name Ordinal 序）顶层为根，
/// 遇 Insert 按 Block.Name OrdinalIgnoreCase 比对 +1，并递归块内 Insert。
/// 下探截断与渲染器 MaxInsertDepth=2 同口径（根 0，depth>=2 只计不再深入）：计入深度 2 的
/// 实例、更深截断 → 影响约值/下界，非渲染可见实例数（渲染器 depth>=2 的 Insert 整体跳绘）。
/// 无环定义下只少计；环/自指定义（病态 DXF）仅深度有界，可能多计。
/// 布局关联块自身不作根（防与纸面实体双计）；无 LAYOUT 的孤儿纸面记录不遍历，
/// 未解析 xref（Insert.Block == null）跳过。纯读、确定性、无副作用。空块名/null doc → 0。
/// </summary>
public static class BlockInstanceCounter
{
    public static int Count(CadDocument? doc, string blockName, int maxDepth = 2)
    {
        if (doc is null || string.IsNullOrEmpty(blockName)) return 0;

        int n = 0;
        void Walk(IEnumerable<Entity> entities, int depth)
        {
            foreach (Entity e in entities)
            {
                if (e is not Insert ins || ins.Block is null) continue;
                if (string.Equals(ins.Block.Name, blockName, StringComparison.OrdinalIgnoreCase)) n++;
                // 自指/环块由此守卫终止（深度有界）
                if (depth < maxDepth) Walk(ins.Block.Entities, depth + 1);
            }
        }

        Walk(doc.Entities, 0);
        if (doc.Layouts is not null)
        {
            foreach (var layout in doc.Layouts
                         .Where(l => l.IsPaperSpace && l.AssociatedBlock is not null)
                         .OrderBy(l => l.Name, StringComparer.Ordinal))
                Walk(layout.AssociatedBlock!.Entities, 0);
        }
        return n;
    }
}
