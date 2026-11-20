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

/// <summary>裁定集合：Load 缺失→空；Save 原子写；Upsert 同 RuleId 替换（最新裁定生效）。</summary>
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

    public static AdjudicationSet Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<AdjudicationSet>(File.ReadAllText(path), JsonOpts) ?? new AdjudicationSet()
            : new AdjudicationSet();

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
}