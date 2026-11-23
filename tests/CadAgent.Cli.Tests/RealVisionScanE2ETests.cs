using System.Text.Json;
using System.Text.Json.Nodes;
using CadAgent.Core;
using CadAgent.Data;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 视觉接线 T4：真实标本端到端（skip-gated，真实 DashScope 云端调用，用户已授权测试）。
/// 行使 T3 遗留缺口——relay 真进程路径（VisionScanSpawn=null → Process dotnet ViewDll，
/// Cli.Tests 输出目录含 View 及其依赖）：semantic scan --vision --confirm-egress →
/// 独立 View 进程 headless 全链（census Yellow 闸 → DPAPI 密钥 → 首次出境确认 →
/// 离屏 Skia 瓦片 → qwen3-vl-plus 读图 → 矢量交叉验证 → overlay/审计）。
/// 成本控制（T4 绑定约束 瓦片≤6/标本）：--max-tiles 6 瓦片预算（截断披露见 View 侧测试）。
/// profile+keys 从 outputs/view-closure/.cadagent 复制复用（DPAPI CurrentUser 同用户可解）；
/// 源 profile timeoutSeconds 已按用户裁定 600（2026-11-23），复制侧不再 override；
/// 不复制 cloud-consent.json——首次出境确认通道必须真实行使。
/// 密钥硬规则：stdout/审计全文 grep sk- 零命中；标本只读（副本字节前后一致，源目录零触碰）。
/// 缺标本/缺 profile/缺密钥 → 显式 Skipped 不假装。
/// </summary>
public class RealVisionScanE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"realvision-{Guid.NewGuid():N}");

    public RealVisionScanE2ETests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.VisionScanSpawn = null;
        try { Directory.Delete(_project, true); } catch (IOException) { } // 子进程文件锁残留不掩测试结论
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [SkippableFact]
    public void RealSpecimen_VisionScan_CloudChain_SeedsOrAmbiguous()
    {
        string root = RepoRoot();
        string specimenSrc = Path.Combine(root, "outputs", "tarch-native2", "1#配套用房0928.dwg");
        string profileSrc = Path.Combine(root, "outputs", "view-closure", ".cadagent", "llm-profiles.json");
        string keySrc = Path.Combine(root, "outputs", "view-closure", ".cadagent", "keys", "dashscope.key");
        Skip.If(!File.Exists(specimenSrc), "真实标本不在本机（仓库不携带），跳过");
        Skip.If(!File.Exists(profileSrc), "DashScope profile 不在本机，跳过");
        Skip.If(!File.Exists(keySrc), "DashScope DPAPI 密钥不在本机，跳过");

        // 标本副本（只读源零副作用：working/sidecar 全落临时项目）
        string specimen = Path.Combine(_project, Path.GetFileName(specimenSrc));
        File.Copy(specimenSrc, specimen);
        byte[] sourceBefore = File.ReadAllBytes(specimen);

        // profile 复制（源 profile timeoutSeconds 已按用户裁定 600，2026-11-23；不再 override）
        string agentDir = Path.Combine(_project, ".cadagent");
        Directory.CreateDirectory(agentDir);
        var profiles = JsonNode.Parse(File.ReadAllText(profileSrc))!;
        File.WriteAllText(Path.Combine(agentDir, "llm-profiles.json"),
            profiles.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // DPAPI 密钥文件复制复用（CurrentUser 同用户；密钥值永不入测试输出）
        Directory.CreateDirectory(Path.Combine(agentDir, "keys"));
        File.Copy(keySrc, Path.Combine(agentDir, "keys", "dashscope.key"));

        // relay 真进程路径：seam 必须为 null（生产 Process dotnet ViewDll）
        CommandRouter.VisionScanSpawn = null;
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(
            new[] { "semantic", "scan", "--vision", "--drawing", specimen,
                    "--confirm-egress", "--max-tiles", "6", "--project", _project },
            outW, errW);
        string stdout = outW.ToString(), stderr = errW.ToString();

        Assert.Equal(0, code); // Yellow 只读视觉审查 exit 0（relay 透传 View 退出码）
        Assert.NotEmpty(stdout); // 真进程 stdout 经 relay 转发（seam 模式无 stdout——真进程路径实证）
        int tiles, claims, seeded, ambiguousCount, promptTokens, completionTokens;
        using (var doc = JsonDocument.Parse(stdout))
        {
            var r = doc.RootElement;
            Assert.True(r.GetProperty("ok").GetBoolean());
            Assert.Equal("Yellow", r.GetProperty("level").GetString()); // census 3257 → Yellow（非假 Green）
            tiles = r.GetProperty("tiles").GetInt32();
            Assert.InRange(tiles, 1, 6); // 瓦片预算上限（成本控制绑定约束）
            claims = r.GetProperty("claims").GetInt32();
            seeded = r.GetProperty("seeded").GetInt32();
            ambiguousCount = r.GetProperty("ambiguous").GetArrayLength();
            promptTokens = r.GetProperty("usage").GetProperty("promptTokens").GetInt32();
            completionTokens = r.GetProperty("usage").GetProperty("completionTokens").GetInt32();
        }
        Assert.True(promptTokens > 0 && completionTokens > 0, "真实云端 usage 必须 >0");
        Assert.True(seeded > 0 || ambiguousCount > 0, "claims 必须落种子或歧义清单（不静默丢弃）");

        // 审计：llm-call 每瓦一条 + egress-consent + vision-scan + 链完整
        var records = new List<AuditRecord>();
        using (var audit = new SqliteAuditStore(Path.Combine(agentDir, "audit.db")))
        {
            records.AddRange(audit.ReadAll());
            Assert.True(audit.VerifyChain(out _));
        }
        var llmCalls = records.Where(r => r.Entry.Kind == "llm-call").ToList();
        Assert.Equal(tiles, llmCalls.Count);
        Assert.All(llmCalls, r =>
        {
            Assert.Equal("llm", r.Entry.Source);
            using var p = JsonDocument.Parse(r.Entry.ParamsJson);
            Assert.Equal("qwen3-vl-plus", p.RootElement.GetProperty("model").GetString());
            Assert.True(p.RootElement.GetProperty("PromptTokens").GetInt32() > 0);
        });
        Assert.Contains(records, r => r.Entry.Kind == "egress-consent");
        var scan = Assert.Single(records, r => r.Entry.Kind == "vision-scan");
        Assert.Equal(scan.Entry.BeforeHash, scan.Entry.AfterHash); // 只读扫描不改图
        Assert.True(File.Exists(Path.Combine(agentDir, "cloud-consent.json"))); // 首次确认持久化

        // overlay：有 seeded 才落盘（防空覆盖）；落盘则含 LlmInference 标注
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        if (seeded > 0)
        {
            var overlay = SemanticOverlay.Load(overlayPath);
            Assert.Contains(overlay.Annotations, a => a.Provenance == Provenance.LlmInference);
        }

        // 密钥零泄漏：stdout/stderr/审计全文/同意文件 sk- 零命中
        string auditText = string.Join("\n", records.Select(r => JsonSerializer.Serialize(r.Entry)));
        string consentText = File.ReadAllText(Path.Combine(agentDir, "cloud-consent.json"));
        Assert.DoesNotContain("sk-", stdout);
        Assert.DoesNotContain("sk-", stderr);
        Assert.DoesNotContain("sk-", auditText);
        Assert.DoesNotContain("sk-", consentText);

        // 标本副本只读（扫描零变更）；源目录文件未被触碰（仅读复制）
        Assert.Equal(sourceBefore, File.ReadAllBytes(specimen));
        Assert.True(File.Exists(DrawingSession.WorkingPathFor(specimen))); // working 物化在临时项目内
    }
}
