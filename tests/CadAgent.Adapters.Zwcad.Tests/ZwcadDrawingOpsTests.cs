using ACadSharp.IO;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// 3j T1 真机冒烟（ZWCAD 2025）：COM 绘制操作 AddLine/AddText/AddCircle/EnsureBlock/AddInsert/
/// EntityCount → SaveAs DWG → ACadSharp 读回断言（几何真正落在厂商会话）。
/// </summary>
[Trait("Category", "RequiresZwcad")]
[Collection("ZwcadLive")]
public class ZwcadDrawingOpsTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"zwops-{Guid.NewGuid():N}");
    private readonly string _dxf;
    private readonly string _dwg;

    public ZwcadDrawingOpsTests()
    {
        Directory.CreateDirectory(_project);
        _dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _dwg = Path.Combine(_project, "ops.dwg");
    }

    public void Dispose()
    {
        // ZWCAD 进程退出释放文件锁有延迟——重试删除（失败路径亦不残留锁冲突）
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

    [Fact]
    public void DrawingOps_LiveVendorSession_ReadBack()
    {
        using var rt = new ZwcadRuntime();
        rt.Open(_dxf);
        Assert.Equal(1, rt.EntityCount());
        rt.AddLine(0, 0, 100, 0);
        rt.AddText(50, 50, "生成式标注", 5);
        rt.AddCircle(20, 20, 7.5);
        Assert.Equal(4, rt.EntityCount());
        var symPrims = new BlockPrimitive[]
        {
            new BlockCircle(0, 0, 1.5),
            new BlockLine(-2.5, 0, 2.5, 0),
            new BlockLine(0, -2.5, 0, 2.5),
        };
        rt.EnsureBlock("SYM", symPrims);
        // 幂等（对齐 Acad 语义：同基元集 count 相等→复用；空集不再作幂等探针——
        // 本方创建块 count 不一致会走清除重建通道）。
        rt.EnsureBlock("SYM", symPrims);
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
}
