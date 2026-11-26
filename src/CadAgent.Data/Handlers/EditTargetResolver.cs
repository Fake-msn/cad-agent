using System.Globalization;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;

namespace CadAgent.Data.Handlers;

/// <summary>编辑目标所有者空间（Plan-2 三空间 + Plan-3 块定义）。</summary>
public enum EditOwnerKind { Model, Paper, InsertAttribute, BlockDefinition }

/// <summary>
/// 解析结果：<see cref="LayoutName"/> 仅纸面/纸面属性携带；
/// <see cref="AttributeOwner"/>/<see cref="InsertHandle"/> 仅 InsertAttribute 携带；
/// <see cref="BlockName"/> 仅 BlockDefinition 携带。
/// </summary>
public sealed record EditTarget(
    Entity Entity, EditOwnerKind Owner, string? LayoutName = null,
    Insert? AttributeOwner = null, ulong? InsertHandle = null, string? BlockName = null);

/// <summary>
/// 统一目标解析（spec §3.1）：块定义（显式 block）→ 模型顶层 → 纸面顶层（布局名 Ordinal）
/// → 顶层 Insert 属性（模型再纸面）。显式限定（block / space:"paper"+layout / attribute:true）
/// fail-closed 不回退；同句柄多空间命中拒绝（脏图）；无 block 时不检索块定义（防误伤全实例）。
/// </summary>
public static class EditTargetResolver
{
    public const string NotFoundMessage = "句柄不存在（已检索：模型/纸面/顶层属性）";
    public const string AmbiguousMessage = "句柄在多个空间出现，请用 space/layout/attribute 限定";
    public const string BlockNotFoundMessage = "块定义中未找到句柄: {0}（块 {1}）";
    public const string BlockProtectedMessage = "块不可编辑（匿名/外部参照/动态/未加载）: {0}";

    // 处理器入口预拒 block 的专用文案（Task 5 接通块内编辑后仅 add-* 新建仍用它）。
    public const string BlockDeferredMessage = "块内编辑将在后续切片提供（Plan-3）";

    public static EditTarget? Resolve(DrawingSession s, JsonElement p) => TryResolve(s, p, out _);

    /// <summary>解析失败→null；<paramref name="error"/> 仅在语义拒绝（多空间命中）时给出文案。</summary>
    public static EditTarget? TryResolve(DrawingSession s, JsonElement p, out string? error)
    {
        error = null;
        if (!TryReadHandle(p, out ulong handle)) return null;

        // 显式块定义：仅检索该块自身实体（单层，不递归嵌套块定义）。
        if (HasValue(p, "block"))
        {
            // 与纸面/属性限定互斥：多空间信号即 fail-closed，不猜优先级。
            if (HasValue(p, "space") || HasValue(p, "layout") || HasValue(p, "attribute")) return null;
            string? blockName = ReadBlockName(p);
            if (blockName is null) return null; // 非字符串/非整数 → fail-closed
            BlockRecord? block = FindBlock(s, blockName);
            if (block is null || !IsBlockEditable(block)) return null; // 处理器用 FindBlock/IsBlockEditable 给专用文案
            Entity? match = block.Entities.FirstOrDefault(e => e.Handle == handle);
            return match is null
                ? null
                : new EditTarget(match, EditOwnerKind.BlockDefinition, BlockName: block.Name);
        }

        bool paperRestricted = HasValue(p, "space") || HasValue(p, "layout");
        if (HasValue(p, "space")
            && !string.Equals(HandlerHelpers.OptString(p, "space"), "paper", StringComparison.OrdinalIgnoreCase))
            return null; // fail-closed：未知空间值
        string? layoutName = HandlerHelpers.OptString(p, "layout");
        if (paperRestricted && string.IsNullOrEmpty(layoutName)) return null; // paper 必配 layout
        bool attributeOnly = false;
        if (HasValue(p, "attribute"))
        {
            if (!IsTrue(p, "attribute")) return null; // 仅接受字面 true
            attributeOnly = true;
        }

        var matches = new List<EditTarget>();
        if (paperRestricted)
        {
            Layout? layout = FindLayout(s, layoutName);
            if (layout is null || !layout.IsPaperSpace || layout.AssociatedBlock is null) return null;
            if (!attributeOnly)
                AddEntityMatches(matches, layout.AssociatedBlock.Entities, EditOwnerKind.Paper, layout.Name, handle);
            AddAttributeMatches(matches, layout.AssociatedBlock.Entities, layout.Name, handle);
        }
        else
        {
            if (!attributeOnly)
            {
                AddEntityMatches(matches, s.Doc.Entities, EditOwnerKind.Model, null, handle);
                foreach (Layout layout in PaperLayouts(s))
                    AddEntityMatches(matches, layout.AssociatedBlock!.Entities, EditOwnerKind.Paper, layout.Name, handle);
            }
            AddAttributeMatches(matches, s.Doc.Entities, null, handle);
            foreach (Layout layout in PaperLayouts(s))
                AddAttributeMatches(matches, layout.AssociatedBlock!.Entities, layout.Name, handle);
        }

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            error = AmbiguousMessage;
            return null;
        }
        return matches[0];
    }

    /// <summary>
    /// 块名 OrdinalIgnoreCase 精确匹配（块表名大小写不敏感，DXF 语义）。
    /// 布局/系统块（布局1、*Model_Space、*Paper_Space）带 Layout → 排除：纸面实体只能经 Paper 空间到达。
    /// </summary>
    public static BlockRecord? FindBlock(DrawingSession s, string name) =>
        string.IsNullOrEmpty(name)
            ? null
            : s.Doc.BlockRecords.FirstOrDefault(b => b.Layout is null
                && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>匿名/动态/外部参照（含覆盖与依赖）/未加载 → 不可编辑（spec §5.3 非目标）。</summary>
    public static bool IsBlockEditable(BlockRecord b) =>
        !b.IsAnonymous && !b.IsDynamic && !b.IsUnloaded
        && (b.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay | BlockTypeFlags.XRefDependent)) == 0;

    /// <summary>布局名 Ordinal 精确匹配（与 ComputeHash/SheetRegistry 布局口径一致）。</summary>
    public static Layout? FindLayout(DrawingSession s, string? name)
    {
        if (string.IsNullOrEmpty(name) || s.Doc.Layouts is null) return null;
        return s.Doc.Layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
    }

    /// <summary>纸面布局（Name Ordinal 序）；Model 布局（IsPaperSpace=false）排除，防与模型顶层双命中。</summary>
    private static IEnumerable<Layout> PaperLayouts(DrawingSession s)
    {
        if (s.Doc.Layouts is null) yield break;
        foreach (Layout layout in s.Doc.Layouts
                     .Where(l => l.IsPaperSpace && l.AssociatedBlock is not null)
                     .OrderBy(l => l.Name, StringComparer.Ordinal))
            yield return layout;
    }

    private static void AddEntityMatches(List<EditTarget> matches, CadObjectCollection<Entity> entities,
        EditOwnerKind owner, string? layoutName, ulong handle)
    {
        foreach (Entity e in entities)
            if (e.Handle == handle)
                matches.Add(new EditTarget(e, owner, layoutName));
    }

    /// <summary>仅顶层 Insert 的属性（实例语义）；不递归块内 Insert（块定义属 Plan-3）。</summary>
    private static void AddAttributeMatches(List<EditTarget> matches, CadObjectCollection<Entity> entities,
        string? layoutName, ulong handle)
    {
        foreach (Insert insert in entities.OfType<Insert>())
            foreach (AttributeEntity attr in insert.Attributes)
                if (attr.Handle == handle)
                    matches.Add(new EditTarget(attr, EditOwnerKind.InsertAttribute, layoutName, insert, insert.Handle));
    }

    // handle 契约同历史 Find 先例（HandleContractTests）：JSON 数字或十进制字符串皆可，非法形态→未命中。
    private static bool TryReadHandle(JsonElement p, out ulong handle)
    {
        handle = 0;
        if (!p.TryGetProperty("handle", out var hEl)) return false;
        if (hEl.ValueKind == JsonValueKind.Number) return hEl.TryGetUInt64(out handle);
        return hEl.ValueKind == JsonValueKind.String
               && ulong.TryParse(hEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out handle);
    }

    // block 名严格读取：string 原样 / number 仅整数（invariant）；其余（含 1.5）→ null，不抛。
    private static string? ReadBlockName(JsonElement p)
    {
        if (!p.TryGetProperty("block", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number when v.TryGetUInt64(out ulong n) => n.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static bool HasValue(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool IsTrue(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
