using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Core.Llm;
using CadAgent.Data;
using CadAgent.Data.Cad;

namespace CadAgent.Cli;

/// <summary>
/// 3j --live：计划级 COM 重放接线（"agent 操作 CAD 绘制"第二执行通道）。
/// tx 提交后从**事务前快照**开活厂商会话→重放计划→逐 step 审计→读回计数闸口
/// （vendor vs 影子）→SaveAs &lt;drawing&gt;.live.dwg 交付→com-replay-committed 审计。
/// 失败（无厂商/重放失败/分歧/异常）响亮：审计+stderr+exit 1（影子审计链不受影响）。
/// </summary>
public static partial class CommandRouter
{
    private static IReadOnlyList<SkillPackage> LivePackages(string[] args, string? primaryDir = null)
    {
        var dirs = new List<string>();
        if (primaryDir != null) dirs.Add(primaryDir);
        dirs.AddRange(GetOptionValues(args, "--skill"));
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(SkillLoader.LoadDirectory).ToList();
    }

    private static (bool Ok, string? OutPath, string? Error) LiveReplay(string[] args, string agentDir,
        string session, IAuditStore audit, OperatorIdentity op, DrawingSession drawing, StepPlan plan,
        IReadOnlyList<SkillPackage> packages, TextWriter stderr)
    {
        if (!args.Contains("--live")) return (true, null, null);
        void Audit(string kind, string target, object payload) =>
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                "live", kind, target, JsonSerializer.Serialize(payload), "", "", "", plan.PlanId));

        var lastTx = audit.ReadAll().LastOrDefault(r => r.Entry.Kind == "tx-commit");
        string snap = lastTx?.Entry.SnapshotRef ?? "";
        if (!File.Exists(snap))
        {
            Audit("com-replay-failed", plan.PlanId, new { error = "事务前快照缺失" });
            stderr.WriteLine("--live 失败: 事务前快照缺失");
            return (false, null, "事务前快照缺失");
        }
        // I1 fail-closed 闸（PR#24 修复波，与 export 共用 AcadWorkingDxfGateRefuses 单点）：
        // 事务前快照=working DXF 同源拷贝（SnapshotStore.Take 保留 basename）——acad 运行时
        // 打开即喂 ACadSharp 产物给 AutoCAD 2014（实证崩溃 0xE0000001）。先于运行时创建拒绝
        // （不激活 COM），审计 com-replay-failed + guidance，exit 非零；影子审计链不受影响。
        if (AcadWorkingDxfGateRefuses(snap, drawing.SourcePath))
        {
            Audit("com-replay-failed", plan.PlanId, new { reason = "acad-working-dxf", error = AcadWorkingDxfGuidance });
            stderr.WriteLine($"--live 失败: {AcadWorkingDxfGuidance}");
            return (false, null, AcadWorkingDxfGuidance);
        }
        ICadRuntime? rt = RuntimeFactory != null ? RuntimeFactory() : CreateRuntimeIfAvailable(stderr);
        if (rt == null)
        {
            Audit("com-replay-failed", plan.PlanId, new { error = "无可用厂商运行时" });
            stderr.WriteLine("--live 失败: 无可用厂商运行时（--live 需活 CAD 会话）");
            return (false, null, "无可用厂商运行时");
        }
        using (rt)
        {
            try
            {
                rt.Open(snap);
                var results = new ComReplayService(rt, packages).Replay(plan, drawing);
                foreach (var r in results)
                    Audit(r.Ok ? "com-replay" : "com-replay-failed", r.Kind, new { r.Kind, r.Error });
                var failed = results.FirstOrDefault(r => !r.Ok);
                if (failed != null)
                {
                    stderr.WriteLine($"--live 重放失败: {failed.Kind}: {failed.Error}");
                    return (false, null, failed.Error);
                }
                int vendorCount = rt.EntityCount();
                int shadowCount = drawing.Doc.Entities.Count();
                if (vendorCount != shadowCount)
                {
                    Audit("com-replay-divergence", plan.PlanId, new { vendorCount, shadowCount });
                    stderr.WriteLine($"--live 读回分歧: 厂商 {vendorCount} vs 影子 {shadowCount}");
                    return (false, null, "读回分歧");
                }
                string outPath = drawing.SourcePath + ".live.dwg";
                rt.SaveAs(outPath);
                rt.Close(false);
                Audit("com-replay-committed", plan.PlanId, new { outPath, vendorCount, shadowCount });
                return (true, outPath, null);
            }
            catch (Exception ex)
            {
                Audit("com-replay-failed", plan.PlanId, new { error = ex.Message });
                stderr.WriteLine($"--live 重放异常: {ex.Message}");
                return (false, null, ex.Message);
            }
        }
    }
}
