using System.Text.Json;

namespace CadAgent.Core.Cad;

/// <summary>导出结果：Mode=vendor（厂商保真）/dxf-degraded（无厂商降级，显式风险）。</summary>
public sealed record ExportResult(bool Ok, string Mode, string? Error, int SourceEntities, int ExportedEntities);

/// <summary>
/// 保真导出（spec §3 保真双闸口）：厂商 Open→SaveAs DWG→读回计数比对→过闸交付；
/// 不过闸→拒绝+审计；COM 崩溃→Restart 一次重试；无厂商→DXF 交付+降级审计（不假装等价）。
/// 实体计数经 Func 注入（Core 不引用 ACadSharp；Data/Cli 提供读回计数）。
/// </summary>
public sealed class ExportService
{
    private readonly ICadRuntime? _runtime;
    private readonly Func<string, int> _countEntities;

    public ExportService(ICadRuntime? runtime, Func<string, int> countEntities)
    {
        _runtime = runtime;
        _countEntities = countEntities;
    }

    public ExportResult Export(string sourceDxf, string outDwg, string sessionId,
        IAuditStore audit, OperatorIdentity op)
    {
        // 保真锚口径（I2 微修复波注记）：sourceCount 以传入源为锚——acad working 派生路径
        // 传入的是 DWG 快照，Cli 侧（CommandRouter.Export）已在 Open 前校验 working↔快照
        // 实体计数相等（不等→snapshot-count-mismatch 拒绝），故快照锚==working 锚不漂移。
        int sourceCount = _countEntities(sourceDxf);
        if (_runtime == null)
        {
            string dxfOut = Path.ChangeExtension(outDwg, ".dxf");
            File.Copy(sourceDxf, dxfOut, overwrite: true);
            Audit(audit, op, sessionId, "export-degraded", sourceDxf,
                new { mode = "dxf-degraded", risk = "无厂商运行时，DXF 交付，保真度未验证", sourceEntities = sourceCount });
            return new ExportResult(true, "dxf-degraded", null, sourceCount, sourceCount);
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                _runtime.Open(sourceDxf);
                _runtime.SaveAs(outDwg);
                _runtime.Close(false);
                int exportedCount = _countEntities(outDwg);
                if (exportedCount != sourceCount)
                {
                    Audit(audit, op, sessionId, "export-rejected", sourceDxf,
                        new { mode = "vendor", sourceEntities = sourceCount, exportedEntities = exportedCount });
                    return new ExportResult(false, "vendor", $"往返校验不过闸：源 {sourceCount} 实体，导出 {exportedCount}", sourceCount, exportedCount);
                }
                Audit(audit, op, sessionId, "export-committed", sourceDxf,
                    new { mode = "vendor", sourceEntities = sourceCount, exportedEntities = exportedCount });
                return new ExportResult(true, "vendor", null, sourceCount, exportedCount);
            }
            catch (CadRuntimeException ex)
            {
                if (attempt == 1)
                {
                    try { _runtime.Restart(); } catch { /* 重启失败走第二轮报错 */ }
                    continue;
                }
                Audit(audit, op, sessionId, "export-failed", sourceDxf, new { mode = "vendor", error = ex.Message });
                return new ExportResult(false, "vendor", ex.Message, sourceCount, 0);
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    private static readonly JsonSerializerOptions AuditJsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static void Audit(IAuditStore audit, OperatorIdentity op, string sessionId,
        string kind, string target, object payload) =>
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), sessionId,
            "export", kind, target, JsonSerializer.Serialize(payload, AuditJsonOpts), "", "", "", ""));
}
