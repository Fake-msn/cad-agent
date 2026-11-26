using System.Globalization;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Handlers;

/// <summary>set-text / set-attribute 共用的参数与顶层集合读取（校验先于任何变更）。</summary>
internal static class TextHandlers
{
    public static bool HasValue(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>字符串参数契约：缺失/非字符串 fail-closed（不把数字静默转成文本）。</summary>
    public static bool TryReadString(JsonElement p, string name, out string? value, out string message)
    {
        value = null;
        message = $"{name} 必填且须为字符串";
        if (!p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString() ?? "";
        return true;
    }

    // handle 契约同 EditTargetResolver（数字或十进制字符串），此处用于 insert 参数。
    public static bool TryReadHandle(JsonElement p, string name, out ulong handle)
    {
        handle = 0;
        if (!p.TryGetProperty(name, out var hEl)) return false;
        if (hEl.ValueKind == JsonValueKind.Number) return hEl.TryGetUInt64(out handle);
        return hEl.ValueKind == JsonValueKind.String
               && ulong.TryParse(hEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out handle);
    }

    /// <summary>
    /// insert 检索范围：默认模型顶层 + 各纸面布局顶层（不递归块定义，Plan-3）；
    /// space:"paper"/layout 限定则只查该布局（fail-closed，不回退模型）。
    /// </summary>
    public static (List<CadObjectCollection<Entity>>? Collections, string Error) TopLevelCollections(
        DrawingSession s, JsonElement p, bool paperRestricted)
    {
        var collections = new List<CadObjectCollection<Entity>>();
        if (paperRestricted)
        {
            string? layoutName = HandlerHelpers.OptString(p, "layout");
            if (string.IsNullOrEmpty(layoutName))
                return (null, "space:\"paper\" 需指定 layout");
            ACadSharp.Objects.Layout? layout = EditTargetResolver.FindLayout(s, layoutName);
            if (layout is null) return (null, $"布局不存在：{layoutName}");
            if (!layout.IsPaperSpace || layout.AssociatedBlock is null)
                return (null, $"布局不是纸面空间：{layoutName}");
            collections.Add(layout.AssociatedBlock.Entities);
            return (collections, "");
        }
        collections.Add(s.Doc.Entities);
        if (s.Doc.Layouts is not null)
        {
            // 纸面布局口径同 Resolver（IsPaperSpace + Ordinal 序；Model 布局排除防双查）。
            foreach (var layout in s.Doc.Layouts
                         .Where(l => l.IsPaperSpace && l.AssociatedBlock is not null)
                         .OrderBy(l => l.Name, StringComparer.Ordinal))
                collections.Add(layout.AssociatedBlock!.Entities);
        }
        return (collections, "");
    }
}

/// <summary>
/// set-text：文字/实例属性改值（spec §3.3）。空串=清空（合法）；TEXT/ATTRIB 为单行
/// 实体禁换行（多行仅 MText）；ADEF 的 Value 是块内默认值（全实例语义，Plan-3）。
/// </summary>
public sealed class SetTextHandler : IStepHandler
{
    public const int MaxLength = 4096;
    public const string ViewportMessage = "视口对象不支持编辑";
    public const string NewlineMessage = "单行文字不支持换行（多行请用 MText）";
    public const string AttributeDefinitionMessage = "属性定义默认值编辑未支持";

    public string Kind => "set-text";

    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectBlockParam(p) is { } blockRejected) return blockRejected;
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        var target = EditTargetResolver.TryResolve(s, p, out string? resolveError);
        if (target == null)
            return new StepResult(false, resolveError ?? EditTargetResolver.NotFoundMessage, "");
        if (target.Entity is Viewport) return new StepResult(false, ViewportMessage, "");
        if (!TextHandlers.TryReadString(p, "text", out string? text, out string paramError))
            return new StepResult(false, paramError, "");
        if (text!.Length > MaxLength)
            return new StepResult(false, $"text 超长（上限 {MaxLength} 字符）", "");
        switch (target.Entity)
        {
            // ADEF/MText/ATTRIB 分支必须先于 TextEntity（三者 : TextEntity）：
            // ADEF 属块内默认值语义；MText 的多行能力不能被单行分支吞掉。
            case AttributeDefinition:
                return new StepResult(false, AttributeDefinitionMessage, "");
            case MText mtext:
                mtext.Value = text;
                break;
            case AttributeEntity attribute:
                if (text.Contains('\n')) return new StepResult(false, NewlineMessage, "");
                attribute.Value = text;
                break;
            case TextEntity textEntity:
                if (text.Contains('\n')) return new StepResult(false, NewlineMessage, "");
                textEntity.Value = text;
                break;
            default:
                return new StepResult(false, $"目标不是文字实体：{target.Entity.GetType().Name}", "");
        }
        return new StepResult(true, "", target.Entity.Handle.ToString());
    }
}

/// <summary>
/// set-attribute：实例属性改值。主形态 {handle,value} 直指 ATTRIB；次形态 {insert,tag,value}
/// 在顶层 Insert（模型/纸面）按 tag 精确（Ordinal，CAD tag 大小写敏感）定位，
/// 同 insert 同 tag 多条 fail-closed 要求句柄形态；块定义内属性属 Plan-3。
/// </summary>
public sealed class SetAttributeHandler : IStepHandler
{
    public const string DuplicateTagMessage = "同标签属性不唯一，请用句柄";
    public const string NotFoundInsertMessage = "insert 句柄不存在（已检索：模型/纸面顶层）";
    public const string NotAttributeMessage = "目标不是实例属性";
    public const string MissingFormMessage = "set-attribute 需要 handle 或 insert+tag";

    public string Kind => "set-attribute";

    public StepResult Apply(IDrawingSession session, JsonElement p)
    {
        var s = HandlerHelpers.AsDrawing(session);
        if (HandlerHelpers.RejectBlockParam(p) is { } blockRejected) return blockRejected;
        if (HandlerHelpers.RejectUnsupportedSpace(p) is { } spaceRejected) return spaceRejected;
        if (!TextHandlers.TryReadString(p, "value", out string? value, out string paramError))
            return new StepResult(false, paramError, "");
        // 属性值同为单行文字（AttributeEntity : TextEntity）：与 set-text 的 ATTRIB 分支同口径。
        if (value!.Contains('\n')) return new StepResult(false, SetTextHandler.NewlineMessage, "");
        if (value.Length > SetTextHandler.MaxLength)
            return new StepResult(false, $"value 超长（上限 {SetTextHandler.MaxLength} 字符）", "");

        if (TextHandlers.HasValue(p, "handle"))
        {
            var target = EditTargetResolver.TryResolve(s, p, out string? resolveError);
            if (target == null)
                return new StepResult(false, resolveError ?? EditTargetResolver.NotFoundMessage, "");
            if (target.Entity is not AttributeEntity attribute)
                return new StepResult(false, NotAttributeMessage, "");
            attribute.Value = value;
            return new StepResult(true, "", attribute.Handle.ToString());
        }

        if (!TextHandlers.HasValue(p, "insert")) return new StepResult(false, MissingFormMessage, "");
        if (!TextHandlers.TryReadHandle(p, "insert", out ulong insertHandle))
            return new StepResult(false, NotFoundInsertMessage, "");
        string? tag = HandlerHelpers.OptString(p, "tag");
        if (string.IsNullOrEmpty(tag)) return new StepResult(false, "tag 必填且须为非空字符串", "");

        bool paperRestricted = TextHandlers.HasValue(p, "space") || TextHandlers.HasValue(p, "layout");
        var (collections, scopeError) = TextHandlers.TopLevelCollections(s, p, paperRestricted);
        if (collections == null) return new StepResult(false, scopeError, "");

        var inserts = new List<Insert>();
        foreach (var entities in collections)
            inserts.AddRange(entities.OfType<Insert>().Where(i => i.Handle == insertHandle));
        if (inserts.Count == 0) return new StepResult(false, NotFoundInsertMessage, "");
        // 脏图同句柄多 insert：按句柄歧义拒绝（与 Resolver 同文案），不猜其一。
        if (inserts.Distinct().Count() > 1)
            return new StepResult(false, EditTargetResolver.AmbiguousMessage, "");

        var matches = inserts[0].Attributes
            .Where(a => string.Equals(a.Tag, tag, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0) return new StepResult(false, $"insert 上不存在标签：{tag}", "");
        if (matches.Count > 1) return new StepResult(false, DuplicateTagMessage, "");
        matches[0].Value = value;
        return new StepResult(true, "", matches[0].Handle.ToString());
    }
}
