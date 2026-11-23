using System.Text;
using System.Text.RegularExpressions;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;

namespace CadAgent.Data.Vision;

/// <summary>视觉读图单条声称（结构化 JSON {claims:[{text,region,confidence}]} 的元素）。</summary>
public sealed record VisionClaim(string Text, string Region, double Confidence);

/// <summary>视觉读图结果（调用方 VisionReader 经 ILlmClient ImageUrl 产出；fake claims 可注入测试）。</summary>
public sealed record VisionReadResult(IReadOnlyList<VisionClaim> Claims);

/// <summary>视觉歧义项：矛盾/不可读/无法归类的声称→人工纠偏清单（诚实：不静默、不猜）。</summary>
public sealed record VisionAmbiguous(string Claim, string Reason);

/// <summary>
/// 交叉验证产出：经矢量佐证的种子标注 + 歧义清单。
/// Fix Round 1：SeededClaims=本幅面产生种子的 claim 原文集——Scanner 汇总后据此
/// 抑制已验证 claim 在他幅的"无矢量佐证"假歧义（矛盾/无法归类保留）。
/// 末尾可选参 + null 归一为空（镜像 ScanResult 口径，legacy 两参构造兼容）。
/// </summary>
public sealed record CrossCheckResult(IReadOnlyList<SemanticAnnotation> Seeded, IReadOnlyList<VisionAmbiguous> Ambiguous,
    IReadOnlyList<string>? SeededClaims = null)
{
    // 位置 record 手写属性接管合成：构造参数喂入，null 归一为空清单。
    public IReadOnlyList<string> SeededClaims { get; init; } = SeededClaims ?? Array.Empty<string>();
}

/// <summary>
/// 视觉→矢量交叉验证（T6，幻觉拦截）。实验实证规律：可读分辨率→准确；
/// 不可读→先验幻觉带置信标签（日期 2023.06 vs 实 2018.10、YJV22 vs 实碳素波纹管，
/// 均被 DXF ground truth 证伪）。裁定：
/// ① claim.Confidence≥0.5 且 GT 吻合（归一 whitespace/全半角后精确或包含匹配）→
///    类推断 AttributeSemantics.ClassForAttributeValue("A", claim.Text) 非 Unknown 才种子；
///    否则取 GT 命中实体层约定类，再否则 claim 文本本身为约定层名；都不中→Ambiguous("无法归类")。
///    种子=Provenance.LlmInference conf 0.9，handle=GT 命中实体 HandlePath，SheetId=入参幅面。
///    Fix Round 1 包含短值守卫：包含通道要求短侧≥3 字符——单/双字符 GT（实图轴号 "1"/"A"
///    ubiquitous）与单字符 claim（"灯"）仅走精确通道，防包含误佐证披"已验证"外衣。
///    Fix Round 1 命中优先级：核心 Tag 属性(A/N/$TEXT$) > 图框属性 > 普通文字，
///    同 claim 多命中取优先级高者（同级保持文档序），非文档序首个。
/// ② claim 与同标签类 GT 矛盾（日期形/图号栏）→不种子+Ambiguous("与矢量事实矛盾: gt=值")。
/// ③ claim.Confidence&lt;0.5 或无 GT 命中→Ambiguous("不可读/无矢量佐证")。
/// GT 通道=幅面内（WalkedEntity.SheetId==sheetId，跨幅佐证拒绝）TextEntity/MText 值 +
/// Insert 属性 A/N/$TEXT$ 值 + 图框 Insert 全属性（图号/日期/比例等）。
/// 人工确认优先（与 seeder/LayerInference 同口径）：HumanConfirmed 句柄不再视觉种子。
/// 本类纯函数（只读 overlay 取人工确认集）；Upsert 由 Scanner 完成。
/// </summary>
public static class VisionCrossChecker
{
    // 无佐证歧义 reason（Scanner 多幅面抑制按本常量精确比对；矛盾/无法归类不受抑制）
    internal const string NoSupportReason = "不可读/无矢量佐证";

    // 日期形（2018.10 / 2023-06 / 2018年10月）：同标签类矛盾判定用
    private static readonly Regex DateShape = new(@"\d{4}\s*[.\-/年]\s*\d{1,2}", RegexOptions.Compiled);

    // GT 条目：值+归一值+证据来源+命中句柄+归一层名+属性 Tag（文字=null）
    // +命中优先级（0=核心 Tag 属性 A/N/$TEXT$，1=图框属性，2=普通文字；越小越优先）
    private sealed record GtEntry(string Value, string Norm, string Source, string HandlePath, string CanonicalLayer, string? Tag, int Priority);

    public static CrossCheckResult Check(VisionReadResult vision, SemanticOverlay overlay,
        IReadOnlyList<WalkedEntity> walked, string sheetId)
    {
        var gt = BuildGroundTruth(walked, sheetId);
        var humanConfirmed = overlay.Annotations
            .Where(a => a.Provenance == Provenance.HumanConfirmed)
            .Select(a => a.TargetHandle)
            .ToHashSet();

        var seeded = new Dictionary<string, SemanticAnnotation>();
        var seededClaims = new HashSet<string>(StringComparer.Ordinal);
        var ambiguous = new List<VisionAmbiguous>();
        foreach (var claim in vision.Claims)
        {
            string text = claim.Text ?? "";
            string norm = Normalize(text);
            // 低置信/空声称：不可读区域的先验幻觉，一律歧义清单（即使恰与 GT 同值也不种子）
            if (norm.Length == 0 || claim.Confidence < 0.5)
            {
                ambiguous.Add(new VisionAmbiguous(text, NoSupportReason));
                continue;
            }

            // 命中优先级（Fix Round 1）：核心 Tag 属性 > 图框属性 > 普通文字；
            // OrderBy 稳定排序——同级保持文档序（原 First 命中语义在同级内不变）。
            var hit = gt.Where(g => Matches(norm, g.Norm))
                .OrderBy(g => g.Priority)
                .FirstOrDefault();
            if (hit is not null)
            {
                // 类推断三路（裁定）：属性值语义 → 命中实体层约定 → claim 文本本身为约定层名
                var cls = AttributeSemantics.ClassForAttributeValue("A", text);
                if (cls == SemanticClass.Unknown) cls = ConventionLibrary.ClassForLayer(hit.CanonicalLayer);
                if (cls == SemanticClass.Unknown) cls = ConventionLibrary.ClassForLayer(text);
                if (cls == SemanticClass.Unknown)
                {
                    ambiguous.Add(new VisionAmbiguous(text, "无法归类"));
                    continue;
                }
                if (humanConfirmed.Contains(hit.HandlePath)) continue; // 人工确认优先，不产竞争推断
                seeded[hit.HandlePath] = new SemanticAnnotation(
                    hit.HandlePath, cls, Provenance.LlmInference, 0.9,
                    $"视觉声称经矢量交叉验证吻合: {hit.Source}", sheetId);
                seededClaims.Add(text); // 供 Scanner 多幅面抑制他幅"无矢量佐证"假歧义
                continue;
            }

            // 无 GT 命中：同标签类矛盾优先拦截（幻觉证伪），否则无矢量佐证
            var contradiction = FindContradiction(norm, claim, gt);
            ambiguous.Add(contradiction is not null
                ? new VisionAmbiguous(text, $"与矢量事实矛盾: gt={contradiction.Value}")
                : new VisionAmbiguous(text, NoSupportReason));
        }
        return new CrossCheckResult(seeded.Values.ToList(), ambiguous, seededClaims.ToList());
    }

    /// <summary>
    /// GT 通道（幅面作用域）：walked 中 SheetId==sheetId 的 TextEntity/MText 值 +
    /// Insert 属性 A/N/$TEXT$ 值；图框 Insert（IsFrameInsert 同 SheetRegistryBuilder 口径）
    /// 全属性入 GT（图号/日期/比例/图别等图签元数据）。文档序保持（同级优先级内 First命中=首个）。
    /// 优先级（Fix Round 1）：核心 Tag 属性=0，图框属性=1，普通文字=2。
    /// </summary>
    private static List<GtEntry> BuildGroundTruth(IReadOnlyList<WalkedEntity> walked, string sheetId)
    {
        var gt = new List<GtEntry>();
        foreach (var w in walked)
        {
            if (w.SheetId != sheetId) continue; // 跨幅佐证拒绝（T4 幅面作用域主题）
            string layer = w.Entity.Layer?.Name ?? "0";
            switch (w.Entity)
            {
                case TextEntity t when !string.IsNullOrWhiteSpace(t.Value):
                    gt.Add(new GtEntry(t.Value, Normalize(t.Value), $"文字 {t.Value} (层 {layer})", w.HandlePath, w.CanonicalLayer, null, 2));
                    break;
                case MText mt when !string.IsNullOrWhiteSpace(mt.Value): // MText : Entity，独立分支
                    gt.Add(new GtEntry(mt.Value, Normalize(mt.Value), $"文字 {mt.Value} (层 {layer})", w.HandlePath, w.CanonicalLayer, null, 2));
                    break;
                case Insert ins:
                    bool isFrame = SheetRegistryBuilder.IsFrameInsert(ins);
                    string blockName = ins.Block?.Name ?? "?";
                    foreach (var att in ins.Attributes)
                    {
                        if (string.IsNullOrWhiteSpace(att.Value)) continue;
                        bool coreTag = string.Equals(att.Tag, "A", StringComparison.OrdinalIgnoreCase)
                            || att.Tag is "N" or "$TEXT$";
                        if (!coreTag && !isFrame) continue; // 非图框 Insert 仅消费 A/N/$TEXT$
                        string source = isFrame && !coreTag
                            ? $"图框属性 {att.Tag}={att.Value}"
                            : $"块属性 {att.Tag}={att.Value} (块 {blockName})";
                        gt.Add(new GtEntry(att.Value, Normalize(att.Value), source, w.HandlePath, w.CanonicalLayer, att.Tag, coreTag ? 0 : 1));
                    }
                    break;
            }
        }
        return gt;
    }

    /// <summary>
    /// 归一后精确或包含匹配（双向：claim⊂GT 或 GT⊂claim；大小写不敏感）。
    /// Fix Round 1 短值守卫：包含通道要求短侧≥3 字符——单/双字符 GT（轴号 "1"/"2"/"A"）
    /// 与单/双字符 claim（"灯"）仅走精确通道，防 ubiquitous 短值包含误佐证。
    /// </summary>
    private static bool Matches(string claimNorm, string gtNorm)
    {
        if (string.Equals(claimNorm, gtNorm, StringComparison.OrdinalIgnoreCase)) return true;
        if (Math.Min(claimNorm.Length, gtNorm.Length) < 3) return false;
        return gtNorm.Contains(claimNorm, StringComparison.OrdinalIgnoreCase)
            || claimNorm.Contains(gtNorm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 同标签类矛盾判定（保守：仅日期形/图号栏两类有明确标签语义的声称）：
    /// 日期形 claim vs Tag=日期 或日期形 GT；region/文本提示图号 vs Tag=图号 GT。
    /// 值不同→矛盾（返回 GT 条目供证据）；无同标签类 GT→null（落回无矢量佐证）。
    /// </summary>
    private static GtEntry? FindContradiction(string claimNorm, VisionClaim claim, List<GtEntry> gt)
    {
        if (DateShape.IsMatch(claimNorm))
        {
            var dateGt = gt.FirstOrDefault(g => g.Tag == "日期" || DateShape.IsMatch(g.Norm));
            if (dateGt is not null && !string.Equals(dateGt.Norm, claimNorm, StringComparison.OrdinalIgnoreCase))
                return dateGt;
        }
        bool numberHint = (claim.Region ?? "").Contains("图号", StringComparison.OrdinalIgnoreCase)
            || claimNorm.Contains("图号", StringComparison.OrdinalIgnoreCase);
        if (numberHint)
        {
            var numGt = gt.FirstOrDefault(g => g.Tag == "图号");
            if (numGt is not null && !string.Equals(numGt.Norm, claimNorm, StringComparison.OrdinalIgnoreCase))
                return numGt;
        }
        return null;
    }

    /// <summary>
    /// 归一：全角→半角（U+FF01..FF5E 偏移 0xFEE0，U+3000→空格）+
    /// whitespace 折叠为单空格 + 首尾修剪。匹配前双方同口径归一。
    /// </summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char n = c >= '\uFF01' && c <= '\uFF5E' ? (char)(c - 0xFEE0) : c == '\u3000' ? ' ' : c;
            if (char.IsWhiteSpace(n))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else sb.Append(n);
        }
        return sb.ToString().Trim();
    }
}
