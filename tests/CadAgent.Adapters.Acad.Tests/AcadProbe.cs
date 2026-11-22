using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 真机可用性探针（Lazy 缓存，整套测试只探一次）。
/// 跳过原因三态（SkipReason 与 Available 同一次 Lazy 求值同步记录；测试先读 Available 再读 SkipReason）：
/// 1. ProgID 缺失 → 本机无 AutoCAD ProgID 注册；
/// 2. 激活抛异常（app 仍 null，本机孤儿注册：CLSID 无 LocalServer32）→ COM 激活失败，未启动进程；
/// 3. 激活成功后异常（首启模态框、RPC 断连等，app 非 null）→ 已 best-effort 收尾，须人工排查真机环境。
/// 收尾契约：只要激活成功（app 非 null），无论探测成败均 Quit + 等待进程退出
/// （不留并发实例，CUIX 竞态守卫）；收尾自身失败不二次抛。
/// </summary>
internal static class AcadProbe
{
    private static readonly Lazy<(bool Ok, string Reason)> _probe = new(() =>
    {
        var type = Type.GetTypeFromProgID(VendorProfiles.Acad.ProgId);
        if (type == null)
        {
            return (false, "本机无 AutoCAD ProgID 注册，跳过真机测试");
        }

        dynamic? app = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            app.Visible = false;
            app.Quit();
            VendorGuard.WaitForProcessExit(VendorProfiles.Acad, TimeSpan.FromSeconds(20));
            return (true, string.Empty);
        }
        catch
        {
            if (app == null)
            {
                // 激活层即失败：未启动任何进程（孤儿注册无 LocalServer32），无需收尾。
                return (false, "本机 AutoCAD COM 激活失败（可能孤儿注册，无 LocalServer32），跳过真机测试");
            }

            // 激活成功后异常：best-effort 收尾，不留 acad.exe（探针文档契约"失败不留实例"）。
            // 本机无 AutoCAD，激活层即抛（app 仍 null），此分支无法真测——代码读核保障；
            // 真机首启模态框/RPC 断连等场景在此兜底。
            try { app.Quit(); } catch { /* 收尾失败不二次抛（RPC 已断连等） */ }
            try
            {
                // Quit 返回≠进程退出：CUIX 回写发生在退出尾段（PR#17 机制），等待闭合窗口。
                VendorGuard.WaitForProcessExit(VendorProfiles.Acad, TimeSpan.FromSeconds(20));
            }
            catch { /* 收尾失败不二次抛 */ }
            return (false, "AutoCAD 探针激活后异常（已 best-effort 收尾）；真机环境须人工排查后重跑");
        }
    });

    public static bool Available => _probe.Value.Ok;

    /// <summary>三态跳过原因（与 Available 同一次 Lazy 求值记录；先读 Available 再读本属性）。</summary>
    public static string SkipReason => _probe.Value.Reason;
}
