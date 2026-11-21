using System.Text.RegularExpressions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50786-2012 4.1.5 序号8 缆线标注格式（modal 双录"应（3.4.3）／宜（4.2.1）——情态冲突在案，
/// 按较严应执行"；force recommended 由规则数据承载，检查器不覆写）。
/// 候选：TextEntity/MText 值以 2～4 个大写字母型号前缀开头且含 "×"（保守启发）。
/// 简报候选式 ^[A-Z]{2,4}- 的 "-" 不强制——否则缺 "-" 的格式违规（如 BV3×2.5）永不成候选，
/// 与简报测试1矛盾；前缀后随 "-" 或数字的常见形态均入候选。
/// 合规简写形（表4.1.5 序号8 的 N/PE 省略实例，如 BV-3×2.5-PC20-WC）：
/// ^[A-Z]{2,4}-\d+(\.\d+)?×\d+(\.\d+)?(-[A-Z]{1,3}\d+)?(-[A-Z]{1,3})?$。
/// 完整形 a b-c(d×e+f×g)i-jh 含空格/+/括号——第一 slice 仅校验简写形候选，完整形候选不判
/// （DetectionBasis 注"完整形校验留后续"）。非缆线文本可能漏检（启发式如实注明）。
/// </summary>
public sealed class CableFormatChecker : IRuleChecker
{
    public string CheckType => "cable-format";

    /// <summary>型号前缀（2～4 大写字母）开头；"-" 不强制（缺 "-" 本身即格式违规，须成候选）。</summary>
    internal static readonly Regex CandidatePrefix = new(@"^[A-Z]{2,4}", RegexOptions.Compiled);

    /// <summary>表4.1.5 序号8 合规简写形（N/PE 省略实例）；SlotCodeChecker 复用为候选判定。</summary>
    internal static readonly Regex SimpleForm = new(
        @"^[A-Z]{2,4}-\d+(\.\d+)?×\d+(\.\d+)?(-[A-Z]{1,3}\d+)?(-[A-Z]{1,3})?$", RegexOptions.Compiled);

    /// <summary>候选启发：型号前缀 + ×（缆线型号+规格特征，保守）。</summary>
    internal static bool IsCandidate(string value) =>
        CandidatePrefix.IsMatch(value) && value.Contains('×');

    /// <summary>完整形特征（空格/加号/括号）→ 第一 slice 不判，留后续。</summary>
    internal static bool IsFullForm(string value) =>
        value.Contains(' ') || value.Contains('+') || value.Contains('(') || value.Contains(')');

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        var issues = new List<Issue>();
        // TextValues.Enumerate：TextEntity/MText 共享枚举（T5 提取，行为与原 switch 等价）。
        foreach (var (handle, value, _) in TextValues.Enumerate(doc))
        {
            if (!IsCandidate(value)) continue;
            if (IsFullForm(value)) continue; // 完整形候选不判（校验留后续）
            if (SimpleForm.IsMatch(value)) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { handle },
                $"缆线标注 \"{value}\" 不符合格式 a b-c(d×e+f×g)i-jh（表4.1.5 序号8）；简写实例：BV-3×2.5-PC20-WC。",
                rule.Suggestion,
                "文本模式候选（型号前缀+×），简写形正则；完整形（含空格/+/括号）校验留后续；非缆线文本可能漏检"));
        }
        return issues;
    }
}
