using System.Text.RegularExpressions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// GB/T 50786-2012 3.4.3（应）／4.2.1（宜）槽位代码白名单（modal 双录"应（3.4.3）／宜（4.2.1）——
/// 情态冲突在案，按较严应执行"；force recommended 由规则数据承载，检查器不覆写）。
/// params：layingMethods/layingPositions/luminaireMounts（三白名单，List&lt;object&gt;→字符串集合
/// 经 CheckParams.StringSet；数据逐字自 D9，由规则包提供——方式 SC,MT,CP,PC,FPC,KPC,CT,CL,MR,PR,M,DB,TC,CE；
/// 部位 AB,AC,CE,SCE,WS,RS,CC,BC,CLC,WC,FC；灯具安装 SW,CS,DS,W,C,R,CR,WR,S,CL,HM）。
/// 候选：缆线简写形文本（CableFormatChecker.SimpleForm 全匹配）。按 '-' 分段槽位判定：
/// 4 段→末段=敷设部位（∈positions）、倒数第二段去数字=敷设方式（∈methods）；
/// 3 段→末段含数字=仅方式槽（去数字判 methods）；纯字母尾码按白名单成员资格定槽：
/// ∈methods ∉positions→方式槽、∈positions ∉methods→部位槽、双属或双不属→不判（宁漏勿滥）；
/// 2 段（BV-3×2.5，无尾码）不判。CE/CL 一码多义按槽位消歧（D9 注记）：同码在不同槽位
/// 按各自白名单成员资格判（CE 方式/部位均合法；CL 方式合法、部位非法——部位白名单为 CLC）。
/// 白名单缺键/空→对应槽位不判（无白名单不可判，保守，不产生幻影问题）。
/// 灯具安装尾码（luminaireMounts）校验留 luminaire-format（T5）；非缆线文本可能漏检。
/// </summary>
public sealed class SlotCodeChecker : IRuleChecker
{
    public string CheckType => "slot-code-whitelist";

    private static readonly Regex Digits = new(@"\d", RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var doc = ((DrawingSession)session).Doc;
        var methods = CheckParams.StringSet(rule.Check.Params, "layingMethods");
        var positions = CheckParams.StringSet(rule.Check.Params, "layingPositions");
        // luminaireMounts 随参转换校验（类型治理）；灯具安装尾码判定留 luminaire-format（T5）。
        _ = CheckParams.StringSet(rule.Check.Params, "luminaireMounts");

        var issues = new List<Issue>();
        // TextValues.Enumerate：TextEntity/MText 共享枚举（T5 提取，行为与原 switch 等价）。
        foreach (var (handle, value, _) in TextValues.Enumerate(doc))
        {
            if (!CableFormatChecker.SimpleForm.IsMatch(value)) continue; // 仅缆线简写形候选

            string[] parts = value.Split('-');
            if (parts.Length < 3) continue; // 无尾码（BV-3×2.5）不判

            // 简写形正则保证形态：4 段→[型号, 规格, 方式+数字, 部位]；3 段→末段为方式+数字或纯字母尾码。
            string last = parts[^1];
            bool lastHasDigits = Digits.IsMatch(last);
            string? position = null;
            string? method = null;
            if (parts.Length >= 4)
            {
                position = last;
                method = Digits.Replace(parts[^2], "");
            }
            else if (lastHasDigits)
            {
                method = Digits.Replace(last, "");
            }
            else
            {
                // 3 段纯字母尾码：按白名单成员资格定槽（CE/CL 消歧先例）——仅方式成员→方式槽、
                // 仅部位成员→部位槽；双属或双不属→不判（宁漏勿滥，尾码语义不可确知）。
                bool inMethods = methods.Contains(last);
                bool inPositions = positions.Contains(last);
                if (inMethods && !inPositions) method = last;
                else if (inPositions && !inMethods) position = last;
            }

            if (method is not null && methods.Count > 0 && !methods.Contains(method))
                issues.Add(BadCode(rule, handle, value, method, "敷设方式", "表4.2.1-1", methods));
            if (position is not null && positions.Count > 0 && !positions.Contains(position))
                issues.Add(BadCode(rule, handle, value, position, "敷设部位", "表4.2.1-2", positions));
        }
        return issues;
    }

    private static Issue BadCode(Rule rule, string handle, string value, string code,
        string slot, string table, HashSet<string> whitelist) =>
        new(rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
            rule.Force, rule.Severity,
            new[] { handle },
            $"缆线标注 \"{value}\" {slot}代码 \"{code}\" 不在{table}白名单（{string.Join("、", whitelist.OrderBy(c => c, StringComparer.Ordinal))}）。",
            rule.Suggestion,
            "缆线简写形按 '-' 分段槽位判定（4段末段=部位、倒数第二段去数字=方式；3段含数字末段=方式）；3段纯字母尾码按白名单成员资格定槽（仅方式或仅部位成员），双属/双不属不判（宁漏勿滥）；CE/CL 一码多义按槽位消歧；灯具安装尾码留 luminaire-format；非缆线文本可能漏检");
}
