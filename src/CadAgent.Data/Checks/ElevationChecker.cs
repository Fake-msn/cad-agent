using System.Globalization;
using System.Text.RegularExpressions;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50001-2017 11.8.4/11.8.5 标高注写格式：候选=overlay Annotation 类标注句柄的
/// TextEntity 值且匹配 ^(±|[+-])?\d+\.\d{2,3}$。params["mode"] 分支（T7 传 mode:decimals /
/// mode:sign；空 params=全查）：
/// - decimals：小数位须 3 位；2 位→issue 且 Severity 覆写为 Low，消息注明总平面豁免需人工确认
///   （11.8.4 可条款：总平面图可 2 位——第一切片不判图类，降级提示）。
/// - sign：零点须 ±0.000；正数不得带 "+"；负数须带 "-"（候选正则下负数必带 -，无 - 的负值
///   文本上不可区分，天然满足）；± 仅用于零点。
/// DetectionBasis 注明正则模式 + 检查模式 + overlay 来源。
/// </summary>
public sealed class ElevationChecker : IRuleChecker
{
    public string CheckType => "elevation-format";

    private const string PatternText = @"^(±|[+-])?\d+\.\d{2,3}$";
    private static readonly Regex Candidate = new(PatternText, RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        string mode = rule.Check.Params.TryGetValue("mode", out var raw)
            ? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""
            : "";
        bool checkDecimals = mode is "" or "decimals";
        bool checkSign = mode is "" or "sign";
        string modeLabel = mode switch
        {
            "decimals" => "小数位",
            "sign" => "符号",
            _ => "全部（小数位+符号）"
        };

        var doc = ((DrawingSession)session).Doc;
        var texts = doc.Entities.OfType<TextEntity>().ToDictionary(t => t.Handle.ToString(), t => t);

        var issues = new List<Issue>();
        foreach (var a in overlay.Annotations)
        {
            if (a.Class != SemanticClass.Annotation) continue;
            if (!texts.TryGetValue(a.TargetHandle, out var text)) continue;
            string value = text.Value ?? "";
            if (!Candidate.IsMatch(value)) continue;

            string basis = $"语义叠加层 Annotation 标注（来源:{a.Provenance}）+ 标高文本模式 {PatternText}；检查模式:{modeLabel}";
            int decimals = value.Length - value.IndexOf('.') - 1;
            double number = double.Parse(value.Replace("±", ""), CultureInfo.InvariantCulture);
            bool isZero = number == 0;

            if (checkDecimals && decimals == 2)
            {
                issues.Add(new Issue(
                    rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                    rule.Force, RuleSeverity.Low, // 覆写降级：11.8.4 可条款，图类待人工确认
                    new[] { text.Handle.ToString() },
                    $"标高 \"{value}\"：小数位为 2 位，应为 3 位（11.8.4）；总平面图可 2 位（11.8.4 可条款），请确认图类。",
                    rule.Suggestion,
                    basis));
            }

            if (!checkSign) continue;

            string? signProblem = (isZero, value) switch
            {
                (true, not "±0.000") => $"标高 \"{value}\"：零点标高须注写为 ±0.000（11.8.5）。",
                (false, ['+', ..]) => $"标高 \"{value}\"：正数标高不应带 \"+\" 号（11.8.5）。",
                (false, ['±', ..]) => $"标高 \"{value}\"：± 号仅用于零点标高 ±0.000（11.8.5）。",
                _ => null // 负数带 "-"（候选正则保证）或正数无符号 → 合规
            };
            if (signProblem is null) continue;

            issues.Add(new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { text.Handle.ToString() },
                signProblem,
                rule.Suggestion,
                basis));
        }
        return issues;
    }
}