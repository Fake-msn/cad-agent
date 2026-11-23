using System.Diagnostics;

namespace CadAgent.Core.Cad;

/// <summary>
/// 厂商实例生命周期守卫（PR#17 CUIX 竞态修复泛化，2026-11-22 事故）：
/// 并发实例共享同一用户 profile → CUIX 加载/保存竞态 → 损坏 → 下次启动弹模态修复框
/// （挂起无人值守自动化）。启动前等待已有实例退出，超时拒绝；Quit 后等待进程退出
/// 闭合 CUIX 回写窗口。机制厂商无关，差异经 VendorProfile 参数化。
/// </summary>
public static class VendorGuard
{
    internal static Func<string[]> ProcessNames =
        () => Process.GetProcesses().Select(p => p.ProcessName).ToArray();
    internal static TimeSpan GuardWait = TimeSpan.FromSeconds(10);
    internal static TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    public static void EnsureNoForeignInstance(VendorProfile profile)
    {
        var deadline = DateTime.UtcNow + GuardWait;
        while (ProcessNames().Any(n => n.Equals(profile.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            if (DateTime.UtcNow >= deadline)
                throw new CadRuntimeException(
                    $"检测到已有 {profile.Vendor} 进程：并发实例共享用户配置会竞态损坏 CUIX。" +
                    $"请关闭已有 {profile.Vendor}（含人工会话）后重试；真机测试须串行运行。");
            Thread.Sleep(250);
        }
    }

    public static void WaitForProcessExit(VendorProfile profile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!ProcessNames().Any(n => n.Equals(profile.ProcessName, StringComparison.OrdinalIgnoreCase)))
                return;
            Thread.Sleep(250);
        }
    }
}
