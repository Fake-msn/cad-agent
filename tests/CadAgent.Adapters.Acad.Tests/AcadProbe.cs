using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 真机可用性探针（Lazy 缓存，整套测试只探一次）。
/// 跳过原因四态（SkipReason 与 Available 同一次 Lazy 求值同步记录；测试先读 Available 再读 SkipReason）：
/// 1. ProgID 缺失 → 本机无 AutoCAD ProgID 注册；
/// 2. 激活抛异常（app 仍 null，本机孤儿注册：CLSID 无 LocalServer32）→ COM 激活失败，未启动进程；
/// 3. 激活成功后异常（首启模态框、RPC 断连等，app 非 null）→ 已 best-effort 收尾，须人工排查真机环境；
/// 4. 激活前守卫检出已有 acad 进程（CUIX 竞态守卫，等待超时才抛；本机无 acad 进程立即通过）
///    → 须关闭已有实例后重跑，不并发激活。
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

        // 激活前守卫（第四态）：已有 acad 进程→跳过而非并发激活（并发实例竞态损坏 CUIX）。
        // 须在 Activator.CreateInstance 之前、Lazy 求值内；守卫超时抛 CadRuntimeException。
        try
        {
            VendorGuard.EnsureNoForeignInstance(VendorProfiles.Acad);
        }
        catch (CadRuntimeException)
        {
            return (false, "已有 AutoCAD 实例运行，须关闭后重跑（CUIX 竞态守卫）");
        }

        dynamic? app = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            // 真机核定（2026-11-24，AutoCAD 2014）：激活返回≠初始化完成，启动窗口内
            // Visible=false 被拒（实测约 0.4s 后恢复）。拒绝有两种包装形态：
            // Invoke 层→COMException(0x80010001)；GetIDsOfNames 层→MissingMemberException
            // （消息含 HRESULT）。xunit 工作线程非 STA，消息过滤器不适用→双形态有界重试
            // （60×500ms=30s 上限，与 AcadRuntime.SetVisibleStartupReady 同款语义）。
            for (int i = 0; ; i++)
            {
                try { app!.Visible = false; break; }
                catch (Exception ex) when (IsStartupRejection(ex) && i < 59)
                {
                    Thread.Sleep(500);
                }
            }
            app!.Quit();
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

    /// <summary>四态跳过原因（与 Available 同一次 Lazy 求值记录；先读 Available 再读本属性）。</summary>
    public static string SkipReason => _probe.Value.Reason;

    // 启动窗口拒绝双形态判定（COMException HRESULT 直判；MissingMemberException 消息含 HRESULT）。
    private static bool IsStartupRejection(Exception ex) =>
        ex is COMException com
            ? com.HResult == unchecked((int)0x80010001) || com.HResult == unchecked((int)0x8001010A)
            : ex is MissingMemberException mm
              && (mm.Message.Contains("0x80010001") || mm.Message.Contains("0x8001010A"));
}
