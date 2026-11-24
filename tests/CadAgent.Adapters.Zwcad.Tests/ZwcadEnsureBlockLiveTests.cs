using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// Zwcad EnsureBlock 对齐真机实证（ZWCAD 2025）：created-set 守卫 + 重入补全。
/// 中途失败以"不支持的块基元"确定性模拟（无需构造忙拒绝）：首基元落地后
/// CadRuntimeException→残留部分块；重入必须 Delete 残留+重建——真机核定 ZWCAD
/// 块 .Count/.Item(i)/.Delete() dynamic API（与 AutoCAD 同构，不赌文档）。
/// 预存块非破坏：SaveAs→Close→Open 换文档会话（created 集合清空）后同名块复用不 Delete。
/// 无 ZWCAD 环境：dotnet test --filter Category!=RequiresZwcad。
/// </summary>
[Trait("Category", "RequiresZwcad")]
[Collection("ZwcadLive")]
public class ZwcadEnsureBlockLiveTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"zwensure-{Guid.NewGuid():N}");
    private readonly string _dxf;

    public ZwcadEnsureBlockLiveTests()
    {
        Directory.CreateDirectory(_project);
        _dxf = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
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

    /// <summary>测试专用不支持基元：确定性触发 EnsureBlock 中途失败（首基元已落地→残留部分块）。</summary>
    private sealed record UnsupportedPrimitive : BlockPrimitive;

    [Fact]
    public void EnsureBlock_MidFailure_ReentryCompletes_RealVendor()
    {
        using var rt = new ZwcadRuntime();
        rt.Open(_dxf);
        var prims = new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new BlockLine(1, 1, 2, 2) };
        // 首基元落地后不支持基元→CadRuntimeException，残留 1/2 部分块（本方创建）。
        var ex = Assert.Throws<CadRuntimeException>(() => rt.EnsureBlock("SYM",
            new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new UnsupportedPrimitive() }));
        Assert.Contains("不支持的块基元", ex.Message);
        rt.EnsureBlock("SYM", prims);   // 重入补全：Delete 残留+重建（真机 .Count/.Item(i)/.Delete() 通道）
        rt.EnsureBlock("SYM", prims);   // 完整重入→复用幂等（不再 Delete/Add）
        rt.AddInsert("SYM", 60, 60);
        var dwg = Path.Combine(_project, "reentry.dwg");
        rt.SaveAs(dwg);
        rt.Close(false);

        using var fs = File.OpenRead(dwg);
        var doc = new DwgReader(fs).Read();
        var sym = doc.BlockRecords.First(b => b.Name.Equals("SYM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, sym.Entities.OfType<Line>().Count()); // 基元完整不重复（残留已清）
        Assert.Contains(doc.Entities.OfType<Insert>(), i => i.Block?.Name == "SYM");
    }

    [Fact]
    public void EnsureBlock_PreexistingUserBlock_ReusedNonDestructive_RealVendor()
    {
        using var rt = new ZwcadRuntime();
        rt.Open(_dxf);
        rt.EnsureBlock("USER", new BlockPrimitive[] { new BlockLine(0, 0, 9, 9) }); // 1 基元
        rt.AddInsert("USER", 10, 10); // 引用块定义，防保存期未引用清理干扰
        var dwg1 = Path.Combine(_project, "preexisting.dwg");
        rt.SaveAs(dwg1);
        rt.Close(false);
        rt.Open(dwg1);   // 换文档会话→created 集合清空：USER 成为新会话的预存块
        // count 1≠2 但非本方创建→复用早退，绝不 Delete 用户内容。
        rt.EnsureBlock("USER", new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new BlockLine(1, 1, 2, 2) });
        var dwg2 = Path.Combine(_project, "reused.dwg");
        rt.SaveAs(dwg2);
        rt.Close(false);

        using var fs = File.OpenRead(dwg2);
        var doc = new DwgReader(fs).Read();
        var user = doc.BlockRecords.First(b => b.Name.Equals("USER", StringComparison.OrdinalIgnoreCase));
        Assert.Single(user.Entities.OfType<Line>()); // 用户内容原样保留（1 基元，未被清除/追加）
    }
}
