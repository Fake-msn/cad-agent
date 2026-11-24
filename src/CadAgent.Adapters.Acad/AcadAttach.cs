using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Acad;

/// <summary>天正 profile 感知附着（spec 切片A §4.1，spike 协议步骤2-3）。
/// AutoCAD 仅当启动参数含 /p "TElec20V5" 才加载天正 TArch——普通 COM 激活加载默认
/// profile（无 TArch）。故需从 ROT（Running Object Table）附着到已由 /p 拉起的实例。
/// P/Invoke 归属：CLSIDFromProgID 在 ole32、GetActiveObject 在 oleaut32
/// （标错 DLL→EntryPointNotFound，且异常易被上层静默吞——spike 实证）。</summary>
public static class AcadAttach
{
    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string progid, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    // 缝：测试注入替代真 COM 查询（单元测试不得触真机）；默认=ROT GetActiveObject 包装。
    internal static Func<string, object?> ActiveObjectResolver = progid =>
    {
        if (CLSIDFromProgID(progid, out var clsid) < 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) < 0 ? null : obj;
    };

    /// <summary>有界轮询附着：启动窗口内 ROT 尚未注册（/p 启动需数秒加载）→ null 后等 1s 重试。
    /// 天正加载慢于普通启动，默认 180 次（180s）覆盖冷启动；耗尽→响亮拒绝（CLI 降级）。</summary>
    internal static object AttachOnly(string progId, int maxAttempts = 180)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var app = ActiveObjectResolver(progId);
            if (app != null) return app;
            Thread.Sleep(1000);
        }
        throw new CadRuntimeException($"天正实例 ROT 附着超时（{maxAttempts}s）：{progId} 未就绪");
    }
}
