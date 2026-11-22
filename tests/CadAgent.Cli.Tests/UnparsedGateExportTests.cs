using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 防假 Green 切片 T3 Fix Round 2（R8）：export census-blind 缺口闭合。
/// Yellow/Red 图纸经 export 产衍生产物（DWG/DXF）会静默丢失天正内容（spec ⑥/保真锚）——
/// sidecar census 注入 Yellow → export 拒绝 exit 1 + export-rejected 留痕（既有 kind，
/// params level/unparsedCount/guidance）+ 不产任何衍生产物；闸先于厂商运行时创建。
/// 无 census Green legacy 路径逐字保持（含无厂商 DXF 降级）。
/// </summary>
public class UnparsedGateExportTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"unparsedexport-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public UnparsedGateExportTests()
    {
        Directory.CreateDirectory(_project);
        // Green 基线：普通 DXF（一条原生 Line，图内 Unknowns=0）。
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    /// <summary>sidecar 注入 census（TCH_WALL×2，源哈希匹配）→ Yellow（有原生+未解析）。</summary>
    private void InjectCensus() =>
        UnparsedCensusStore.Save(_project, _drawing,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private List<AuditRecord> AuditRecords()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    [Fact]
    public void Export_YellowUnparsed_RefusedExit1_NoArtifact_AuditExportRejected()
    {
        InjectCensus();
        // 厂商运行时若被创建则测试失败——闸必须先于 RuntimeFactory（不产衍生产物的前提）。
        bool runtimeCreated = false;
        CommandRouter.RuntimeFactory = () =>
        {
            runtimeCreated = true;
            throw new InvalidOperationException("厂商运行时不应被创建（准入闸须先于 export 交付路径）");
        };
        string outDwg = Path.Combine(_project, "out.dwg");
        byte[] before = File.ReadAllBytes(_drawing);

        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", outDwg);

        Assert.Equal(1, code);
        Assert.False(runtimeCreated); // 闸先于交付路径（vendor/降级均不可达）
        Assert.Contains("只读审查", stderr); // Yellow guidance
        Assert.False(File.Exists(outDwg)); // 无 DWG 衍生产物
        Assert.False(File.Exists(Path.Combine(_project, "out.dxf"))); // 无 DXF 降级衍生产物
        Assert.Equal(before, File.ReadAllBytes(_drawing)); // 源图未被触碰
        var records = AuditRecords();
        var rejected = Assert.Single(records, r => r.Entry.Kind == "export-rejected");
        Assert.Equal("export", rejected.Entry.Source);
        Assert.Equal(_drawing, rejected.Entry.Target);
        Assert.Equal(rejected.Entry.BeforeHash, rejected.Entry.AfterHash); // 拒绝不改图
        using (var p = JsonDocument.Parse(rejected.Entry.ParamsJson))
        {
            Assert.Equal("Yellow", p.RootElement.GetProperty("level").GetString());
            Assert.Equal(2, p.RootElement.GetProperty("unparsedCount").GetInt32());
            Assert.Contains("只读审查", p.RootElement.GetProperty("guidance").GetString());
        }
        Assert.DoesNotContain(records, r => r.Entry.Kind == "export-degraded");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "export-committed");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Export_NoCensus_GreenLegacy_DegradesToDxf()
    {
        // 无 sidecar → Green legacy：无厂商 DXF 降级路径逐字保持（显式风险审计）。
        CommandRouter.RuntimeFactory = () => null;
        string outDwg = Path.Combine(_project, "out.dwg");

        var (code, stdout, stderr) = Run("export", "--drawing", _drawing, "--out", outDwg);

        Assert.Equal(0, code);
        Assert.Contains("dxf-degraded", stdout);
        Assert.DoesNotContain("只读审查", stderr);
        Assert.True(File.Exists(Path.Combine(_project, "out.dxf")));
        var records = AuditRecords();
        Assert.Contains(records, r => r.Entry.Kind == "export-degraded");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "export-rejected");
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.True(audit.VerifyChain(out _));
    }
}
