using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;

namespace CadAgent.Cli;

/// <summary>
/// review 命令族（3b-ii，§4.1⑥/§5.3）：签认须 Review 角色；职责分离（签认人≠事务执行人，
/// 经审计 SnapshotRef 含 txId 溯源）；防重复签认；驳回→既有快照回滚（审计 rollback）。
/// IdentityOverride 仅测试注入。
/// </summary>
public static partial class CommandRouter
{
    /// <summary>测试注入身份（review/repl 消费）；null→Windows 当前账号。</summary>
    internal static IIdentityProvider? IdentityOverride;

    private static int Review(string[] args, string[] positional, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2 || positional[1] != "signoff")
        {
            stderr.WriteLine("用法: review signoff --drawing <file> --tx <txId> (--approve|--reject) [--comment <text>]");
            return 1;
        }
        string drawingPath = RequireOption(args, "--drawing");
        string txId = RequireOption(args, "--tx");
        bool approve = args.Contains("--approve");
        bool reject = args.Contains("--reject");
        if (approve == reject) { stderr.WriteLine("--approve 与 --reject 必须二选一"); return 1; }
        string comment = GetOption(args, "--comment") ?? "";

        var op = (IdentityOverride ?? Identity(agentDir)).Current;
        try { new RoleChecker().Ensure(op, Role.Review); }
        catch (RoleViolationException ex) { stderr.WriteLine(ex.Message); return 1; }

        using var audit = OpenAudit(agentDir);
        var all = audit.ReadAll();
        // 溯源：SnapshotRef 路径含 txId（SnapshotStore.Take: {root}/{txId}/{file}）
        var txRecords = all.Where(r => r.Entry.SnapshotRef.Contains(txId, StringComparison.Ordinal)).ToList();
        if (txRecords.Count == 0) { stderr.WriteLine($"事务不存在: {txId}"); return 1; }
        string executor = txRecords[0].Entry.Operator;
        if (string.Equals(op.Account, executor, StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("职责分离：绘制者不能自审自批");
            return 1;
        }
        if (all.Any(r => (r.Entry.Kind == "review-approved" || r.Entry.Kind == "review-rejected")
                && r.Entry.ParamsJson.Contains(txId, StringComparison.Ordinal)))
        {
            stderr.WriteLine($"事务已签认，不可重复: {txId}");
            return 1;
        }

        string payload = JsonSerializer.Serialize(new { txId, comment, signer = op.Account });
        if (approve)
        {
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                "review", "review-approved", txId, payload, "", "", "", ""));
            stdout.WriteLine($"已批准事务 {txId}");
            return 0;
        }
        // 驳回：先留痕，再回滚（既有快照机制，rollback 自身亦留痕）
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
            "review", "review-rejected", txId, payload, "", "", "", ""));
        // T5 顺手修②：裸 Load → LoadWithCensus——与 run/rollback 同一 persisted census
        // 直载口径（sidecar 有效时不重读源）；Yellow 上驳回回滚保持 R7 撤回处置豁免。
        var drawing = LoadOrReject(() => LoadWithCensus(drawingPath, agentDir), drawingPath, session, audit, op, stderr, source: "review");
        if (drawing == null) return 1;
        Engine(agentDir, audit).Rollback(txId, drawing, session);
        drawing.Reload();
        stdout.WriteLine($"已驳回事务 {txId} 并回滚图纸");
        return 0;
    }
}
