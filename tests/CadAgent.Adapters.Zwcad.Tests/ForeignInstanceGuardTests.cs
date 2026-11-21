using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// CUIX 竞态根因(b)守卫测试（2026-11-22 事故）：并发 ZWCAD 实例共享用户 profile
/// 会竞态损坏 ZWCAD.CUIX。守卫经 ProcessNames seam 注入，不触真机 COM。
/// </summary>
public class ForeignInstanceGuardTests : IDisposable
{
    private readonly Func<string[]> _originalNames = ZwcadRuntime.ProcessNames;
    private readonly TimeSpan _originalWait = ZwcadRuntime.GuardWait;

    public void Dispose()
    {
        ZwcadRuntime.ProcessNames = _originalNames;
        ZwcadRuntime.GuardWait = _originalWait;
    }

    [Fact]
    public void NoInstance_ReturnsImmediately()
    {
        ZwcadRuntime.ProcessNames = () => Array.Empty<string>();
        ZwcadRuntime.EnsureNoForeignInstance();
    }

    [Fact]
    public void ForeignInstancePresent_ThrowsWithCuixMessage()
    {
        ZwcadRuntime.ProcessNames = () => new[] { "ZWCAD" };
        ZwcadRuntime.GuardWait = TimeSpan.FromMilliseconds(300);
        var ex = Assert.Throws<CadRuntimeException>(() => ZwcadRuntime.EnsureNoForeignInstance());
        Assert.Contains("CUIX", ex.Message);
        Assert.Contains("串行", ex.Message);
    }

    [Fact]
    public void ForeignInstanceExitsWithinWait_Proceeds()
    {
        int calls = 0;
        ZwcadRuntime.ProcessNames = () => ++calls <= 2 ? new[] { "zwcad" } : Array.Empty<string>();
        ZwcadRuntime.GuardWait = TimeSpan.FromSeconds(5);
        ZwcadRuntime.EnsureNoForeignInstance();
        Assert.True(calls >= 3);
    }

    [Fact]
    public void CaseInsensitiveProcessName_Detected()
    {
        ZwcadRuntime.ProcessNames = () => new[] { "ZwCad" };
        ZwcadRuntime.GuardWait = TimeSpan.FromMilliseconds(300);
        Assert.Throws<CadRuntimeException>(() => ZwcadRuntime.EnsureNoForeignInstance());
    }

    [Fact]
    public void WaitForProcessExit_ClearsWithinWait_Returns()
    {
        int calls = 0;
        ZwcadRuntime.ProcessNames = () => ++calls <= 2 ? new[] { "ZWCAD" } : Array.Empty<string>();
        ZwcadRuntime.WaitForProcessExit(TimeSpan.FromSeconds(5));
        Assert.True(calls >= 3);
    }

    [Fact]
    public void WaitForProcessExit_Timeout_ReturnsSilently()
    {
        ZwcadRuntime.ProcessNames = () => new[] { "ZWCAD" };
        ZwcadRuntime.WaitForProcessExit(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void Dispose_WaitsForProcessExit()
    {
        int calls = 0;
        ZwcadRuntime.ProcessNames = () => ++calls <= 2 ? new[] { "ZWCAD" } : Array.Empty<string>();
        ZwcadRuntime.ExitWait = TimeSpan.FromSeconds(5);
        using (var rt = new ZwcadRuntime()) { }
        Assert.True(calls >= 3);
    }
}