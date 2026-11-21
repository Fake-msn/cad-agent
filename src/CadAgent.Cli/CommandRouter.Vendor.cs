using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Cli;

/// <summary>
/// vendor 命令（3c，spec §11）：厂商支持矩阵清单。Verified 须真机证据。
/// </summary>
public static partial class CommandRouter
{
    internal static string VendorMatrixPath => Path.Combine(AppContext.BaseDirectory, "vendor-support.json");

    private static int Vendor(string[] positional, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2 || positional[1] != "list")
        {
            stderr.WriteLine("用法: vendor list");
            return 1;
        }
        var matrix = VendorMatrix.Load(VendorMatrixPath);
        stdout.WriteLine(JsonSerializer.Serialize(new { entries = matrix.Entries }, SemanticJsonOpts));
        return 0;
    }

    /// <summary>导出 gating：未验证厂商版本→警示+审计注记（不阻止，决策权归人）。</summary>
    private static void ExportGating(ICadRuntime runtime, string agentDir, string session,
        IAuditStore audit, OperatorIdentity op, TextWriter stderr)
    {
        var info = runtime.GetInfo();
        var matrix = VendorMatrix.Load(VendorMatrixPath);
        var entry = matrix.Find(info.Vendor, info.Version);
        if (entry?.Status != VendorStatus.Verified)
        {
            stderr.WriteLine($"警示: {info.Vendor} {info.Version} 不在已验证矩阵（状态: {entry?.Status.ToString() ?? "未收录"}），导出保真度未经真机验证");
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                "export", "export-gating", $"{info.Vendor}/{info.Version}",
                JsonSerializer.Serialize(new { vendor = info.Vendor, version = info.Version, status = entry?.Status.ToString() ?? "unlisted" }),
                "", "", "", ""));
        }
    }
}