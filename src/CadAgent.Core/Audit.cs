using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CadAgent.Core;

public sealed record AuditEntry(
    DateTimeOffset Ts,
    string Operator,
    string Role,
    string Session,
    string Source,
    string Kind,
    string Target,
    string ParamsJson,
    string BeforeHash,
    string AfterHash,
    string SnapshotRef,
    string PlanRef);

public sealed record AuditRecord(long Seq, AuditEntry Entry, string PrevHash, string Hash);

public static class HashChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Canonical(AuditEntry e, long seq, string prevHash) =>
        string.Join("|", seq.ToString(CultureInfo.InvariantCulture), e.Ts.ToString("O"),
            Esc(e.Operator), Esc(e.Role), Esc(e.Session), Esc(e.Source), Esc(e.Kind),
            Esc(e.Target), Esc(e.ParamsJson), Esc(e.BeforeHash), Esc(e.AfterHash),
            Esc(e.SnapshotRef), Esc(e.PlanRef), prevHash);

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("|", "\\p");

    public static string Compute(AuditEntry e, long seq, string prevHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(e, seq, prevHash))));
}
