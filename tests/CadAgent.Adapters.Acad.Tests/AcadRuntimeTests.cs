using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AcadRuntime 单元测试（无 COM）：ProgIdResolver/CreateComInstance seam 注入。
/// 本机事实：AutoCAD 孤儿 ProgID 注册（CLSID 无 LocalServer32）→ 激活必败 →
/// GetInfo 必须优雅返回 Available=false（不得抛挂 CLI）。
/// </summary>
public class AcadRuntimeTests : IDisposable
{
    private readonly Func<string, Type?> _originalResolver = AcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _originalCreate = AcadRuntime.CreateComInstance;
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;

    public AcadRuntimeTests()
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.ProgIdResolver = _originalResolver;
        AcadRuntime.CreateComInstance = _originalCreate;
        VendorGuard.ProcessNames = _originalNames;
    }

    [Fact]
    public void GetInfo_ProgIdMissing_ReturnsUnavailable()
    {
        AcadRuntime.ProgIdResolver = _ => null;
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.Equal("AutoCAD", info.Vendor);
        Assert.False(info.Available);
        Assert.Equal("", info.Version);
    }

    [Fact]
    public void GetInfo_ActivationThrows_ReturnsUnavailable()
    {
        // 孤儿注册场景：ProgID 在但 COM 服务器不存在
        AcadRuntime.ProgIdResolver = _ => typeof(object);
        AcadRuntime.CreateComInstance = _ => throw new COMException("没有注册类 (0x80040154)");
        using var rt = new AcadRuntime();
        var info = rt.GetInfo();
        Assert.False(info.Available);
    }

    [Fact]
    public void SaveAs_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() => rt.SaveAs("x.dwg"));
        Assert.Contains("无打开的图纸", ex.Message);
    }

    [Fact]
    public void EntityCount_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        Assert.Throws<CadRuntimeException>(() => rt.EntityCount());
    }

    [Fact]
    public void EnsureBlock_NoOpenDocument_ThrowsCadRuntime()
    {
        using var rt = new AcadRuntime();
        Assert.Throws<CadRuntimeException>(() => rt.EnsureBlock("B", Array.Empty<BlockPrimitive>()));
    }

    [Fact]
    public void Dispose_WaitsForAcadProcessExit()
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { "acad" } : Array.Empty<string>();
        var originalExit = VendorGuard.ExitWait;
        VendorGuard.ExitWait = TimeSpan.FromSeconds(5);
        try
        {
            using (var rt = new AcadRuntime()) { }
            Assert.True(calls >= 3);
        }
        finally { VendorGuard.ExitWait = originalExit; }
    }
}
