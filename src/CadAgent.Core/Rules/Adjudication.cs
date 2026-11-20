using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadAgent.Core;

/// <summary>人工裁定：强制力覆盖／豁免／解读。规则解释权始终在人；同一规则最新裁定生效。</summary>
/// <param name="RuleId">目标规则 Id。</param>
/// <param name="Operator">裁定人。</param>
/// <param name="Ts">裁定时间。</param>
/// <param name="Reason">裁定理由（必填，审计留痕）。</param>
/// <param name="ForceOverride">强制力覆盖；null 表示沿用规则自身 Force。</param>
/// <param name="Exempt">豁免：true 表示本图类不适用，检查时跳过。</param>
/// <param name="Interpretation">人工解读文本。</param>
public sealed record Adjudication(
    string RuleId,
    string Operator,
    DateTimeOffset Ts,
    string Reason,
    RuleForce? ForceOverride = null,
    bool Exempt = false,
    string Interpretation = "");

/// <summary>裁定集合：Load 缺失→空、同 RuleId 去重保留末条；Save 原子写；Upsert 同 RuleId 替换（最新裁定生效）；Fingerprint 复现证据。</summary>
public sealed class AdjudicationSet
{
    /// <summary>审计 kind 常量（Cli 记录裁定操作用）。</summary>
    public const string AuditKind = "rule-adjudicated";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<Adjudication> Items { get; set; } = new();

    public static AdjudicationSet Load(string path)
    {
        if (!File.Exists(path)) return new AdjudicationSet();
        var set = JsonSerializer.Deserialize<AdjudicationSet>(File.ReadAllText(path), JsonOpts)
                  ?? new AdjudicationSet();
        // 去重（2b-ii-a T6）：同 RuleId 多条→保留最后一条（latest-wins，与 Upsert 一致）。
        // 修复重复项下 EffectiveForce（Find 取首条）与 IsExempt（Exists 任一条）的语义分歧。
        // GroupBy 保首现键序、取末条内容——确定性且与 Upsert 重放等价。
        set.Items ??= new List<Adjudication>();
        set.Items = set.Items
            .GroupBy(x => x.RuleId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
        return set;
    }

    public void Save(string path)
    {
        // 原子写（沿用 SemanticOverlay 模式）：先写同目录临时文件再 Move 覆盖——
        // 写入中途崩溃时磁盘旧文件完整，人工裁定不丢。失败时 tmp 可能残留（可接受）。
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string json = JsonSerializer.Serialize(this, JsonOpts);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>同 RuleId 替换——最新裁定生效；否则追加。</summary>
    public void Upsert(Adjudication a)
    {
        int i = Items.FindIndex(x => x.RuleId == a.RuleId);
        if (i >= 0) Items[i] = a; else Items.Add(a);
    }

    /// <summary>生效强制力：豁免→null（跳过信号，调用方跳过该规则）；否则 ForceOverride ?? rule.Force。</summary>
    public RuleForce? EffectiveForce(Rule rule)
    {
        var a = Items.Find(x => x.RuleId == rule.Id);
        if (a is { Exempt: true }) return null;
        return a?.ForceOverride ?? rule.Force;
    }

    public bool IsExempt(string ruleId) => Items.Exists(x => x.RuleId == ruleId && x.Exempt);

    /// <summary>
    /// 裁定集指纹（2b-ii-a T6，复现证据）：Items 按 RuleId Ordinal 排序→规范化 JSON
    /// （匿名投影固定字段序 RuleId/Operator/Ts/Reason/ForceOverride/Exempt/Interpretation；
    /// Ts 往返格式 "O"；ForceOverride 为 PascalCase 枚举名或 null）→SHA256 大写 hex。
    /// 空集→SHA256("[]" UTF8)。与存储顺序无关、对任一字段内容敏感；
    /// 前提不变量：RuleId 唯一（Load 去重 + Upsert 保证）。
    /// </summary>
    public string Fingerprint()
    {
        var canonical = Items
            .OrderBy(x => x.RuleId, StringComparer.Ordinal)
            .Select(x => new
            {
                x.RuleId,
                x.Operator,
                Ts = x.Ts.ToString("O"),
                x.Reason,
                ForceOverride = x.ForceOverride?.ToString(),
                x.Exempt,
                x.Interpretation
            });
        string json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
