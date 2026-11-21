using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;

namespace CadAgent.Cli;

/// <summary>
/// export 命令（3c，spec §3 保真双闸口）：厂商 SaveAs DWG + ACadSharp 读回计数闸口；
/// 无厂商→DXF 降级（显式风险审计）。RuntimeFactory 测试注入（null→降级路径）。
/// </summary>
public static partial class CommandRouter
{
    /// <summary>测试注入运行时工厂；null→生产 ZwcadRuntime（Available 检查）。</summary>
    internal static Func<ICadRuntime?>? RuntimeFactory;

    private static int Export(string[] args, string agentDir, string session, TextWriter stdout, TextWriter stderr)
    {
        string drawingPath = RequireOption(args, "--drawing");
        string outPath = RequireOption(args, "--out");
        using var audit = OpenAudit(agentDir);
        var op = (IdentityOverride ?? Identity(agentDir)).Current;
        var drawing = LoadOrReject(() => DrawingSession.Load(drawingPath), drawingPath, session, audit, op, stderr, source: "export");
        if (drawing == null) return 1;

        ICadRuntime? runtime = RuntimeFactory != null
            ? RuntimeFactory()
            : CreateZwcadIfAvailable(stderr);
        try
        {
            var svc = new ExportService(runtime, CountEntities);
            var result = svc.Export(drawing.SourcePath, outPath, session, audit, op);
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                mode = result.Mode,
                error = result.Error,
                sourceEntities = result.SourceEntities,
                exportedEntities = result.ExportedEntities
            }, SemanticJsonOpts));
            return result.Ok ? 0 : 1;
        }
        finally
        {
            runtime?.Dispose();
        }
    }

    private static ICadRuntime? CreateZwcadIfAvailable(TextWriter stderr)
    {
        try
        {
            var rt = new CadAgent.Adapters.Zwcad.ZwcadRuntime();
            var info = rt.GetInfo();
            if (!info.Available)
            {
                stderr.WriteLine("警示: 无可用厂商运行时，降级 DXF 交付（保真度未验证）");
                rt.Dispose();
                return null;
            }
            return rt;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"警示: 厂商运行时初始化失败（{ex.Message}），降级 DXF 交付");
            return null;
        }
    }

    private static int CountEntities(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dwg")
            {
                using var reader = new ACadSharp.IO.DwgReader(path);
                return reader.Read().Entities.Count();
            }
            return DrawingSession.Load(path).Summarize().Entities.Count;
        }
        catch
        {
            return -1; // 读回失败→计数不等→闸口拒绝
        }
    }
}