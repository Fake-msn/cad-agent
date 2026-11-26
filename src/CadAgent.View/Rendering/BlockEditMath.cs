using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using CadAgent.Core.Cad;

namespace CadAgent.View.Rendering;

/// <summary>
/// 块编辑数学（Plan-3 Task 3）：Insert 链复合仿射 + 世界/纸面增量→块局部增量逆解。
/// 复合顺序与 <see cref="RenderModelBuilder"/> 的 Insert 展开逐字一致
/// （t.Compose(OcsAffine(normal).Compose(InsertAffine(insert)))，外→内），
/// 保证逆解与渲染落点同口径。纯逻辑、无 Avalonia 依赖。
/// </summary>
public static class BlockEditMath
{
    public const string SingularErrorMessage = "块插入变换不可逆（零缩放）";

    /// <summary>链解析失败文案（缺失 Insert 或无块定义）。</summary>
    public static string ChainErrorMessage(string handle) => $"插入链解析失败: {handle}";

    /// <summary>
    /// 把世界（模型/纸面同空间）增量逆解为最内层块局部增量。
    /// <paramref name="insertChain"/> 自外向内：chain[0] 为模型顶层或任一纸面布局顶层 Insert，
    /// chain[i+1] 位于 chain[i].Block.Entities。空链=无块路径，增量原样返回。
    /// </summary>
    public static (bool Ok, double Dx, double Dy, string Error) WorldDeltaToBlockDelta(
        CadDocument doc, IReadOnlyList<string> insertChain, double dx, double dy)
    {
        if (doc is null) return (false, 0, 0, ChainErrorMessage(string.Empty));
        if (insertChain is null || insertChain.Count == 0) return (true, dx, dy, string.Empty);

        Affine t = Affine.Identity;
        IEnumerable<Entity>? scope = RootScope(doc, insertChain[0]);
        foreach (string h in insertChain)
        {
            Insert? ins = FindInsert(scope, h);
            if (ins?.Block is null) return (false, 0, 0, ChainErrorMessage(h));
            t = t.Compose(RenderModelBuilder.OcsAffine(ins.Normal).Compose(Affine.InsertAffine(ins)));
            scope = ins.Block.Entities;
        }

        if (!t.TryInvertLinear(out Affine inv)) return (false, 0, 0, SingularErrorMessage);
        (double lx, double ly) = inv.Apply(dx, dy);
        return (true, lx, ly, string.Empty);
    }

    /// <summary>
    /// chain[0] 的顶层宿主：模型空间优先，其次纸面布局顶层（IsPaperSpace，Name Ordinal 序
    /// 防枚举序漂移）。都无命中→null（由调用方统一报"插入链解析失败"）。
    /// </summary>
    private static IEnumerable<Entity>? RootScope(CadDocument doc, string handle)
    {
        if (FindInsert(doc.Entities, handle) is not null) return doc.Entities;
        if (doc.Layouts is null) return null;
        foreach (Layout layout in doc.Layouts
                     .Where(l => l.IsPaperSpace && l.AssociatedBlock is not null)
                     .OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            IEnumerable<Entity> entities = layout.AssociatedBlock!.Entities;
            if (FindInsert(entities, handle) is not null) return entities;
        }
        return null;
    }

    /// <summary>句柄串 Ordinal 精确匹配（渲染器句柄口径：Handle.ToString() 十进制串）。</summary>
    private static Insert? FindInsert(IEnumerable<Entity>? scope, string handle)
    {
        if (scope is null) return null;
        foreach (Entity e in scope)
        {
            if (e is Insert ins
                && string.Equals(ins.Handle.ToString(CultureInfo.InvariantCulture), handle, StringComparison.Ordinal))
                return ins;
        }
        return null;
    }
}
