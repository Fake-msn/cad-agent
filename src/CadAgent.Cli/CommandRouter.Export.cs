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

    /// <summary>
    /// I1 fail-closed 闸指引（PR#24 修复波）：acad 拒绝 working 派生 DXF 时的统一 guidance。
    /// </summary>
    internal const string AcadWorkingDxfGuidance =
        "AutoCAD 2014 无法解析 ACadSharp 写入的 DXF（内容级不兼容，实证崩溃 0xE0000001）；请用默认 zwcad 运行时或 T3 转换件";

    /// <summary>
    /// I2 保真锚闸指引（微修复波）：working→DWG 快照转换丢实体（或读回失败）时拒绝交付。
    /// </summary>
    internal const string AcadSnapshotCountMismatchGuidance =
        "DWG 快照实体数与 working 源不一致（转换丢实体或读回失败），保真锚漂移拒绝交付；请用默认 zwcad 运行时或 T3 转换件";

    /// <summary>
    /// I1 fail-closed 闸（PR#24 修复波，单点化，镜像 GateRefusesRun 先例）：
    /// runtime=acad 且输入为 working 派生 DXF → 拒绝。export（ExportService.Open(sourceDxf)）
    /// 与 --live（事务前快照同源 working DXF→rt.Open(snap)）两条生产流均把 ACadSharp
    /// DxfWriter 产物喂厂商 COM Open——AutoCAD 2014 读取器内容级不兼容（实证崩溃
    /// 0xE0000001，spec §12 崩溃注记）；闸先于运行时创建（不激活 COM，拒绝路径无厂商
    /// 进程、无 CUIX 竞态暴露），拒绝+审计+guidance，exit 非零。zwcad 不受影响
    /// （读取器兼容 ACadSharp DXF，真机实证）。判定口径：
    /// ① .working.dxf 后缀（.dwg 源 sidecar；--live 快照保留源 basename）；
    /// ② 路径==DrawingSession.WorkingPathFor(源)（.dxf 源恒真——引擎 SaveWorking 原地
    ///    重写，任何 .dxf 输入均按 working 派生 fail-closed）；
    /// ③ --live 快照同源：basename 与 working 源一致且源即 working 文件。
    /// tracked 后续：working 文件兼容写出格式评估（根因未修，本闸为 interim fail-closed）。
    /// </summary>
    internal static bool AcadWorkingDxfGateRefuses(string inputPath, string sourcePath)
    {
        if (ResolveRuntimeName() != "acad") return false;
        if (inputPath.EndsWith(".working.dxf", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(Path.GetFullPath(inputPath),
                Path.GetFullPath(DrawingSession.WorkingPathFor(sourcePath)), StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(Path.GetFileName(inputPath), Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFullPath(sourcePath),
                Path.GetFullPath(DrawingSession.WorkingPathFor(sourcePath)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DWG 快照写出步 seam（微修复波 I1）：默认 ACadSharp DwgWriter.Write；Func 化供测试
    /// 注入"建目录后抛"（真实转换器已 CreateDirectory + 部分写出）钉住失败自清临时目录。
    /// </summary>
    internal static Action<Stream, ACadSharp.CadDocument> WriteAcadDwgSnapshot =
        (fs, doc) => ACadSharp.IO.DwgWriter.Write(fs, doc);

    /// <summary>
    /// DWG 快照转换器（解除 acad fail-closed 停用，用户批准设计）：working 派生 DXF →
    /// DxfReader 读入 → doc.Header.Version 钉 AC1027 → DwgWriter 写临时 .dwg → 返回快照路径。
    /// 探针实证（outputs/api-probe/compat-probe-report.md）：ACadSharp DwgWriter AC1027 产物
    /// AutoCAD 2014 可直接 COM Open（0.5s，CJK 保真）；真实标本 t3 往返零差异。
    /// **CJK 钉住 AC1027**：DwgWriter/DxfWriter 在 AC1015/AC1018 写中文有损（??????，本轮抽检实证），
    /// AC1027/AC1032 保真——故快照版本固定 AC1027（2014 可读上限，AC1032 默认会被 2014 拒）。
    /// 版本设置方式经 API 核实：DwgWriter 构造仅 (path/stream, CadDocument)，DwgWriterConfiguration
    /// 无 Version 属性——版本唯一来源是 doc.Header.Version（反射 + 往返实证）。
    /// 临时快照置于独立 GUID 子目录，调用方用后即清（Directory.Delete 整目录）；
    /// 建目录后写出失败则转换器内部自清（I1 微修复波：调用方拿不到路径，防 %TEMP% 孤儿目录）。
    /// Func 化供测试注入失败（fake DwgWriter 抛）钉住转换失败 fail-closed 安全侧。
    /// </summary>
    internal static Func<string, string> ConvertAcadDwgSnapshot = workingDxfPath =>
    {
        ACadSharp.CadDocument doc;
        using (var reader = new ACadSharp.IO.DxfReader(workingDxfPath)) doc = reader.Read();
        doc.Header.Version = ACadSharp.ACadVersion.AC1027; // CJK 钉住（AC1015/AC1018 有损）
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-acad-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string snapPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(workingDxfPath) + ".dwg");
            using (var fs = File.Create(snapPath)) WriteAcadDwgSnapshot(fs, doc);
            return snapPath;
        }
        catch
        {
            // I1（微修复波）：部分失败自清——CreateDirectory 后写出抛时调用方拿不到快照路径
            // （acadSnapDir 仍 null，外层 finally 清理不可达），不自清则 %TEMP% 孤儿 GUID
            // 目录+部分 .dwg 永不清理。best-effort 清理不掩盖原始异常；再上抛保持 fail-closed 拒绝。
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不二次抛 */ }
            throw;
        }
    };

    /// <summary>清理 DWG 快照临时目录（用后即清；best-effort，清理失败不影响交付/拒绝终态）。</summary>
    internal static void CleanupAcadDwgSnapshot(string? snapDir)
    {
        if (snapDir == null) return;
        try { Directory.Delete(snapDir, recursive: true); } catch { /* 临时目录清理失败不二次抛 */ }
    }

    /// <summary>
    /// 纯工厂 seam（不做可用性检查；构造不激活 COM）；Func 化供测试注入假 runtime
    /// （PR#18 修复波 Important#2：GetInfo 抛后 Dispose 回归钉住），调用语法不变。
    /// </summary>
    internal static Func<string, ICadRuntime> CreateRuntimeInstance = name => name switch
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

        // I1 闸口径升级（DWG 快照路径，解除 acad fail-closed 停用，用户批准设计）：
        // acad + working 派生 DXF → 先转换 DWG 快照（DxfReader→DwgWriter 钉 AC1027，探针实证
        // 2014 可直接 Open）→ 成功放行（ExportService.Open 喂 DWG）；转换失败仍 fail-closed
        // 拒绝+审计+guidance（安全侧保持，先于运行时创建不激活 COM）。非 working 派生输入
        // （原生 .dwg）闸 false → 不转换，直接 Open 不变。zwcad 路径零改动（闸 false）。
        string exportSource = drawing.SourcePath;
        string? acadSnapDir = null;
        bool acadWorking = AcadWorkingDxfGateRefuses(drawing.SourcePath, drawingPath);
        if (acadWorking)
        {
            try
            {
                exportSource = ConvertAcadDwgSnapshot(drawing.SourcePath);
                acadSnapDir = Path.GetDirectoryName(exportSource);
            }
            catch (Exception ex)
            {
                // 转换失败 → fail-closed 拒绝（保留安全侧）：复用既有 export-rejected kind
                // （params reason/guidance/conversionError），不产任何衍生产物，先于 COM 激活。
                string acadHash = drawing.ComputeHash();
                audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    session, "export", "export-rejected", drawingPath,
                    JsonSerializer.Serialize(new { reason = "acad-working-dxf", guidance = AcadWorkingDxfGuidance, conversionError = ex.Message }),
                    acadHash, acadHash, "", ""));
                stderr.WriteLine(AcadWorkingDxfGuidance);
                return 1;
            }
            // I2 保真锚校验（微修复波）：ExportService.Export 的 sourceCount 以快照为锚——
            // working→快照转换丢实体时 export 双闸口（源==导出）看不见（live 有影子计数兜底，
            // export 无）。Open 前校验 working↔快照实体计数相等；不等/读回失败（-1）→
            // fail-closed 拒绝+审计（reason=snapshot-count-mismatch）+guidance，先于运行时
            // 创建不激活 COM；拒绝路径临时快照亦清。
            int workingCount = CountEntities(drawing.SourcePath);
            int snapCount = CountEntities(exportSource);
            if (workingCount < 0 || snapCount < 0 || workingCount != snapCount)
            {
                CleanupAcadDwgSnapshot(acadSnapDir);
                string acadHash = drawing.ComputeHash();
                audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    session, "export", "export-rejected", drawingPath,
                    JsonSerializer.Serialize(new
                    {
                        reason = "snapshot-count-mismatch",
                        guidance = AcadSnapshotCountMismatchGuidance,
                        workingEntities = workingCount,
                        snapshotEntities = snapCount
                    }),
                    acadHash, acadHash, "", ""));
                stderr.WriteLine(AcadSnapshotCountMismatchGuidance);
                return 1;
            }
        }

        ICadRuntime? runtime = RuntimeFactory != null
            ? RuntimeFactory()
            : CreateRuntimeIfAvailable(stderr);
        try
        {
            // acad working 派生 + 无厂商（降级）：降级产物即 ACadSharp DXF（残余崩溃风险），
            // fail-closed 不旁路（镜像旧闸口径——acad 显式选择下降级交付同样拒绝）。
            if (acadWorking && runtime == null)
            {
                string acadHash = drawing.ComputeHash();
                audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                    session, "export", "export-rejected", drawingPath,
                    JsonSerializer.Serialize(new { reason = "acad-working-dxf", guidance = AcadWorkingDxfGuidance }),
                    acadHash, acadHash, "", ""));
                stderr.WriteLine(AcadWorkingDxfGuidance);
                return 1;
            }
            if (runtime != null)
                ExportGating(runtime, agentDir, session, audit, op, stderr);
            var svc = new ExportService(runtime, CountEntities);
            var result = svc.Export(exportSource, outPath, session, audit, op);
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
            CleanupAcadDwgSnapshot(acadSnapDir); // 临时 DWG 快照用后即清
        }
    }

    private static ICadRuntime? CreateRuntimeIfAvailable(TextWriter stderr)
    {
        var name = ResolveRuntimeName(); // 配置错误硬失败（用户显式选择的厂商不得静默降级）
        ICadRuntime? rt = null;
        try
        {
            rt = CreateRuntimeInstance(name);
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
            // 激活可能已成功但 GetInfo 抛（Visible/Version COM 失败）：必须释放已构造的 rt，
            // 否则孤儿厂商进程残留（CUIX 竞态源，违反守卫自身不变量）。
            rt?.Dispose();
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
