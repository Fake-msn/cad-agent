using System.Text.RegularExpressions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50001-2017 13.0.1/13.0.2 图层命名：params mode="charset"|"structure"。
/// charset（13.0.1）：层名同时含汉字 [\u4e00-\u9fff] 与 A-Za-z → 汉英混用违规；
/// structure（13.0.2）：按 "-" 分段，段数∈[2,5]，每段非空且字符集∈[汉字A-Za-z0-9]
/// （汉字层与英文层同一字符集校验；专业代码白名单/附录B推荐名 deferred——视觉依赖表，消息注明）。
/// 图层 "0"（默认层）、"Defpoints"（大小写不敏感，AutoCAD 非打印定义点系统层）与含 '|' 的
/// xref 依赖层（XREF|LAYER，命名随宿主图纸）两模式均跳过；空 mode → 空列表（行为不变）；
/// 非空未知 mode → ArgumentException（T4 硬化：引擎隔离记账，不再静默空列表）。
/// </summary>
public sealed class LayerNameChecker : IRuleChecker
{
    public string CheckType => "layer-name";

    private static readonly Regex Han = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex Latin = new(@"[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex SegmentCharset = new(@"^[\u4e00-\u9fffA-Za-z0-9]+$", RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        string mode = rule.Check.Params.TryGetValue("mode", out var raw)
            ? Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            : "";
        if (mode == "") return Array.Empty<Issue>(); // 空 mode 行为不变：不检查
        if (mode is not ("charset" or "structure"))
            throw new ArgumentException($"未知 mode: {mode}");

        var doc = ((DrawingSession)session).Doc;
        var issues = new List<Issue>();
        foreach (var layer in doc.Layers)
        {
            string name = layer.Name ?? "";
            if (name == "0") continue; // 默认层跳过
            // 捆绑小修（最终审查）：Defpoints 系统层与 xref 依赖层（含 '|'）两模式均跳过，与 "0" 并列。
            if (string.Equals(name, "Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains('|')) continue;

            string? violation = mode == "charset" ? CharsetViolation(name) : StructureViolation(name);
            if (violation is null) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { layer.Handle.ToString() },
                $"图层 \"{name}\" {violation}",
                rule.Suggestion,
                "图层名直接检查"));
        }
        return issues;
    }

    /// <summary>13.0.1 汉英不得混用：同时含汉字与拉丁字母才违规；汉字+数字不算混用。</summary>
    private static string? CharsetViolation(string name) =>
        Han.IsMatch(name) && Latin.IsMatch(name)
            ? "汉英混用，违反 13.0.1：图层名不得汉字与英文字母混用，请统一为汉字或英文命名。"
            : null;

    /// <summary>13.0.2 分级命名：'-' 分隔 2～5 段，每段非空且仅含汉字/字母/数字。</summary>
    private static string? StructureViolation(string name)
    {
        string[] segments = name.Split('-');
        if (segments.Length is < 2 or > 5)
            return $"分级命名段数为 {segments.Length}，不符合 13.0.2：应按 \"-\" 分隔为 2～5 段。专业代码白名单/附录B推荐名核验暂缓（视觉依赖表，留后续）。";
        foreach (string segment in segments)
            if (!SegmentCharset.IsMatch(segment))
                return $"存在空段或非法字符段 \"{segment}\"，不符合 13.0.2：每段须非空且仅含汉字/字母/数字。专业代码白名单/附录B推荐名核验暂缓（视觉依赖表，留后续）。";
        return null;
    }
}
