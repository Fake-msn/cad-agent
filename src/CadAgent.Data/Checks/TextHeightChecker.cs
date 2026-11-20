using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50001-2017 5.0.2 字高系列：TextEntity 与 MText（MText : Entity，非 TextEntity，
/// 独立分支）的字高须落在 hanVector/other 两系列并集内（容差 0.01mm）。
/// 第一切片按并集判定，字体归属细化留后续（消息注明）；未检出图比例，出图换算见 14.0.4（计划3）。
/// </summary>
public sealed class TextHeightChecker : IRuleChecker
{
    public string CheckType => "text-height-series";

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        double[] hanVector = CheckParams.Series(rule.Check.Params, "hanVector");
        double[] other = CheckParams.Series(rule.Check.Params, "other");
        double[] union = hanVector.Concat(other).ToArray();

        var issues = new List<Issue>();
        foreach (var e in doc.Entities)
        {
            double height;
            switch (e)
            {
                case TextEntity text:
                    height = text.Height;
                    break;
                case MText mtext: // MText : Entity（非 TextEntity），两者都有 Height，独立处理
                    height = mtext.Height;
                    break;
                default:
                    continue;
            }
            if (CheckParams.InSeries(height, union)) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { e.Handle.ToString() },
                $"字高 {CheckParams.Format(height)}mm 不在字高系列并集内（汉字矢量系列：{CheckParams.FormatSeries(hanVector)}；其他字体系列：{CheckParams.FormatSeries(other)}）。第一切片按两系列并集判定，字体归属细化留后续。",
                rule.Suggestion,
                "实体字高直接比对；未检出图比例，出图换算见 14.0.4（计划3）"));
        }
        return issues;
    }
}