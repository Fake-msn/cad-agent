using System.Runtime.InteropServices;
using System.Text;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// user32 顶层窗口枚举（spec 切片B §5）：NETLOAD 看门狗的"安全确认框检出"诊断增强。
/// 仅作进展/诊断信号，不改变阻塞与超时语义；枚举失败一律返回 false（看门狗按无进展处理）。
/// 单元测试不调用本实现（<c>AcadRuntime.SecurityDialogDetector</c> 默认指向此处，但测试注入 fake）。
/// </summary>
internal static class AcadWindows
{
    private const int TitleBufferLength = 256;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>
    /// 是否存在安全确认框：枚举顶层窗口，标题含"安全"或"Security"（大小写不敏感）即命中。
    /// <paramref name="processId"/>!=0 时只看该进程（AutoCAD）的窗口；0=全桌面（进程未知时兜底）。
    /// </summary>
    internal static bool HasSecurityDialog(int processId)
    {
        bool found = false;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (found) return false;
                if (processId != 0)
                {
                    if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid != (uint)processId)
                        return true;   // 非目标进程→继续枚举
                }
                var sb = new StringBuilder(TitleBufferLength);
                if (GetWindowTextW(hWnd, sb, sb.Capacity) <= 0) return true;
                string title = sb.ToString();
                if (IsSecurityTitle(title))
                {
                    found = true;
                    return false;   // 提前终止枚举
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // P/Invoke/枚举异常（会话无桌面等）：按"未检出"处理，不影响看门狗主路径。
            return false;
        }
        return found;
    }

    /// <summary>标题匹配（纯函数，单测可钉）：含"安全"或"Security"（OrdinalIgnoreCase）。</summary>
    internal static bool IsSecurityTitle(string? title) =>
        !string.IsNullOrEmpty(title)
        && (title.Contains("安全", StringComparison.Ordinal)
            || title.Contains("Security", StringComparison.OrdinalIgnoreCase));
}
