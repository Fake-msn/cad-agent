using System.Runtime.InteropServices;
using System.Text;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// user32 顶层窗口枚举（spec 切片B §5）：NETLOAD 看门狗的"安全确认框检出"诊断增强。
/// 仅作进展/诊断信号，不改变阻塞与超时语义；枚举失败一律返回 false（看门狗按无进展处理）。
/// 单元测试不调用本实现（<c>AcadRuntime.SecurityDialogDetector</c> 默认指向此处，但测试注入 fake）。
/// Fix round 3：新增 <see cref="DismissDialogs"/>——按标题关闭**本方进程**的**信息性**模态框
/// （真机实证 /p 启动每次弹"不支持硬件加速"WPF 提示框；未关时 COM Gate1 间歇失败、Quit 后
/// 本方 acad.exe 变孤儿）。只对本 PID、只发 WM_CLOSE，绝不按键/点击/结束进程。
/// </summary>
internal static class AcadWindows
{
    private const int TitleBufferLength = 256;

    // WM_CLOSE（winuser.h）：与标题匹配的窗口收到即按"用户点关闭"路径处理（信息性提示框无副作用）。
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // 只投递 WM_CLOSE，不等待、不关线程（PostMessage 语义）；返回值忽略——窗口可能已消失。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>顶层窗口快照项：句柄 + 标题（同一序号，供关窗时定位）。</summary>
    internal readonly struct TopLevelWindow
    {
        public TopLevelWindow(IntPtr handle, string title)
        {
            Handle = handle;
            Title = title;
        }

        public IntPtr Handle { get; }

        public string Title { get; }
    }

    /// <summary>
    /// 该 PID 顶层窗口（句柄+标题）枚举缝（默认 user32 实枚举；单测注入 fake，
    /// 不得触碰真实窗口）。
    /// </summary>
    internal static Func<int, TopLevelWindow[]> WindowsOfProcess = DefaultWindowsOfProcess;

    /// <summary>
    /// 该 PID 顶层窗口标题枚举缝（默认由 <see cref="WindowsOfProcess"/> 派生——注入其一
    /// 即两缝同源同序；单测可据此观测"关窗节拍确实在 Gate1 期间跑过"）。
    /// </summary>
    internal static Func<int, string[]> WindowTitlesOfProcess = processId =>
        WindowsOfProcess(processId).Select(w => w.Title).ToArray();

    /// <summary>关窗动作缝（默认 PostMessage(WM_CLOSE)；单测注入记录器，绝不触真窗口）。</summary>
    internal static Action<IntPtr> CloseWindow = hWnd => PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 窗口所属进程 ID（F3 归属校验增强：句柄→PID；0=不可判定/窗口已销毁）。
    /// 直接回答"附着实例的窗口是不是本方 /p 进程的窗口"——比比对
    /// <c>Process.MainWindowHandle</c> 更精确（启动期该属性可能指向闪屏等非主框架窗口，
    /// 造成"本方实例被误判为他方"的伪失败）。
    /// </summary>
    internal static Func<int, int> WindowProcessId = DefaultWindowProcessId;

    private static int DefaultWindowProcessId(int windowHandle)
    {
        try
        {
            return GetWindowThreadProcessId(new IntPtr(windowHandle), out uint pid) == 0
                ? 0
                : (int)pid;
        }
        catch { return 0; }
    }

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

    /// <summary>
    /// 按标题关闭**指定进程**的顶层窗口（Fix round 3）：仅标题包含任一 needle
    /// （OrdinalIgnoreCase）的窗口收到 WM_CLOSE，返回实际投递个数。只关
    /// <paramref name="processId"/> 自己的窗口（绝不跨进程），绝不按键/点击。
    /// <paramref name="processId"/>&lt;=0 或 needles 为空→0（fail-safe：没有目标就不动窗口）；
    /// 句柄序列与标题序列长度不符→0（关闭不可撤销，宁可少关也不误关）。
    /// </summary>
    internal static int DismissDialogs(int processId, string[] titleNeedles)
    {
        if (processId <= 0 || titleNeedles == null || titleNeedles.Length == 0) return 0;
        try
        {
            string[] titles = WindowTitlesOfProcess(processId) ?? Array.Empty<string>();
            if (titles.Length == 0) return 0;
            TopLevelWindow[] windows = WindowsOfProcess(processId) ?? Array.Empty<TopLevelWindow>();
            if (windows.Length != titles.Length) return 0;   // 两次枚举不一致：不按下标猜句柄
            int closed = 0;
            for (int i = 0; i < titles.Length; i++)
            {
                if (!TitleMatchesAny(titles[i], titleNeedles)) continue;
                CloseWindow(windows[i].Handle);   // 只对命中标题的窗口投递 WM_CLOSE
                closed++;
            }
            return closed;
        }
        catch
        {
            // 枚举/投递异常（会话无桌面等）：按"未关任何窗口"处理，不影响 Gate1/退出主路径。
            return 0;
        }
    }

    /// <summary>标题包含任一 needle（OrdinalIgnoreCase，纯函数，单测可钉；空 needle 跳过）。</summary>
    internal static bool TitleMatchesAny(string? title, IReadOnlyList<string>? titleNeedles)
    {
        if (string.IsNullOrEmpty(title) || titleNeedles == null) return false;
        for (int i = 0; i < titleNeedles.Count; i++)
        {
            string? needle = titleNeedles[i];
            if (string.IsNullOrEmpty(needle)) continue;
            if (title!.Contains(needle!, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 默认窗口枚举：顶层窗口按 PID 过滤，取标题（标题为空也保留——保持与标题序列同序同数）。
    private static TopLevelWindow[] DefaultWindowsOfProcess(int processId)
    {
        var windows = new List<TopLevelWindow>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (GetWindowThreadProcessId(hWnd, out uint pid) == 0 || pid != (uint)processId)
                    return true;   // 非目标进程→继续枚举（绝不关他方窗口）
                var sb = new StringBuilder(TitleBufferLength);
                int len = GetWindowTextW(hWnd, sb, sb.Capacity);
                windows.Add(new TopLevelWindow(hWnd, len > 0 ? sb.ToString() : string.Empty));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return Array.Empty<TopLevelWindow>();
        }
        return windows.ToArray();
    }
}
