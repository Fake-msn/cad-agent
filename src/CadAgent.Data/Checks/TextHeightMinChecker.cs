using System.Text.RegularExpressions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50786-2012 5.1.3 标注最小字高：TextEntity 与 MText（MText : Entity，非 TextEntity，
/// 独立分支）。值含汉字 [\u4e00-\u9fff] → Height≥minHan（默认 3.5mm，"不宜"——issue.Severity
/// 覆写 Medium）；纯拉丁/数字（无汉字）→ Height≥minLatin（默认 2.5mm，"不应"——severity 沿用
/// rule.Severity）；混合文本按汉字判（较严）。空/纯空白值跳过（无字高语义，不产生幻影问题）。
/// 最小值比较仅用 1e-9 epsilon（CheckParams.AtLeast），非系列 0.01mm 容差。
/// 主导专业用房 3.0mm 变体需图类判定（deferred，见 DetectionBasis）。
/// </summary>
public sealed class TextHeightMinChecker : IRuleChecker
{
    public string CheckType => "text-height-min";

    private static readonly Regex HasHan = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        double minLatin = CheckParams.Scalar(rule.Check.Params, "minLatin", 2.5);
        double minHan = CheckParams.Scalar(rule.Check.Params, "minHan", 3.5);

        var issues = new List<Issue>();
        // TextValues.Enumerate：TextEntity/MText 共享枚举（T5 提取，行为与原 switch 等价）。
        foreach (var (handle, value, height) in TextValues.Enumerate(doc))
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            bool han = HasHan.IsMatch(value); // 含汉字（含混合文本）→ 汉字阈值（较严）
            double min = han ? minHan : minLatin;
            if (CheckParams.AtLeast(height, min)) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, han ? RuleSeverity.Medium : rule.Severity,
                new[] { handle },
                han
                    ? $"文本 \"{value}\" 字高 {CheckParams.Format(height)}mm 小于汉字标注最小字高 {CheckParams.Format(min)}mm（不宜）。"
                    : $"文本 \"{value}\" 字高 {CheckParams.Format(height)}mm 小于字母/数字标注最小字高 {CheckParams.Format(min)}mm（不应）。",
                rule.Suggestion,
                "实体字高直接比对；出图比例换算留计划3；主导专业用房 3.0mm 变体需图类判定（deferred）"));
        }
        return issues;
    }
}
