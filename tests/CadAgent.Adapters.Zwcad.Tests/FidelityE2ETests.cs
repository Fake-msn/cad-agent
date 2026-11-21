using ACadSharp.IO;
using CadAgent.Cli;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// 3c T7 真机保真 E2E（ZWCAD 2025）：DXF（线+文字+块）→export 厂商 SaveAs DWG→
/// ACadSharp 读回实体数/文本内容比对→审计 export-committed→审计包→验锚 Ok。
/// </summary>
[Trait("Category", "RequiresZwcad")]
public class FidelityE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"fidelity-{Guid.NewGuid():N}");
    private readonly string _dxf;
    private readonly string _dwg;
    public FidelityE2ETests()
    {
        Directory.CreateDirectory(_project);
        _dxf = DxfFixture.CreateTempDxf(doc =>
        {
            DxfFixture.AddSampleLine(doc);
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(5, 5), Value = "保真测试", Height = 5 });
            var block = new BlockRecord("LUM");
            block.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(10, 0) });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = (XYZ)new XY(50, 50) });
        });
        _dwg = Path.Combine(_project, "out.dwg");
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = null;
        Directory.Delete(_project, true);
        File.Delete(_dxf);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void LiveFidelity_Export_RoundTrip_AuditPackage_Anchor()
    {
        CommandRouter.RuntimeFactory = () => new ZwcadRuntime();
        var source = DrawingSession.Load(_dxf);
        int sourceCount = source.Summarize().Entities.Count;

        // 导出（真机 ZWCAD SaveAs）
        var (code, stdout, stderr) = Run("export", "--drawing", _dxf, "--out", _dwg);
        Assert.True(code == 0, $"export failed: {stdout} {stderr}");
        Assert.Contains("vendor", stdout);
        Assert.True(File.Exists(_dwg));

        // 读回比对：实体数 + 文本内容
        using var reader = new DwgReader(_dwg);
        var exported = reader.Read();
        Assert.Equal(sourceCount, exported.Entities.Count());
        Assert.Contains(exported.Entities.OfType<TextEntity>(), t => t.Value == "保真测试");

        // 审计 export-committed
        using (var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db")))
        {
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-committed" && r.Entry.ParamsJson.Contains("vendor"));
            Assert.True(audit.VerifyChain(out _));
        }

        // 审计包 + 验锚
        string pkg = Path.Combine(_project, "pkg");
        var (pc, _, _) = Run("audit", "package", "--out", pkg);
        Assert.Equal(0, pc);
        Assert.True(File.Exists(Path.Combine(pkg, "manifest.json")));
        var (vc, vout, _) = Run("audit", "verify", "--anchor");
        Assert.Equal(0, vc);
        Assert.Contains("外部锚校验通过", vout);

        // 矩阵：ZWCAD 2025 Verified（无 gating 警示）
        Assert.DoesNotContain("未经真机验证", stderr);
    }
}