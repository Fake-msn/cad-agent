using System.Text.RegularExpressions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50786-2012 4.1.5 序号5 灯具标注格式 a-b(c×d×L)/e f：a=数量、b=型号、c=光源数、
/// d=容量、L=光源种类、e=安装高度（数值或 "—" 吸顶）、f=安装方式代码。
/// 候选（保守）：^\d+-[A-Za-z0-9]+\(\d+×\d+×[A-Za-z]+\)/（数量-型号(光源数×容量×种类)/…）——
/// 无 "/" 的文本不成候选（不判，宁漏勿误）。候选尾须为 "e f"（单空格分隔两非空段）：
/// 缺 /e f → 格式 issue（消息引全形）；e 非数值非 "—" → issue；f ∉ luminaireMounts 白名单
/// （params，CheckParams.StringSet）→ issue。白名单缺键/空 → f 不判（无白名单不可判，
/// 保守，同 SlotCode，不产生幻影问题）；e 判定不依赖 params。
/// 符号附近关联属视觉项（deferred）；分数排版（分子/分母上下排版）视觉项不检（DetectionBasis 注）。
/// </summary>
public sealed class LuminaireFormatChecker : IRuleChecker
{
    public string CheckType => "luminaire-format";

    /// <summary>保守候选：数量-型号(光源数×容量×种类)/ 前缀（简报原式）。</summary>
    internal static readonly Regex Candidate = new(@"^\d+-[A-Za-z0-9]+\(\d+×\d+×[A-Za-z]+\)/", RegexOptions.Compiled);

    /// <summary>全形尾部 "/e f"：单空格分隔两非空段（e/f 各自再判）。</summary>
    private static readonly Regex Tail = new(@"^(?<e>\S+) (?<f>\S+)$", RegexOptions.Compiled);

    private static readonly Regex Number = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        var mounts = CheckParams.StringSet(rule.Check.Params, "luminaireMounts");

        var issues = new List<Issue>();
        foreach (var (handle, value, _) in TextValues.Enumerate(doc))
        {
            var candidate = Candidate.Match(value);
            if (!candidate.Success) continue;

            string tail = value[candidate.Length..];
            var m = Tail.Match(tail);
            if (!m.Success)
            {
                issues.Add(NewIssue(rule, handle,
                    $"灯具标注 \"{value}\" 不符合格式 a-b(c×d×L)/e f（e=安装高度数值或 \"—\" 吸顶，f=安装方式代码，表4.1.5 序号5）。"));
                continue;
            }

            string e = m.Groups["e"].Value;
            string f = m.Groups["f"].Value;
            if (e != "—" && !Number.IsMatch(e))
                issues.Add(NewIssue(rule, handle,
                    $"灯具标注 \"{value}\" 安装高度 \"{e}\" 非数值且非 \"—\"（吸顶）（格式 a-b(c×d×L)/e f，表4.1.5 序号5）。"));
            if (mounts.Count > 0 && !mounts.Contains(f))
                issues.Add(NewIssue(rule, handle,
                    $"灯具标注 \"{value}\" 安装方式代码 \"{f}\" 不在 luminaireMounts 白名单（{string.Join("、", mounts.OrderBy(c => c, StringComparer.Ordinal))}）。"));
        }
        return issues;
    }

    private static Issue NewIssue(Rule rule, string handle, string message) =>
        new(rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
            rule.Force, rule.Severity,
            new[] { handle },
            message,
            rule.Suggestion,
            "灯具标注模式候选；符号附近关联属视觉（deferred）；分数排版视觉项不检");
}
