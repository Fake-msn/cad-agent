using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// DWG 快照路径真机闭环（用户批准设计 §4 skip-gated 真机）：acad export 读 working 派生 DXF
/// → 转换 DWG 快照（钉 AC1027）→ 真实 AutoCAD COM Open → SaveAs DWG → DwgReader 读回 CJK 保真。
/// 条件跑（AcadProbe 不可用→显式跳过，非静默绿）；AcadLive Collection 串行（不并发激活 COM）。
/// 经 CADAGENT_RUNTIME=acad 环境变量驱动生产路径（RuntimeFactory 默认 null→真实 AcadRuntime），
/// 用完即还原。CJK 夹具钉 AC1027（DxfWriter AC1015/AC1018 写中文有损，AC1027 保真，抽检实证）。
/// </summary>
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadExportSnapshotLiveTests
{
    private const string Cjk = "中文标注测试";

    [SkippableFact]
    public void Export_Acad_WorkingDxfSnapshot_RoundTripCjk()
    {
        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        string project = Path.Combine(Path.GetTempPath(), "acadexp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        string cjkDxf = CreateCjkWorkingDxf();
        string outDwg = Path.Combine(project, "out.dwg");
        string? prevRuntime = Environment.GetEnvironmentVariable("CADAGENT_RUNTIME");
        try
        {
            Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", "acad");
            var outW = new StringWriter(); var errW = new StringWriter();
            int code = CadAgent.Cli.CommandRouter.Run(
                new[] { "export", "--drawing", cjkDxf, "--out", outDwg, "--project", project }, outW, errW);
            Assert.True(code == 0, $"code={code} out={outW} err={errW}");
            Assert.True(File.Exists(outDwg));
            // 读回交付 DWG：CJK 文本保真 + 实体数（快照转换不丢实体）。
            using var fs = File.OpenRead(outDwg);
            var doc = new DwgReader(fs).Read();
            Assert.Contains(doc.Entities.OfType<TextEntity>(), t => t.Value == Cjk);
            Assert.Single(doc.Entities);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", prevRuntime);
            File.Delete(cjkDxf);
            try { Directory.Delete(project, true); } catch { /* 真机文件占用 best-effort 清理 */ }
        }
    }

    /// <summary>CJK working DXF 夹具（钉 AC1027，DxfWriter 保 CJK；AC1015/AC1018 有损见类注）。</summary>
    private static string CreateCjkWorkingDxf()
    {
        var doc = new CadDocument();
        doc.Header.Version = ACadVersion.AC1027;
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 10), Height = 5, Value = Cjk });
        string path = Path.Combine(Path.GetTempPath(), "acadexp-cjk-" + Guid.NewGuid().ToString("N") + ".dxf");
        using var fs = File.Create(path);
        DxfWriter.Write(fs, doc);
        return path;
    }
}
