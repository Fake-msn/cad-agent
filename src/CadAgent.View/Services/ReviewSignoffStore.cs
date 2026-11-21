using System.Text.Json;
using System.Text.Json.Serialization;
using CadAgent.Core;

namespace CadAgent.View.Services;

/// <summary>
/// 审查签认记录（计划4 T7）：谁（Operator/Role）在何时（Ts）对哪个事务（TxId）
/// 给出通过/驳回（Approved）与意见（Comment）。同 TxId+Operator 最新一条生效
/// （覆盖签认），不同签认人各自留痕。
/// </summary>
public sealed record ReviewSignoff(string TxId, string Operator, Role Role, bool Approved, string Comment, DateTimeOffset Ts);

/// <summary>
/// 签认存储（计划4 T7）：.cadagent/signoffs.json。构造即 Load（缺失→空）；
/// Save 原子写（沿用 SemanticOverlay/AdjudicationSet 模式：同目录 tmp 写全量
/// 再 Move 覆盖——写入中途崩溃时磁盘旧文件完整，签认不丢；失败时 tmp 可能残留，
/// 可接受）。Upsert 仅改内存（同 TxId+Operator 替换，账号大小写不敏感），
/// 由调用方决定何时 Save。Load 对磁盘重复键去重保留末条（与 Upsert 重放等价）。
/// </summary>
public sealed class ReviewSignoffStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public ReviewSignoffStore(string path)
    {
        _path = path;
        Load();
    }

    public List<ReviewSignoff> Items { get; private set; } = new();

    /// <summary>从磁盘重读（缺失→空列表）；同 TxId+Operator 多条→保留末条。</summary>
    public void Load()
    {
        if (!File.Exists(_path))
        {
            Items = new List<ReviewSignoff>();
            return;
        }
        SignoffFile? file = JsonSerializer.Deserialize<SignoffFile>(File.ReadAllText(_path), JsonOpts);
        Items = (file?.Items ?? new List<ReviewSignoff>())
            .GroupBy(x => (x.TxId, Account: x.Operator.ToLowerInvariant()))
            .Select(g => g.Last())
            .ToList();
    }

    /// <summary>原子写：先写同目录临时文件再 Move 覆盖（目录不存在则创建）。</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string json = JsonSerializer.Serialize(new SignoffFile { Items = Items }, JsonOpts);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>同 TxId+Operator 替换（最新签认生效）；否则追加。仅内存，不落盘。</summary>
    public void Upsert(ReviewSignoff signoff)
    {
        int i = Items.FindIndex(x => x.TxId == signoff.TxId
            && string.Equals(x.Operator, signoff.Operator, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Items[i] = signoff; else Items.Add(signoff);
    }

    private sealed class SignoffFile
    {
        public List<ReviewSignoff> Items { get; set; } = new();
    }
}
