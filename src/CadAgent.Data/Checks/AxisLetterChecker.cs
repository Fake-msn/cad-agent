using System.Text.RegularExpressions;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50001-2017 8.0.4 轴号字母：overlay 中 SemanticClass.Axis 且 Confidence≥0.6 的标注
/// 句柄→取 TextEntity 值；合法形式：单大写拉丁字母（I/O/Z 除外）、双大写字母（同样排除
/// I/O/Z——8.0.4 禁用字母适用于一切轴线编号）或字母+数字注脚（8.0.4 溢出/附加轴条款）。
/// 小写或含 I/O/Z→issue。overlay 无 Axis 标注→返回空列表（跳过计数仅针对规则级，由引擎记账）。
/// </summary>
public sealed class AxisLetterChecker : IRuleChecker
{
    public string CheckType => "axis-letter";

    // A-Y 去掉 I/O/Z：A-H、J-N、P-Y；{1,2} 覆盖单/双字母；另接受 字母+数字 注脚。
    private static readonly Regex ValidAxis = new(@"^[A-HJ-NP-Y]{1,2}$|^[A-HJ-NP-Y][0-9]+$", RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        var texts = doc.Entities.OfType<TextEntity>().ToDictionary(t => t.Handle.ToString(), t => t);

        var issues = new List<Issue>();
        foreach (var a in overlay.Annotations)
        {
            if (a.Class != SemanticClass.Axis || a.Confidence < 0.6) continue;
            if (!texts.TryGetValue(a.TargetHandle, out var text)) continue; // 句柄无对应 TEXT（如 MText/已删除）→ 不查
            string value = text.Value ?? "";
            if (ValidAxis.IsMatch(value)) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { text.Handle.ToString() },
                $"轴号 \"{value}\" 不符合 8.0.4：应为单大写拉丁字母（I/O/Z 除外）、双大写字母或字母+数字注脚（附加轴）；小写或含 I/O/Z 均违规。",
                rule.Suggestion,
                $"语义叠加层 Axis 标注（来源:{a.Provenance}）+ 文本值"));
        }
        return issues;
    }
}