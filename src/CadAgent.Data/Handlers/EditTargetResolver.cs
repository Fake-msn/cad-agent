using System.Globalization;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;

namespace CadAgent.Data.Handlers;

/// <summary>编辑目标所有者空间（Plan-2 三空间；块定义编辑属 Plan-3）。</summary>
public enum EditOwnerKind { Model, Paper, InsertAttribute }

/// <summary>
/// 解析结果：<see cref="LayoutName"/> 仅纸面/纸面属性携带；
/// <see cref="AttributeOwner"/>/<see cref="InsertHandle"/> 仅 InsertAttribute 携带。
/// </summary>
public sealed record EditTarget(
    Entity Entity, EditOwnerKind Owner, string? LayoutName = null,
    Insert? AttributeOwner = null, ulong? InsertHandle = null);

/// <summary>
/// 统一目标解析（spec §3.1）：模型顶层 → 纸面顶层（布局名 Ordinal）→ 顶层 Insert 属性
/// （模型再纸面）。显式限定（space:"paper"+layout / attribute:true）fail-closed 不回退；
/// 同句柄多空间命中拒绝（脏图）；含 block 参数不检索块定义（Plan-3）。
/// </summary>
public static class EditTargetResolver
{
    public const string NotFoundMessage = "句柄不存在（已检索：模型/纸面/顶层属性）";
    public const string AmbiguousMessage = "句柄在多个空间出现，请用 space/layout/attribute 限定";
    public const string BlockDeferredMessage = "块内编辑将在后续切片提供（Plan-3）";

    public static EditTarget? Resolve(DrawingSession s, JsonElement p) => TryResolve(s, p, out _);

    /// <summary>解析失败→null；<paramref name="error"/> 仅在语义拒绝（多空间命中）时给出文案。</summary>
    public static EditTarget? TryResolve(DrawingSession s, JsonElement p, out string? error)
    {
        error = null;
        // block：块定义检索属 Plan-3——含该参数一律不解析（处理器入口已先行拒绝；
        // 此处兜底防静默落回模型顶层误改全实例）。
        if (HasValue(p, "block") || !TryReadHandle(p, out ulong handle)) return null;

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

    private static bool HasValue(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool IsTrue(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
