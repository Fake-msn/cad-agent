using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// AutoCAD 真机可用性探针（Lazy 缓存，整套测试只探一次）。
/// 本机孤儿注册：ProgID 在但激活抛 COMException → false → 真机测试 SkippableFact 跳过。
/// 真机环境：激活成功→立即 Quit+等待进程退出（不留并发实例，CUIX 竞态守卫）。
/// </summary>
internal static class AcadProbe
{
    private static readonly Lazy<bool> _available = new(() =>
    {
        var type = Type.GetTypeFromProgID(VendorProfiles.Acad.ProgId);
        if (type == null) return false;
        try
        {
            dynamic app = Activator.CreateInstance(type)!;
            app.Visible = false;
            app.Quit();
            VendorGuard.WaitForProcessExit(VendorProfiles.Acad, TimeSpan.FromSeconds(20));
            return true;
        }
        catch
        {
            return false;
        }
    });

    public static bool Available => _available.Value;
}
