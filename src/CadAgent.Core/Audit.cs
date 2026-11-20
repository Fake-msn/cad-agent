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
        string.Join("|", seq, e.Ts.ToString("O"), e.Operator, e.Role, e.Session,
            e.Source, e.Kind, e.Target, e.ParamsJson, e.BeforeHash, e.AfterHash,
            e.SnapshotRef, e.PlanRef, prevHash);

    public static string Compute(AuditEntry e, long seq, string prevHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(e, seq, prevHash))));
}