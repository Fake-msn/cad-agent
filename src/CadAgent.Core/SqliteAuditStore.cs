using Microsoft.Data.Sqlite;

namespace CadAgent.Core;

public interface IAuditStore
{
    AuditRecord Append(AuditEntry e);
    IReadOnlyList<AuditRecord> ReadAll();
    bool VerifyChain(out string error);
}

public sealed class SqliteAuditStore : IAuditStore, IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public SqliteAuditStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS audit(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL, operator TEXT NOT NULL, role TEXT NOT NULL,
                session TEXT NOT NULL, source TEXT NOT NULL, kind TEXT NOT NULL,
                target TEXT NOT NULL, params_json TEXT NOT NULL,
                before_hash TEXT NOT NULL, after_hash TEXT NOT NULL,
                snapshot_ref TEXT NOT NULL, plan_ref TEXT NOT NULL,
                prev_hash TEXT NOT NULL, hash TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public AuditRecord Append(AuditEntry e)
    {
        lock (_writeLock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            long seq;
            string prevHash;
            using (var head = conn.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = "SELECT seq, hash FROM audit ORDER BY seq DESC LIMIT 1";
                using var r = head.ExecuteReader();
                if (r.Read()) { seq = r.GetInt64(0) + 1; prevHash = r.GetString(1); }
                else { seq = 1; prevHash = HashChain.GenesisHash; }
            }
            string hash = HashChain.Compute(e, seq, prevHash);
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO audit(seq, ts, operator, role, session, source, kind, target,
                        params_json, before_hash, after_hash, snapshot_ref, plan_ref, prev_hash, hash)
                    VALUES($seq,$ts,$op,$role,$session,$source,$kind,$target,
                        $params,$before,$after,$snap,$plan,$prev,$hash)
                    """;
                ins.Parameters.AddWithValue("$seq", seq);
                ins.Parameters.AddWithValue("$ts", e.Ts.ToString("O"));
                ins.Parameters.AddWithValue("$op", e.Operator);
                ins.Parameters.AddWithValue("$role", e.Role);
                ins.Parameters.AddWithValue("$session", e.Session);
                ins.Parameters.AddWithValue("$source", e.Source);
                ins.Parameters.AddWithValue("$kind", e.Kind);
                ins.Parameters.AddWithValue("$target", e.Target);
                ins.Parameters.AddWithValue("$params", e.ParamsJson);
                ins.Parameters.AddWithValue("$before", e.BeforeHash);
                ins.Parameters.AddWithValue("$after", e.AfterHash);
                ins.Parameters.AddWithValue("$snap", e.SnapshotRef);
                ins.Parameters.AddWithValue("$plan", e.PlanRef);
                ins.Parameters.AddWithValue("$prev", prevHash);
                ins.Parameters.AddWithValue("$hash", hash);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            return new AuditRecord(seq, e, prevHash, hash);
        }
    }

    public IReadOnlyList<AuditRecord> ReadAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM audit ORDER BY seq";
        using var r = cmd.ExecuteReader();
        var list = new List<AuditRecord>();
        while (r.Read()) list.Add(ReadRecord(r));
        return list;
    }

    private static AuditRecord ReadRecord(SqliteDataReader r)
    {
        var e = new AuditEntry(
            DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ts"))),
            r.GetString(r.GetOrdinal("operator")),
            r.GetString(r.GetOrdinal("role")),
            r.GetString(r.GetOrdinal("session")),
            r.GetString(r.GetOrdinal("source")),
            r.GetString(r.GetOrdinal("kind")),
            r.GetString(r.GetOrdinal("target")),
            r.GetString(r.GetOrdinal("params_json")),
            r.GetString(r.GetOrdinal("before_hash")),
            r.GetString(r.GetOrdinal("after_hash")),
            r.GetString(r.GetOrdinal("snapshot_ref")),
            r.GetString(r.GetOrdinal("plan_ref")));
        return new AuditRecord(r.GetInt64(r.GetOrdinal("seq")), e,
            r.GetString(r.GetOrdinal("prev_hash")), r.GetString(r.GetOrdinal("hash")));
    }

    public bool VerifyChain(out string error)
    {
        string prev = HashChain.GenesisHash;
        foreach (var rec in ReadAll())
        {
            if (rec.PrevHash != prev)
            { error = $"seq {rec.Seq}: prev_hash 断链"; return false; }
            string expect = HashChain.Compute(rec.Entry, rec.Seq, rec.PrevHash);
            if (rec.Hash != expect)
            { error = $"seq {rec.Seq}: 哈希不匹配（内容被篡改）"; return false; }
            prev = rec.Hash;
        }
        error = "";
        return true;
    }

    public void Dispose() { }
}