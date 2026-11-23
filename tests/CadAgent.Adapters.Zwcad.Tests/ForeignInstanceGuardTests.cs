using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// ZwcadRuntime.Dispose → VendorGuard 退出等待委托集成测试（seam 注入，不触 COM）。
/// 守卫行为本身见 Core.Tests/VendorGuardTests（双厂商参数化）。
/// </summary>
public class ForeignInstanceGuardTests : IDisposable
{
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;
    private readonly TimeSpan _originalExit = VendorGuard.ExitWait;

    public void Dispose()
    {
        VendorGuard.ProcessNames = _originalNames;
        VendorGuard.ExitWait = _originalExit;
    }

    [Fact]
    public void Dispose_WaitsForProcessExit()
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { "ZWCAD" } : Array.Empty<string>();
        VendorGuard.ExitWait = TimeSpan.FromSeconds(5);
        using (var rt = new ZwcadRuntime()) { }
        Assert.True(calls >= 3);
    }
}
