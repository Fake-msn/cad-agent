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
    /// <summary>测试注入运行时工厂；null→生产运行时（CADAGENT_RUNTIME 选择，Available 检查）。</summary>
    internal static Func<ICadRuntime?>? RuntimeFactory;

    /// <summary>运行时选择 seam（默认读 CADAGENT_RUNTIME 环境变量；null→zwcad；空白/未知→硬错误）。</summary>
    internal static Func<string?> RuntimeSelector =
        () => Environment.GetEnvironmentVariable("CADAGENT_RUNTIME");

    internal static string ResolveRuntimeName()
    {
        var name = (RuntimeSelector() ?? "zwcad").Trim().ToLowerInvariant();
        if (name is not ("zwcad" or "acad"))
            throw new CadRuntimeException($"未知 CADAGENT_RUNTIME: {name}（支持: zwcad, acad）");
        return name;
    }

    /// <summary>纯工厂（不做可用性检查；构造不激活 COM）。</summary>
    internal static ICadRuntime CreateRuntimeInstance(string name) => name switch
    {
        "zwcad" => new CadAgent.Adapters.Zwcad.ZwcadRuntime(),
        "acad" => new CadAgent.Adapters.Acad.AcadRuntime(),
        _ => throw new CadRuntimeException($"未知运行时: {name}")
    };

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
            : CreateRuntimeIfAvailable(stderr);
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

    private static ICadRuntime? CreateRuntimeIfAvailable(TextWriter stderr)
    {
        var name = ResolveRuntimeName(); // 配置错误硬失败（用户显式选择的厂商不得静默降级）
        try
        {
            var rt = CreateRuntimeInstance(name);
            var info = rt.GetInfo();
            if (!info.Available)
            {
                stderr.WriteLine($"警示: 无可用厂商运行时（{name}），降级 DXF 交付（保真度未验证）");
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