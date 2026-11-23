using CadAgent.Core.Cad;

namespace CadAgent.Core.Tests;

/// <summary>
/// 厂商实例守卫参数化测试（PR#17 CUIX 竞态修复泛化）：
/// ProcessNames seam 注入，不触真机 COM。双厂商等价类覆盖。
/// </summary>
public class VendorGuardTests : IDisposable
{
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;
    private readonly TimeSpan _originalWait = VendorGuard.GuardWait;
    private readonly TimeSpan _originalExit = VendorGuard.ExitWait;

    public void Dispose()
    {
        VendorGuard.ProcessNames = _originalNames;
        VendorGuard.GuardWait = _originalWait;
        VendorGuard.ExitWait = _originalExit;
    }

    public static IEnumerable<object[]> Profiles()
    {
        yield return new object[] { VendorProfiles.Zwcad };
        yield return new object[] { VendorProfiles.Acad };
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void NoInstance_ReturnsImmediately(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
        VendorGuard.EnsureNoForeignInstance(profile);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void ForeignInstancePresent_ThrowsWithCuixMessage(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => new[] { profile.ProcessName };
        VendorGuard.GuardWait = TimeSpan.FromMilliseconds(300);
        var ex = Assert.Throws<CadRuntimeException>(() => VendorGuard.EnsureNoForeignInstance(profile));
        Assert.Contains("CUIX", ex.Message);
        Assert.Contains("串行", ex.Message);
        Assert.Contains(profile.Vendor, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void ForeignInstanceExitsWithinWait_Proceeds(VendorProfile profile)
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { profile.ProcessName } : Array.Empty<string>();
        VendorGuard.GuardWait = TimeSpan.FromSeconds(5);
        VendorGuard.EnsureNoForeignInstance(profile);
        Assert.True(calls >= 3);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void CaseInsensitiveProcessName_Detected(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => new[] { profile.ProcessName.ToUpperInvariant() };
        VendorGuard.GuardWait = TimeSpan.FromMilliseconds(300);
        Assert.Throws<CadRuntimeException>(() => VendorGuard.EnsureNoForeignInstance(profile));
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void OtherVendorProcess_NotDetected(VendorProfile profile)
    {
        // 异厂商进程不触发守卫（ZWCAD 守卫不得拦 acad，反之亦然）
        var other = profile.Vendor == "ZWCAD" ? "acad" : "ZWCAD";
        VendorGuard.ProcessNames = () => new[] { other };
        VendorGuard.EnsureNoForeignInstance(profile);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void WaitForProcessExit_ClearsWithinWait_Returns(VendorProfile profile)
    {
        int calls = 0;
        VendorGuard.ProcessNames = () => ++calls <= 2 ? new[] { profile.ProcessName } : Array.Empty<string>();
        VendorGuard.WaitForProcessExit(profile, TimeSpan.FromSeconds(5));
        Assert.True(calls >= 3);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void WaitForProcessExit_Timeout_ReturnsSilently(VendorProfile profile)
    {
        VendorGuard.ProcessNames = () => new[] { profile.ProcessName };
        VendorGuard.WaitForProcessExit(profile, TimeSpan.FromMilliseconds(300));
    }
}
