using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.Llm;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 3d T4 布灯基准 E2E（第二期场景验证，离线 fake LLM）：
/// 建筑底图（墙体+房名+灯具块）→scan（Wall/Room 推断）→摘要含房间→人工 map 确认 Room→
/// agent 布灯（网格 Insert 落 E-LITE 层）→审查签认（职责分离）→降级导出→审计包+验锚。
/// 第二测试：审查驳回→事务回滚→灯具清零（要求③留痕撤回在布灯场景复验）。
/// </summary>
public class LightingBenchmarkE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"lightbench-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _skillCopy;

    public LightingBenchmarkE2ETests()
    {
        Directory.CreateDirectory(_project);
        _skillCopy = Path.Combine(_project, "lighting");
        CopyDirectory(LightingSkillDir(), _skillCopy);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("WALL"));
            var wall = doc.Layers.First(l => l.Name == "WALL");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(6000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 4000), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(0, 4000), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(6000, 0), EndPoint = (XYZ)new XY(6000, 4000), Layer = wall });
            doc.Layers.Add(new Layer("ROOM-NAME"));
            doc.Entities.Add(new TextEntity
            {
                InsertPoint = (XYZ)new XY(3000, 2000),
                Value = "客厅",
                Height = 5,
                Layer = doc.Layers.First(l => l.Name == "ROOM-NAME")
            });
            var block = new BlockRecord("LUM");
            block.Entities.Add(new Line { StartPoint = (XYZ)new XY(-100, 0), EndPoint = (XYZ)new XY(100, 0) });
            doc.BlockRecords.Add(block);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.LlmClientFactory = null;
        CommandRouter.IdentityOverride = null;
        CommandRouter.RuntimeFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string LightingSkillDir() =>
        Path.Combine(RepoRoot(), "examples", "skills", "lighting");

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (string sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }

    private sealed class LightingPlanClient : ILlmClient
    {
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(request.Messages[0].Content.Contains("验证智能体") ? "{\"verdict\":\"ok\"}" : "{\"steps\":[{\"kind\":\"layout-lighting\",\"params\":{\"x\":0,\"y\":0,\"width\":6000,\"depth\":4000,\"spacing\":2000,\"block\":\"LUM\",\"layer\":\"E-LITE\"}}]}", new TokenUsage(20, 8), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    private sealed class FixedId : IIdentityProvider
    {
        public FixedId(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private string LastTxId()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var tx = audit.ReadAll().Last(r => r.Entry.Kind == "tx-commit");
        string snap = tx.Entry.SnapshotRef;
        return Path.GetFileName(Path.GetDirectoryName(snap)!)!;
    }

    private void PrepareConfirmedRoom()
    {
        Assert.Equal(0, Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key", "sk-benchkey12345678").code);
        Assert.Equal(0, Run("llm", "use", "--name", "p").code);
        CommandRouter.LlmClientFactory = (p, k) => new LightingPlanClient();
        Assert.Equal(0, Run("semantic", "scan", "--drawing", _drawing, "--skill", _skillCopy).code);
        // 摘要含房间段（LLM 规划依据）：6000×4000 非 assumed（墙体 bbox 证据）
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        string summary = DrawingSummarizer.Summarize(DrawingSession.Load(_drawing), overlay);
        Assert.Contains("客厅@(3000,2000) 6000×4000mm", summary);
        Assert.DoesNotContain("[assumed]", summary);
        Assert.Equal(0, Run("semantic", "map", "--layer", "ROOM-NAME", "--class", "Room", "--drawing", _drawing).code);
    }

    [Fact]
    public void Benchmark_FullLoop_ScanConfirm_Place_Signoff_Export_Package()
    {
        PrepareConfirmedRoom();
        var (code, stdout, _) = Run("agent", "--drawing", _drawing, "--intent", "在客厅按2米间距布灯", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Executed", stdout);
        var inserts = DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>().ToList();
        Assert.Equal(6, inserts.Count);
        Assert.All(inserts, i => Assert.Equal("E-LITE", i.Layer?.Name));
        string txId = LastTxId();

        // 审查签认（职责分离：审查者≠绘制者）
        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var (ac, _, aErr) = Run("review", "signoff", "--drawing", _drawing, "--tx", txId, "--approve", "--comment", "布灯合格");
        Assert.Equal(0, ac);
        Assert.Empty(aErr);
        CommandRouter.IdentityOverride = null;

        // 降级导出（无厂商运行时→DXF 显式降级）
        CommandRouter.RuntimeFactory = () => null;
        string outDwg = Path.Combine(_project, "lighting-out.dwg");
        var (ec, eOut, _) = Run("export", "--drawing", _drawing, "--out", outDwg);
        Assert.Equal(0, ec);
        Assert.Contains("dxf-degraded", eOut);
        var exported = DrawingSession.Load(Path.Combine(_project, "lighting-out.dxf"));
        Assert.Equal(6, exported.Doc.Entities.OfType<Insert>().Count());

        // 审计包 + 验锚（第三方可独立复核）
        string pkgDir = Path.Combine(_project, "pkg");
        var (pc, _, pErr) = Run("audit", "package", "--out", pkgDir);
        Assert.Equal(0, pc);
        Assert.Empty(pErr);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pkgDir, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("chainVerified").GetBoolean());
        Assert.Equal("Ok", manifest.RootElement.GetProperty("anchorStatus").GetString());
        Assert.False(File.Exists(Path.Combine(pkgDir, "llm-profiles.json")));
        Assert.False(File.Exists(Path.Combine(pkgDir, "anchor-key.bin")));
        var (vc, vOut, _) = Run("audit", "verify", "--anchor");
        Assert.Equal(0, vc);
        Assert.Contains("外部锚校验通过", vOut);

        // 全链审计
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        foreach (string kind in new[] { "semantic-scan", "semantic-map", "llm-call", "tx-commit", "review-approved", "export-degraded" })
            Assert.Contains(all, r => r.Entry.Kind == kind);
        Assert.True(audit.VerifyChain(out _));
        Assert.All(all, r => Assert.DoesNotContain("sk-benchkey12345678", r.Entry.ParamsJson));
    }

    [Fact]
    public void Benchmark_ReviewReject_Rollback_LightsRemoved()
    {
        PrepareConfirmedRoom();
        Assert.Equal(0, Run("agent", "--drawing", _drawing, "--intent", "布灯", "--yes").code);
        Assert.Equal(6, DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>().Count());
        string txId = LastTxId();

        CommandRouter.IdentityOverride = new FixedId(new OperatorIdentity("TEST\\reviewer", Role.Review));
        var (rc, _, _) = Run("review", "signoff", "--drawing", _drawing, "--tx", txId, "--reject", "--comment", "间距需复核");
        Assert.Equal(0, rc);
        CommandRouter.IdentityOverride = null;

        Assert.Empty(DrawingSession.Load(_drawing).Doc.Entities.OfType<Insert>());
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "review-rejected");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "rollback");
        Assert.True(audit.VerifyChain(out _));
    }
}
