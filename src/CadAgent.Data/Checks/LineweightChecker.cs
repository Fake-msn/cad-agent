using ACadSharp;
using ACadSharp.Extensions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50001-2017 4.0.1 线宽组：实体显式线宽须落在 groups 并集内（容差 0.01mm）。
/// ACadSharp 3.8.35 核实：属性为 Entity.LineWeight（LineWeightType 枚举，short），
/// 显式值 W0..W211 经 GetLineWeightValue() 换算为 mm；
/// ByLayer(-1)/ByBlock(-2)/Default(-3)/ByDIPs(-4) 为哨兵值（非显式 mm 线宽）→跳过该实体不报。
/// </summary>
public sealed class LineweightChecker : IRuleChecker
{
    public string CheckType => "lineweight-group";

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        double[] groups = CheckParams.Series(rule.Check.Params, "groups");

        var issues = new List<Issue>();
        foreach (var e in doc.Entities)
        {
            var lw = e.LineWeight;
            // 哨兵值：线宽由图层/块/默认决定，不属显式线宽违规（GetLineWeightValue 对哨兵值无意义）。
            if (lw is LineWeightType.ByLayer or LineWeightType.ByBlock or LineWeightType.Default or LineWeightType.ByDIPs)
                continue;

            double mm = lw.GetLineWeightValue();
            if (CheckParams.InSeries(mm, groups)) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { e.Handle.ToString() },
                $"线宽 {CheckParams.Format(mm)}mm 不在线宽组并集内（{CheckParams.FormatSeries(groups)}）；线宽组依据 {rule.Source.Standard} {rule.Source.Clause}。",
                rule.Suggestion,
                "实体显式线宽（LineWeightType→mm）直接比对线宽组并集；ByLayer/ByBlock/Default/ByDIPs 哨兵值跳过不报"));
        }
        return issues;
    }
}
