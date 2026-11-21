using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CadAgent.Core.Audit;

/// <summary>验锚结果（spec §12 外部锚约束）。</summary>
public enum AnchorStatus { Ok, Truncated, Forged, Missing }

/// <summary>
/// 审计链外部锚：DB 旁 anchor.json 持久化头哈希+序号+HMAC 签名。
/// 密钥 DPAPI CurrentUser 保护（本机威胁模型）——堵尾截断（DB head seq &lt; anchor seq）
/// 与无密钥全链重算伪造（HMAC 不符）。跨机密钥分发留后续（诚实注记）。
/// </summary>
public sealed class AnchorService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly byte[] _key;

    public AnchorService(string anchorDir)
    {
        _dir = anchorDir;
        Directory.CreateDirectory(_dir);
        string keyPath = Path.Combine(_dir, "anchor-key.bin");
        if (File.Exists(keyPath))
        {
            _key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            byte[] cipher = ProtectedData.Protect(_key, null, DataProtectionScope.CurrentUser);
            string tmp = keyPath + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, keyPath, overwrite: true);
        }
    }

    private string AnchorPath => Path.Combine(_dir, "anchor.json");

    public void Update(long seq, string headHash)
    {
        string signedAt = DateTimeOffset.UtcNow.ToString("O");
        string hmac = Sign(seq, headHash, signedAt);
        string json = JsonSerializer.Serialize(new AnchorDoc(seq, headHash, signedAt, hmac), JsonOpts) + "\n";
        string tmp = AnchorPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AnchorPath, overwrite: true);
    }

    public AnchorStatus Verify(long dbHeadSeq, string dbHeadHash)
    {
        if (!File.Exists(AnchorPath)) return AnchorStatus.Missing;
        AnchorDoc? doc;
        try { doc = JsonSerializer.Deserialize<AnchorDoc>(File.ReadAllText(AnchorPath), JsonOpts); }
        catch (JsonException) { return AnchorStatus.Forged; }
        if (doc == null) return AnchorStatus.Forged;
        // HMAC 校验（防 anchor 篡改/无密钥重算）
        string expect = Sign(doc.Seq, doc.HeadHash, doc.SignedAt);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expect), Encoding.UTF8.GetBytes(doc.Hmac)))
            return AnchorStatus.Forged;
        if (dbHeadSeq < doc.Seq) return AnchorStatus.Truncated;
        if (dbHeadSeq == doc.Seq && dbHeadHash != doc.HeadHash) return AnchorStatus.Forged;
        return AnchorStatus.Ok;
    }

    private string Sign(long seq, string headHash, string signedAt)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{seq}|{headHash}|{signedAt}")));
    }

    private sealed record AnchorDoc(long Seq, string HeadHash, string SignedAt, string Hmac);
}