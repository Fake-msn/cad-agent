using System.IO.Compression;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 条件真机测试（spec §11：Verified 须真机证据）。
/// 探针不可用→SkippableFact 显式跳过（原因见 AcadProbe.SkipReason 三态，不是静默绿）。
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
        _dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _dwg = Path.Combine(_project, "ops.dwg");
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
        // EnsureBlock 走 BlocksAddNameFirst=true（AutoCAD 文档序）。
        // 若此处 COM 报错→真机核定签名序相反→翻 VendorProfiles.Acad 标志并重跑（协议文档步骤 4）。
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
