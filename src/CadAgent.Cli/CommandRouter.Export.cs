using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;

namespace CadAgent.Cli;

/// <summary>
/// export 命令（3c，spec §3 保真双闸口）：厂商 SaveAs DWG + ACadSharp 读回计数闸口；
/// 无厂商→DXF 降级（显式风险审计）。RuntimeFactory 测试注入（null→降级路径）。
/// 防假 Green（T3 R8）：加载接 census sidecar（LoadWithCensus）+ 准入闸——Yellow/Red
/// 图纸产衍生产物（DWG/DXF）会静默丢失天正内容（spec ⑥/保真锚），一律先于交付路径
/// 拒绝（export-rejected 既有 kind 留痕，不产任何衍生产物）；Green 路径逐字保持。
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
        var drawing = LoadOrReject(() => LoadWithCensus(drawingPath, agentDir), drawingPath, session, audit, op, stderr, source: "export");
        if (drawing == null) return 1;
        // 准入闸（T3 R8 缺口闭合）：export 此前 census-blind Load——Yellow/Red 经降级/厂商
        // 路径产衍生产物静默丢失天正内容。闸先于 RuntimeFactory（vendor/降级均不可达），
        // 拒绝留痕复用既有 export-rejected kind（params level/unparsedCount/guidance）。
        var gateReport = GateDetector.Inspect(drawing.Summarize(), new EmptySupportMatrix());
        if (GateRefusesRun(gateReport))
        {
            string gateHash = drawing.ComputeHash();
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "export", "export-rejected", drawingPath,
                JsonSerializer.Serialize(new
                {
                    level = gateReport.Level.ToString(),
                    unsupportedCount = gateReport.Unsupported.Count,
                    unparsedCount = drawing.Unparsed.TotalCount,
                    guidance = gateReport.Guidance
                }),
                gateHash, gateHash, "", ""));
            stderr.WriteLine(gateReport.Guidance);
            return 1;
        }

        ICadRuntime? runtime = RuntimeFactory != null
            ? RuntimeFactory()
            : CreateZwcadIfAvailable(stderr);
        try
        {
            if (runtime != null)
                ExportGating(runtime, agentDir, session, audit, op, stderr);
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

    /// <summary>
    /// audit package（3c，spec §5.5）：可独立复核的交付包——audit.db + snapshots +
    /// semantic/adjudications + anchor + manifest。**不含 llm 配置/密钥**。
    /// </summary>
    private static int AuditPackage(string[] args, string agentDir, TextWriter stdout, TextWriter stderr)
    {
        string outDir = RequireOption(args, "--out");
        Directory.CreateDirectory(outDir);
        using var audit = OpenAudit(agentDir);
        var all = audit.ReadAll();
        bool chainOk = audit.VerifyChain(out string chainError);
        long headSeq = all.Count > 0 ? all[^1].Seq : 0;
        string headHash = all.Count > 0 ? all[^1].Hash : "";
        var anchorStatus = new CadAgent.Core.Audit.AnchorService(agentDir).Verify(headSeq, headHash);

        var contents = new List<string>();
        void CopyFile(string name)
        {
            string src = Path.Combine(agentDir, name);
            if (File.Exists(src)) { File.Copy(src, Path.Combine(outDir, name), true); contents.Add(name); }
        }
        CopyFile("audit.db");
        CopyFile("audit.db-wal");
        CopyFile("audit.db-shm");
        CopyFile("anchor.json");
        CopyFile("semantic.json");
        CopyFile("adjudications.json");
        string snapDir = Path.Combine(agentDir, "snapshots");
        if (Directory.Exists(snapDir))
        {
            foreach (string f in Directory.GetFiles(snapDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(agentDir, f);
                string dest = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
                contents.Add(rel);
            }
        }
        // 密钥与 llm 配置绝不入包（anchor-key.bin 亦不入——跨机验锚需密钥分发，留后续）
        var manifest = new
        {
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            headSeq,
            headHash,
            recordCount = all.Count,
            chainVerified = chainOk,
            chainError = chainOk ? null : chainError,
            anchorStatus = anchorStatus.ToString(),
            contents
        };
        File.WriteAllText(Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, SemanticJsonOpts) + "\n");
        stdout.WriteLine(JsonSerializer.Serialize(new { package = outDir, manifest }, SemanticJsonOpts));
        return chainOk ? 0 : 1;
    }
}
