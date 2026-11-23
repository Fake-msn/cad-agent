using System.IO.Compression;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 条件真机测试（spec §11：Verified 须真机证据）。
/// 探针不可用→SkippableFact 显式跳过（原因见 AcadProbe.SkipReason 四态，不是静默绿）。
/// 工作环境执行协议：docs/verification/autocad-adapter-protocol.md。
/// </summary>
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadLiveTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"acadops-{Guid.NewGuid():N}");
    private readonly string _dxf;
    private readonly string _dwg;

    public AcadLiveTests()
    {
        Directory.CreateDirectory(_project);
        _dxf = CreateMinimalR12Dxf();
        _dwg = Path.Combine(_project, "ops.dwg");
    }

    /// <summary>
    /// AutoCAD 专用手写最小 R12 DXF 夹具（真机核定 2026-11-24，AutoCAD 2014 R19.1）：
    /// ACadSharp 3.8.35 DxfWriter 输出被 AutoCAD 2014 读取器一律拒绝
    /// （Documents.Open 抛 0x80200157「解密数据时出错」）——与版本标签无关：
    /// ① 默认 AC1027 被拒；② doc.Header.Version=AC1015 降级仍被拒（$ACADVER 自检通过）；
    /// ③ 手写最小 AC1015（HEADER+TABLES+ENTITIES）亦被拒；
    /// ④ 手写最小 R12（仅 ENTITIES+LINE）真开（doc.Name/ActiveDocument 证实）；
    /// ⑤ AutoCAD 自带 SaveAs 的 AC1015/R12 DXF 同环境可开。
    /// → 毒在 ACadSharp 写入器内容而非版本标签；真机夹具绕开写入器，手写 R12 文本。
    /// 爆炸半径隔离：仅本测试类使用；共享 DxfFixture 默认行为不变（ZWCAD/Data/Cli/View 依赖）。
    /// 几何与 DxfFixture.AddSampleLine 相同（0,0→100,100 单 LINE，层 0）。
    /// 注：SaveAs 输出为 COM 原生 DWG（非 DXF），读回经 DwgReader，不受此限制。
    /// </summary>
    private static string CreateMinimalR12Dxf()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acadagent-live-{Guid.NewGuid():N}.dxf");
        // R12 DXF 最小合法形态：仅 ENTITIES 段 + EOF（CRLF 行尾，ASCII 路径）。
        string[] pairs =
        {
            "0", "SECTION", "2", "ENTITIES",
            "0", "LINE", "8", "0",
            "10", "0.0", "20", "0.0", "30", "0.0",
            "11", "100.0", "21", "100.0", "31", "0.0",
            "0", "ENDSEC", "0", "EOF",
        };
        File.WriteAllText(path, string.Join("\r\n", pairs) + "\r\n");
        return path;
    }

    public void Dispose()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(_project, true);
                File.Delete(_dxf);
                return;
            }
            catch (IOException) { Thread.Sleep(500); }
        }
    }

    [SkippableFact]
    public void GetInfo_LiveVendorSession_Available()
    {
        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.True(info.Available);
        Assert.Equal("AutoCAD", info.Vendor);
        Assert.False(string.IsNullOrEmpty(info.Version));
    }

    [SkippableFact]
    public void DrawingOps_LiveVendorSession_ReadBack()
    {
        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        using var rt = new AcadRuntime();
        rt.Open(_dxf);
        Assert.Equal(1, rt.EntityCount());
        rt.AddLine(0, 0, 100, 0);
        rt.AddText(50, 50, "生成式标注", 5);
        rt.AddCircle(20, 20, 7.5);
        Assert.Equal(4, rt.EntityCount());
        // EnsureBlock 走 BlocksAddNameFirst=false（2026-11-24 真机探针核定：AutoCAD 2014
        // 实测序=(origin, Name)，与 Autodesk 文档序相反，同 ZWCAD；g3 证据归档）。
        rt.EnsureBlock("SYM", new BlockPrimitive[]
        {
            new BlockCircle(0, 0, 1.5),
            new BlockLine(-2.5, 0, 2.5, 0),
            new BlockLine(0, -2.5, 0, 2.5),
        });
        rt.EnsureBlock("SYM", Array.Empty<BlockPrimitive>()); // 幂等
        rt.AddInsert("SYM", 60, 60);
        Assert.Equal(5, rt.EntityCount());
        rt.SaveAs(_dwg);
        rt.Close(false);

        using var fs = File.OpenRead(_dwg);
        var doc = new DwgReader(fs).Read();
        Assert.Equal(5, doc.Entities.Count());
        Assert.Contains(doc.Entities.OfType<TextEntity>(), t => t.Value == "生成式标注");
        Assert.Contains(doc.Entities.OfType<Insert>(), i => i.Block?.Name == "SYM");
        Assert.Contains(doc.Entities.OfType<Circle>(), c => Math.Abs(c.Radius - 7.5) < 1e-6);
    }

    [SkippableFact]
    public void OpenSaveAs_RoundTrip_EntityCountPreserved()
    {
        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        using var rt = new AcadRuntime();
        rt.Open(_dxf);
        rt.SaveAs(_dwg);
        rt.Close(false);
        using var fs = File.OpenRead(_dwg);
        var doc = new DwgReader(fs).Read();
        Assert.Equal(1, doc.Entities.Count());
    }

    [SkippableFact]
    public void UserProfileCuix_ZipIntact()
    {
        var files = CuixLocator.FindCuix(VendorProfiles.Acad);
        Skip.If(files.Count == 0, "本机无 AutoCAD 用户 profile CUIX，跳过金丝雀");
        foreach (var prof in files)
        {
            using var z = ZipFile.OpenRead(prof);
            Assert.True(z.Entries.Count > 0);
            foreach (var e in z.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms); // CRC 校验在读时触发
            }
        }
    }
}
