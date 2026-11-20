using System.Security.Cryptography;
using System.Text;

namespace CadAgent.Core.Tests;

/// <summary>
/// 2b-ii-a T6：裁定指纹（复现证据）+ Load 去重（latest-wins）。
/// 指纹契约：Items 按 RuleId Ordinal 排序→规范化 JSON（固定字段序、枚举 PascalCase、
/// Ts "O"）→SHA256 大写 hex；空集→SHA256("[]" UTF8)；顺序无关、内容敏感。
/// </summary>
public class AdjudicationFingerprintTests
{
    private static Rule MakeRule(string id, RuleForce f) =>
        new(id, new RuleSource("GB/T 50001-2017", "5.0.2", "excerpt", "source-text"),
            "应", f, RuleSeverity.High, new RuleScope(), new CheckSpec("t", new()), "fix");

    [Fact]
    public void Fingerprint_EmptySet_Stable()
    {
        Assert.Equal(new AdjudicationSet().Fingerprint(), new AdjudicationSet().Fingerprint());
        Assert.Equal(64, new AdjudicationSet().Fingerprint().Length);
    }

    [Fact]
    public void Fingerprint_OrderIndependent()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R2", "u", DateTimeOffset.UnixEpoch, "r"));
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r"));
        b.Upsert(new Adjudication("R2", "u", DateTimeOffset.UnixEpoch, "r"));
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_ContentChange_Differs()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r1"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r2"));
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Load_DuplicateRuleId_LastWins()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"Items":[
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-01T00:00:00+00:00","Reason":"old","ForceOverride":null,"Exempt":false,"Interpretation":""},
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-02T00:00:00+00:00","Reason":"new","ForceOverride":"Mandatory","Exempt":false,"Interpretation":""}
            ]}
            """);
        try
        {
            var set = AdjudicationSet.Load(path);
            Assert.Single(set.Items);
            Assert.Equal("new", set.Items[0].Reason);
        }
        finally { File.Delete(path); }
    }

    // 空集指纹钉死为 SHA256("[]" UTF8) 大写 hex——跨实现/跨版本可复现的固定值。
    [Fact]
    public void Fingerprint_EmptySet_Sha256OfEmptyArray_UppercaseHex()
    {
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("[]")));
        string actual = new AdjudicationSet().Fingerprint();
        Assert.Equal(expected, actual);
        Assert.Matches("^[0-9A-F]{64}$", actual);
    }

    [Fact]
    public void Fingerprint_ExemptChange_Differs()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r", null, false));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r", null, true));
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_ForceOverrideChange_Differs()
    {
        var none = new AdjudicationSet();
        none.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r"));
        var mandatory = new AdjudicationSet();
        mandatory.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r", RuleForce.Mandatory));
        var recommended = new AdjudicationSet();
        recommended.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r", RuleForce.Recommended));
        Assert.NotEqual(none.Fingerprint(), mandatory.Fingerprint());
        Assert.NotEqual(mandatory.Fingerprint(), recommended.Fingerprint());
    }

    [Fact]
    public void Fingerprint_TsChange_Differs()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch.AddSeconds(1), "r"));
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_InterpretationChange_Differs()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r", null, false, "解读甲"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r", null, false, "解读乙"));
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_OperatorChange_Differs()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R1", "u1", DateTimeOffset.UnixEpoch, "r"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u2", DateTimeOffset.UnixEpoch, "r"));
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    // 去重修复的语义分歧：重复项首条 ForceOverride=Mandatory/Exempt=false、
    // 末条 Exempt=true——修复前 EffectiveForce（Find 首条）=Mandatory 而
    // IsExempt（Exists 任一条）=true 相互矛盾；修复后 latest-wins 二者一致（跳过）。
    [Fact]
    public void Load_Duplicates_EffectiveForceAndIsExempt_Agree()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"Items":[
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-01T00:00:00+00:00","Reason":"old","ForceOverride":"Mandatory","Exempt":false,"Interpretation":""},
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-02T00:00:00+00:00","Reason":"new","ForceOverride":null,"Exempt":true,"Interpretation":""}
            ]}
            """);
        try
        {
            var set = AdjudicationSet.Load(path);
            Assert.Single(set.Items);
            Assert.True(set.IsExempt("R1"));
            Assert.Null(set.EffectiveForce(MakeRule("R1", RuleForce.Recommended)));
        }
        finally { File.Delete(path); }
    }

    // 复现证据一致性：Load 去重后的指纹 == 以 Upsert 重放同序列构造的集合指纹。
    [Fact]
    public void Load_Dedup_Fingerprint_MatchesUpsertReplay()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"Items":[
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-01T00:00:00+00:00","Reason":"old","ForceOverride":null,"Exempt":false,"Interpretation":""},
              {"RuleId":"R2","Operator":"u","Ts":"2026-01-01T00:00:00+00:00","Reason":"keep","ForceOverride":null,"Exempt":false,"Interpretation":""},
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-02T00:00:00+00:00","Reason":"new","ForceOverride":"Mandatory","Exempt":false,"Interpretation":""}
            ]}
            """);
        try
        {
            var loaded = AdjudicationSet.Load(path);
            var replay = new AdjudicationSet();
            replay.Upsert(new Adjudication("R1", "u", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), "old"));
            replay.Upsert(new Adjudication("R2", "u", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), "keep"));
            replay.Upsert(new Adjudication("R1", "u", DateTimeOffset.Parse("2026-01-02T00:00:00+00:00"), "new", RuleForce.Mandatory));
            Assert.Equal(replay.Fingerprint(), loaded.Fingerprint());
        }
        finally { File.Delete(path); }
    }
}