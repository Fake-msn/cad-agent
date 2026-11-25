using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.TArchBridge.Protocol;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// AutoCAD COM 适配器（spec §3/§11）：dynamic COM（ProgID AutoCAD.Application
/// 版本无关→CurVer；不编译期引用厂商 interop DLL）；所有 COM 调用经专用 STA 线程
/// 任务队列串行化；崩溃→CadRuntimeException，Restart 重建。
/// 状态：CompatibleUnverified——开发机无 AutoCAD（孤儿注册），真机验证按
/// docs/verification/autocad-adapter-protocol.md 在工作环境执行。
/// 与 ZwcadRuntime 同源结构；差异经 VendorProfiles.Acad 参数化
/// （Blocks.Add 实测序 origin 先，2026-11-24 真机探针核定，与文档相反）。
/// </summary>
public sealed class AcadRuntime : ICadRuntime
{
    private static readonly VendorProfile Profile = VendorProfiles.Acad;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _staThread;
    private dynamic? _app;
    private dynamic? _doc;
    private bool _disposed;

    // 复审 finding 4：本方 /p 启动的进程句柄（EnsureAppTArch 内赋值，仅用于 ROT 附着后归属
    // 校验与失败清理）。归属不符=附着到的实例非本方启动（用户同时开了 AutoCAD）——
    // 清理只对本进程，绝不 Quit 他方实例。
    private Process? _launchedProc;

    // 重名碰撞守卫（复审微修复波）：本文档会话经 EnsureBlock 成功创建的块名集合。
    // 重入补全只对"本方创建"的块清除重建；预存用户块（名不在集合）一律复用早退——
    // skill 符号名与用户图纸自有块重名（如 "DOOR"）时不得删除用户内容（恢复旧非破坏行为）。
    // AutoCAD 块名大小写不敏感（与 insert-symbol 符号匹配同口径 OrdinalIgnoreCase）；
    // Open 换文档即清空：跨文档同名块不属于本文档会话的"本方残留"。
    private readonly HashSet<string> _createdBlocks = new(StringComparer.OrdinalIgnoreCase);

    // 切片B：NETLOAD 只对本实例的 AutoCAD 进程有效——成功加载后同实例后续桥操作跳过 NETLOAD；
    // 实例被 Quit/重启（Restart/失败清理）时复位（新进程未加载桥）。
    private bool _bridgeLoaded;

    // 切片B（复审 F1）：实例级桥通道目录。AutoCAD 子进程只在 CreateProcess 时**快照**环境变量
    // CADAGENT_TARCH_CHANNEL——若每次调用新建目录，第二次调用写入的就是子进程看不到的新目录。
    // 故目录在本实例首次启动前确定（EnsureChannelDir），READ 与 EXPLODE_REPLACE 全程复用；
    // 目录删除与环境变量还原只在 Dispose（实例生命周期=桥会话生命周期）。
    private string? _channelDir;
    private string? _channelEnvPrevious;

    internal static Func<string, Type?> ProgIdResolver = Type.GetTypeFromProgID;
    internal static Func<Type, object?> CreateComInstance = t => Activator.CreateInstance(t);

    // 切片A §4.1：天正 profile 感知启动的 exe 启动缝（测试注入；单元测试不得拉起真机）。
    // T10 真机核定收紧（2026-11-25）：原假定「安装 AutoCAD 2014 的真机环境 PATH 可解析
    // acad.exe」**实证失败**——Workstation 安装不写 PATH，裸 Process.Start("acad.exe")
    // 抛 Win32Exception「系统找不到指定的文件」（诊断经本缝透传，见 EnsureAppTArch）。
    // 故启动前解析安装路径：① 注册表 AcadLocation（HKLM/HKCU ...\Autodesk\AutoCAD\R*\ACAD-*，
    // 版本降序，每根独立容错）；② %ProgramFiles%/%ProgramFiles(x86)%\Autodesk\AutoCAD *\acad.exe
    // 目录扫描（版本降序）；③ 兜底裸 "acad.exe"（PATH 可解析环境保持旧行为；仍失败由
    // EnsureAppTArch 响亮上抛）。
    internal static Func<string?> AcadExecutableResolver = DefaultResolveAcadExecutable;

    private const string DefaultAcadExecutableName = "acad.exe";

    // 返回 null=启动失败（UseShellExecute 时 Process.Start 亦可能返回 null）。
    internal static Func<string, Process?> ProcessLauncher = profileName =>
        Process.Start(BuildStartInfo(profileName, AcadExecutableResolver() ?? DefaultAcadExecutableName));

    // /p 启动参数构造（纯函数，单测钉住 FileName/Arguments/UseShellExecute，不拉起进程）。
    internal static ProcessStartInfo BuildStartInfo(string profileName, string executable) =>
        new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"/p \"{profileName}\"",
            UseShellExecute = true,
        };

    // 安装路径解析纯逻辑（单测注入候选表；仅 File.Exists 探测，无进程/无 COM 副作用）。
    internal static string? ResolveAcadExecutable(
        IReadOnlyList<string> registryLocations, IReadOnlyList<string> installDirs)
    {
        foreach (var loc in registryLocations)
        {
            if (string.IsNullOrWhiteSpace(loc)) continue;
            string exe = Path.Combine(loc, DefaultAcadExecutableName);
            if (File.Exists(exe)) return exe;
        }
        foreach (var dir in installDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string exe = Path.Combine(dir, DefaultAcadExecutableName);
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static string? DefaultResolveAcadExecutable() =>
        ResolveAcadExecutable(DefaultAcadRegistryLocations(), DefaultAcadInstallDirs());

    // 注册表候选位置（只读）：本机实证布局 HKLM\SOFTWARE\Autodesk\AutoCAD\R19.1\ACAD-D001:804
    // → AcadLocation=C:\Program Files\Autodesk\AutoCAD 2014。
    // 复审 finding 9a：**每根独立 try/catch**——HKLM 不可读（权限/布局差异/权限位拒绝）不得
    // 连带跳过 HKCU（用户级覆盖键在缺 HKLM 值时是唯一来源）；某根内部某 release/product 抛
    // 错亦仅终止该根枚举，已收集候选保留、继续下一根。全部不可读→空表回退目录扫描。
    // 复审 finding 9c（信任边界）：注册表给出的安装路径会以**当前用户身份**执行（Process.Start
    // /p）；HKCU 用户可写——本通道只用于定位本机已安装 acad.exe，非安全边界，调用方信任
    // 范围为"同用户已可信的注册表配置"。
    private static IReadOnlyList<string> DefaultAcadRegistryLocations()
    {
        var locations = new List<string>();
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var acadRoot = root.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
                if (acadRoot == null) continue;
                var releases = acadRoot.GetSubKeyNames()
                    .Where(n => n.StartsWith("R", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(VersionRank);
                foreach (string release in releases)
                {
                    using var rk = acadRoot.OpenSubKey(release);
                    if (rk == null) continue;
                    foreach (string product in rk.GetSubKeyNames())
                    {
                        using var pk = rk.OpenSubKey(product);
                        if (pk?.GetValue("AcadLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                            locations.Add(loc);
                    }
                }
            }
            catch
            {
                // 该根不可读（权限/布局差异）→ 继续下一根（不得连带吞掉 HKCU 候选），
                // 不静默当"未安装"（空表回退目录扫描）。
            }
        }
        return locations;
    }

    // 常见安装目录扫描（版本降序；本机实证 %ProgramFiles%\Autodesk\AutoCAD 2014）。
    // 复审 finding 9b：补 Program Files (x86)（32 位安装布局）；两根全部候选合并后按版本降序
    // （同版本按路径 Ordinal 定序，结果确定）。
    private static IReadOnlyList<string> DefaultAcadInstallDirs()
    {
        var dirs = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string autodesk = Path.Combine(root, "Autodesk");
                if (!Directory.Exists(autodesk)) continue;
                dirs.AddRange(Directory.EnumerateDirectories(autodesk, "AutoCAD *"));
            }
            catch
            {
                // 该根不可枚举 → 跳过（下一根/调用方兜底裸名），不影响已收集候选。
            }
        }
        return dirs
            .OrderByDescending(VersionRank)
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    // 名称版本排序键：取名称首个数字串（"R19.1"→19、"AutoCAD 2014"→2014）；无数字→0。
    internal static int VersionRank(string name)
    {
        int i = 0;
        while (i < name.Length && !char.IsDigit(name[i])) i++;
        int j = i;
        while (j < name.Length && char.IsDigit(name[j])) j++;
        return j > i && int.TryParse(name.AsSpan(i, j - i), out int v) ? v : 0;
    }

    // 真机核定（2026-11-24，AutoCAD 2014 R19.1）：CreateInstance 返回≠初始化完成，
    // 启动窗口内 COM 调用抛 RPC_E_CALL_REJECTED(0x80010001)/RPC_E_SERVERCALL_RETRYLATER(0x8001010A)
    // （实测约 0.4s 后恢复）。有界重试等待就绪；默认上限 30s 防挂死，耗尽→上层优雅降级。
    internal static int StartupRetryAttempts = 60;
    internal static TimeSpan StartupRetryDelay = TimeSpan.FromMilliseconds(500);

    public AcadRuntime()
    {
        _staThread = new Thread(() =>
        {
            // 启动窗口 COM 拒绝在消息过滤器层自动重试（真机核定，见 ComMessageFilter 注释）。
            ComMessageFilter.Register();
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
    }

    // Run=纯 STA 调度（I2，PR#24 修复波）：不再整 lambda 忙重试——复合方法
    // （AddX+SetLayer/EnsureBlock 多基元）后段拒绝→整段重试会重复前段副作用（重复实体）。
    // 忙重试降至单个 COM 基元调用粒度（RetryBusy），由各入口/基元自行包装。
    private T Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    // 应用层忙拒绝有界重试（真机核定 2026-11-24）：AutoCAD 忙时（Open 后再生窗口等）
    // IDispatch::Invoke 直接把 RPC_E_CALL_REJECTED(0x80010001)/RETRYLATER(0x8001010A)
    // 作为调用结果返回——RPC 层消息过滤器兜不住此形态（f9 实证：SaveAs 仍被拒）。
    // **单调用口径（I2 更正）**：拒绝=该单个 COM 基元调用未被执行，仅对单调用重试安全；
    // 复合方法不得整段重试（后段拒绝→前段副作用重复），须逐基元包装（见 Add*/EnsureBlock）。
    // 只读取值（ModelSpace()/Blocks/Count 等 getter）无副作用，可与后续单写调用同组重试。
    // 非忙错误立即上抛不吞诊断；操作内层已把 COMException 包装为 CadRuntimeException→
    // 沿 InnerException 链判定。
    private static T RetryBusy<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (Exception ex) when (IsBusyRejection(ex) && i < StartupRetryAttempts - 1)
            {
                Thread.Sleep(StartupRetryDelay);
            }
        }
    }

    private static void RetryBusy(Action a) => RetryBusy<object?>(() => { a(); return null; });

    private static bool IsBusyRejection(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is COMException com &&
                (com.HResult == unchecked((int)0x80010001) || com.HResult == unchecked((int)0x8001010A)))
                return true;
            // GetIDsOfNames 层拒绝被 C# 绑定器包装为 MissingMemberException（消息含 HRESULT）。
            // M3 脆弱面披露（PR#24 修复波）：消息文本匹配依赖绑定器消息格式含 HRESULT 十六进制
            // 串——区域设置/运行时版本变化可能失配（漏判→忙拒绝当普通错误上抛，安全侧失败，
            // 不产生重复副作用）。COMException HResult 直判为主通道；此分支为 f6 实证形态兜底。
            if (e is MissingMemberException mm &&
                (mm.Message.Contains("0x80010001") || mm.Message.Contains("0x8001010A")))
                return true;
        }
        return false;
    }

    private void Run(Action a) => Run<object?>(() => { a(); return null; });

    private dynamic EnsureApp()
    {
        if (_app == null)
        {
            // 通道目录须先于任何启动路径确定（复审 F1）：EnsureApp 也是 acad.exe 的一次启动
            // （CreateComInstance→LocalServer32），其子进程同样快照本环境变量。
            EnsureChannelDir();
            VendorGuard.EnsureNoForeignInstance(Profile);
            var type = ProgIdResolver(Profile.ProgId)
                ?? throw new CadRuntimeException($"AutoCAD COM 未注册: {Profile.ProgId}");
            try
            {
                _app = CreateComInstance(type)
                    ?? throw new CadRuntimeException("AutoCAD COM 实例创建失败");
            }
            catch (Exception ex) when (ex is not CadRuntimeException)
            {
                throw new CadRuntimeException(
                    $"AutoCAD COM 激活失败（ProgID 可能为孤儿注册，无 LocalServer32）: {ex.Message}", ex);
            }
            SetVisibleStartupReady();
        }
        return _app;
    }

    // 启动窗口拒绝重试：仅对 RPC_E_CALL_REJECTED/RPC_E_SERVERCALL_RETRYLATER 重试，
    // 其他 COM 错误（孤儿注册/许可失败等）立即上抛，不吞诊断信息。
    private void SetVisibleStartupReady()
    {
        for (int i = 0; ; i++)
        {
            try { _app!.Visible = false; return; }
            catch (COMException ex) when (
                (ex.HResult == unchecked((int)0x80010001) || ex.HResult == unchecked((int)0x8001010A))
                && i < StartupRetryAttempts - 1)
            {
                Thread.Sleep(StartupRetryDelay);
            }
        }
    }

    // ---- 切片A §4.1：天正 profile 感知启动（/p 启动 + ROT 附着 + Gate1）----
    // 与既有 EnsureApp（普通 COM 激活→默认 profile，无 TArch）互斥：_app 已存在即复用，
    // 不得再拉起第二实例（双实例共享用户 profile 会竞态损坏 CUIX，VendorGuard 语义）。
    // 流程：守卫→/p "profileName" 启动 acad.exe→ROT 附着（AcadAttach）→归属校验→Gate1 就绪等待。
    // 归属校验（复审 finding 4）：ROT AttachOnly 取的是"当前注册的活动实例"——若用户同时
    // 开了 AutoCAD，可能附着到他方实例；此时后续 Cleanup/Dispose 的 Quit 会误杀外人实例并
    // 留下本方孤儿进程。故附着后比对实例 HWND 与本方进程主窗句柄，不符→只清本方进程、
    // 置空 _app 并响亮拒绝（绝不 Quit 附着实例）。
    // 失败路径不残留进程（spec §5）：经 CleanupTArchFailure 优雅 Quit / 兜底 Kill 本方进程。
    // 调用方（ReadNativeParams，Task 5）经 Run 在 STA 队列内执行。
    //
    // Fix round 3（真机实证）：/p 每次启动都弹一个**信息性** WPF 模态框（标题含"不支持硬件加速"）。
    // 未关掉时 COM Gate1 间歇失败、Dispose 的 Quit 还会留下本方 acad.exe 孤儿进程；测试场景
    // 无人手点，只能按标题自动关窗。范围严格收紧：只枚举本方启动进程的窗口、只投递 WM_CLOSE、
    // 绝不按键/点击/结束进程；非标题命中的窗口一律不碰。
    internal dynamic EnsureAppTArch(string profileName)
    {
        if (_app != null)
        {
            // 复用路径仍须过 Gate1（复审 F2）：_app 可能由 EnsureApp()（默认 profile，无 /p）
            // 先建好——早退不得绕过就绪等待，否则调用方在未就绪实例上读参/读桥。
            WaitTArchGate1();
            return _app;
        }
        // 通道目录须先于 /p 启动确定（复审 F1）：子进程只在启动时快照该环境变量。
        EnsureChannelDir();
        VendorGuard.EnsureNoForeignInstance(Profile);
        Process? proc = null;
        Timer? dismissal = null;   // 关窗节拍句柄：finally 必 dispose（不遗留线程/回调）
        int processId = 0;
        try
        {
            // 复审修复波：默认 launcher 异常已透传（不再吞 Win32 原因），此处统一包装并
            // 拼入 ex.Message（inner 保留），保证真机可见"acad.exe 未找到/权限拒绝"诊断。
            try { proc = ProcessLauncher(profileName); }
            catch (Exception ex) { throw new CadRuntimeException($"天正实例启动失败: {ex.Message}", ex); }
            if (proc == null)
                throw new CadRuntimeException("天正实例启动失败");
            _launchedProc = proc;
            // 关窗节拍须在 ROT 附着之前起：模态框会挡住 COM 调用（Gate1/附着均被拒）。
            processId = SafeProcessId(proc);
            dismissal = StartInformationalDialogDismissal(processId);
            _app = AcadAttach.AttachOnly(Profile.ProgId);
            // 归属校验（findings 4）：_launchedProc 恒非空于本路径（复用早退走上方 _app!=null）。
            // 不符→清空 _app 后抛出，外层 catch 的 CleanupTArchFailure 只见 proc（不 Quit）。
            if (!OwnershipMatches(_app, proc))
            {
                _app = null;
                throw new CadRuntimeException("ROT 附着实例非本方启动（归属校验失败）");
            }
            WaitTArchGate1();
            return _app;
        }
        catch
        {
            // 清理前补关一次：模态框未关时优雅 Quit 会被卡住，留下孤儿进程。best-effort 吞异常，
            // rethrow 保持原异常与堆栈，清理不掩盖根因。
            DismissInformationalDialogs(processId);
            CleanupTArchFailure(proc);
            throw;
        }
        finally
        {
            // Gate1 成功或放弃即停节拍（Timer.Dispose 不阻塞、不 Join 在途回调）。
            dismissal?.Dispose();
        }
    }

    // ---- Fix round 3：本方进程信息性模态框自动关窗 ----
    // 标题针（真机实证文案）：只关"不支持硬件加速"这一条信息性提示，其他对话框（安全确认框、
    // 用户图纸相关模态框等）一律不碰——关错窗口的代价远大于收益。
    internal static readonly string[] InformationalDialogTitleNeedles = { "不支持硬件加速" };

    // 节拍（缝：单测可调）；首发立即执行（模态框出现即关，不等第一个周期）。
    internal static TimeSpan DialogDismissInterval = TimeSpan.FromSeconds(1);

    // 进程 PID（未 Start/已退出的句柄会抛 InvalidOperationException）→ 0 表示"无 PID"：
    // 关窗逻辑据此早退，绝不退回全桌面枚举（那会误伤他方窗口）。
    private static int SafeProcessId(Process? proc)
    {
        try { return proc?.Id ?? 0; }
        catch { return 0; }
    }

    // 关窗一次性动作（best-effort）：只经 AcadWindows 的标题匹配关窗缝，异常一律吞掉。
    private static void DismissInformationalDialogs(int processId)
    {
        if (processId <= 0) return;
        try { AcadWindows.DismissDialogs(processId, InformationalDialogTitleNeedles); }
        catch { /* 关窗 best-effort：不影响 Gate1/退出主路径 */ }
    }

    // 关窗节拍：首发同步执行一次（确定性，且尽早解除模态阻塞），随后每 DialogDismissInterval
    // 一次直到调用方 dispose。<paramref name="processId"/>&lt;=0（fake/未启动进程）不建定时器。
    private static Timer? StartInformationalDialogDismissal(int processId)
    {
        if (processId <= 0) return null;
        DismissInformationalDialogs(processId);   // 首发：不等周期
        return new Timer(_ => DismissInformationalDialogs(processId), null,
            DialogDismissInterval, DialogDismissInterval);
    }

    // 失败路径进程清理（spec §5「启动失败不残留进程」）。仅处置本方经 ProcessLauncher
    // 拿到的 Process 句柄——守卫前置已保证无他方实例，绝不强杀外人进程。
    // 已附着（_app 已由 ROT 赋值）→ 优先优雅 Quit（RetryBusy 覆盖忙拒绝）；Quit 失败
    // 或未附着 → 进程兜底 CloseMainWindow→有界 WaitForExit→Kill。
    // _app/_doc 无论清理成败一律置空：否则残留缓存会让下一次 EnsureAppTArch 早退返回
    // 已失效对象（Gate1 失败后重试须真正重启，复审 finding 3）。
    private void CleanupTArchFailure(Process? proc)
    {
        var app = _app;
        _app = null;
        _doc = null;
        _launchedProc = null; // 失败清理后下次 EnsureAppTArch 须真正重启（不残留失效句柄）
        _bridgeLoaded = false; // 进程级 NETLOAD 注册随实例销毁失效（新进程须重装）
        bool quit;
        if (app != null)
        {
            try { RetryBusy(() => { app.Quit(); }); quit = true; }
            catch { quit = false; /* 优雅失败→走进程兜底；吞异常不掩盖原异常 */ }
        }
        else quit = false;
        if (!quit && proc != null)
        {
            try { TarchProcessCleanup(proc); }
            catch { /* 清理 best-effort：异常不影响原失败诊断 */ }
        }
    }

    // 失败路径进程清理缝（测试注入；单元测试不得操作真进程）。
    // 默认步骤：CloseMainWindow（温和关窗）→ 有界 WaitForExit → Kill 兜底；
    // 每步独立吞异常（进程未启动/已退出/无主窗口均属常态），返回是否确认清理完成。
    internal static Func<Process, bool> TarchProcessCleanup = DefaultTarchProcessCleanup;
    private static readonly TimeSpan TarchCleanupWait = TimeSpan.FromSeconds(10);

    // 复审 finding 4：ROT 附着归属校验缝。HwndReader=实例主窗口句柄（默认 e.HWND）；
    // OwnershipMatches=归属判定可注入（单测注入 true/false，fake Process 无法携带真实 HWND）。
    internal static Func<dynamic, int> HwndReader = e => (int)e.HWND;
    internal static Func<dynamic, Process, bool> OwnershipMatches = DefaultOwnershipMatches;

    // 默认归属判定：实例 HWND 须等于本方进程主窗句柄（Refresh 后取实时值）。
    // 不可判定即放行（fail-open 到既有行为）：HWND==0（实例未建主窗/无该属性面）、进程
    // 主窗句柄==0（ShellExecute 启动后主窗尚未创建）、进程句柄未关联（fake/non-started）
    // 或 COM 读拒——此时无法否认归属，沿用旧行为（守卫 EnsureNoForeignInstance 已前置，
    // 归属校验是纵深防御，不是唯一防线）。
    private static bool DefaultOwnershipMatches(dynamic app, Process proc)
    {
        try
        {
            int hwnd = HwndReader(app);
            if (hwnd == 0) return true;
            proc.Refresh();
            long main = proc.MainWindowHandle.ToInt64();
            if (main == 0) return true;
            return hwnd == unchecked((int)main);
        }
        catch { return true; }
    }

    private static bool DefaultTarchProcessCleanup(Process proc)
    {
        try { if (proc.HasExited) return true; /* 已自行退出：无残留 */ }
        catch { return false; /* 未启动/句柄失效：无可清理 */ }
        try
        {
            if (proc.CloseMainWindow() && proc.WaitForExit((int)TarchCleanupWait.TotalMilliseconds))
                return true;
        }
        catch { /* 无主窗口/关闭被拒：Kill 兜底 */ }
        try { proc.Kill(); return true; }
        catch { return false; /* 已退出或权限受限：best-effort 终止 */ }
    }

    // Gate1（spike 协议步骤3）：ROT 附着成功≠天正初始化完成——Version 非空字符串且
    // 至少 1 个文档（/p 启动默认开 Drawing1.dwg）才可安全读参。就绪条件不满足→按
    // StartupRetryDelay 有界重试（StartupRetryAttempts 次）；忙拒绝由内层 RetryBusy
    // 自行有界重试，其耗尽即"确实未就绪"→直接 Gate1 终局（不叠加外层重试放大总时长）。
    // 耗尽→响亮拒绝（不无限挂起，CLI 据此降级）。
    private void WaitTArchGate1()
    {
        Exception? last = null;
        for (int i = 0; i < StartupRetryAttempts; i++)
        {
            try
            {
                var version = RetryBusy(() => (string)_app!.Version);
                if (!string.IsNullOrEmpty(version) && RetryBusy(() => (int)_app!.Documents.Count) >= 1)
                    return;
            }
            catch (Exception ex) when (IsBusyRejection(ex))
            {
                // RetryBusy 已用完 StartupRetryAttempts 次忙重试仍被拒——终局不再叠加。
                throw new CadRuntimeException("天正实例未就绪（Gate1 超时）", ex);
            }
            catch (Exception ex) { last = ex; /* 其他未就绪错误：按 Gate1 预算重试到界 */ }
            Thread.Sleep(StartupRetryDelay);
        }
        throw last == null
            ? new CadRuntimeException("天正实例未就绪（Gate1 超时）")
            : new CadRuntimeException("天正实例未就绪（Gate1 超时）", last);
    }

    // 孤儿注册/激活失败/守卫超时均优雅降级 Available=false（CLI 据此走 DXF 降级+警示）。
    // 守卫超时（已有实例运行）也报不可用→降级，安全侧选择；真机协议注记此语义。
    public RuntimeInfo GetInfo() => Run(() =>
    {
        if (ProgIdResolver(Profile.ProgId) == null)
            return new RuntimeInfo("AutoCAD", "", false);
        try
        {
            // 激活（含启动窗口有界重试）先行：失败直达 catch→Available=false（旧语义保持，
            // 不得经忙重试路径绕过启动窗口——EnsureApp 失败后 _app 已缓存，重试会跳过 Visible）。
            var app = EnsureApp();
            // 单调用入口：Version 只读 getter 独立忙重试。
            string version = RetryBusy(() => (string)app.Version);
            return new RuntimeInfo("AutoCAD", version, true);
        }
        catch
        {
            return new RuntimeInfo("AutoCAD", "", false);
        }
    });

    public void Open(string drawingPath) => Run(() =>
    {
        // 单调用入口：激活先行（自带启动窗口重试，失败即上抛）；Documents.Open 单个 COM
        // 基元独立忙重试——拒绝=未执行，重试不重复打开。
        try
        {
            var app = EnsureApp();
            _doc = RetryBusy<object?>(() => app.Documents.Open(drawingPath));
            // 换文档即失效本方创建块名集合（重名碰撞守卫按文档会话口径，防跨文档误清）。
            _createdBlocks.Clear();
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"打开图纸失败: {ex.Message}", ex); }
    });

    public void SaveAs(string outputPath) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        // 单调用入口：SaveAs 单个 COM 基元独立忙重试（f9 实证形态）。
        try { RetryBusy(() => { _doc.SaveAs(outputPath); }); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"SaveAs 失败: {ex.Message}", ex); }
    });

    public void Close(bool save) => Run(() =>
    {
        // COM 失败统一 CadRuntimeException（与 SaveAs 同款）；
        // 仅 Close 成功后清空 _doc——失败时文档可能仍开，保留引用供重试/Restart。
        // 单调用入口：Close 单个 COM 基元独立忙重试；耗尽→包装上抛（_doc 保留）。
        try { if (_doc != null) { RetryBusy(() => { _doc.Close(save); }); _doc = null; } }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"Close 失败: {ex.Message}", ex); }
    });

    public void Restart() => Run(() =>
    {
        // 先关文档再 Quit：带开文档 Quit 会被判异常终止→下次启动弹 CUIX 修复对话框（根因 a）。
        // I2：每个基元独立忙重试——Close 成功后 Quit 被拒仅重试 Quit，不重复 Close。
        // 忙拒绝耗尽→上抛（M3 边界，调用方降级）；非忙错误忽略（崩溃后 Close/Quit 可能失败）。
        try { RetryBusy(() => { _doc?.Close(false); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 崩溃后 Close 可能失败，忽略 */ }
        _doc = null;
        try { RetryBusy(() => { _app?.Quit(); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 崩溃后 Quit 可能失败，忽略 */ }
        _app = null;
        _bridgeLoaded = false;   // Quit 后新实例未 NETLOAD：桥注册随进程销毁
    });

    private dynamic ModelSpace()
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        return _doc.ModelSpace;
    }

    private static double[] Pt(double x, double y) => new[] { x, y, 0.0 };

    private void EnsureLayer(string layer)
    {
        // I2：Item 只读探测 / Add 单写调用各自独立忙重试（拒绝=该调用未执行，不重复建层）。
        try { dynamic l = RetryBusy<object?>(() => _doc.Layers.Item(layer))!; if (l != null) return; }
        catch { /* 不存在→创建 */ }
        RetryBusy(() => { _doc.Layers.Add(layer); });
    }

    private void SetLayer(dynamic e, string? layer)
    {
        if (layer == null) return;
        EnsureLayer(layer);
        // I2：Layer 属性写入为独立单写调用——AddX 成功后此处被拒仅重试写入，不重复建实体。
        RetryBusy(() => { e.Layer = layer; });
    }

    // I2（PR#24 修复波）：复合方法=AddX+SetLayer 多 COM 调用——每个基元独立忙重试，
    // 组合不外层重试（后段拒绝→整段重试会重复实体）。ModelSpace() 只读 getter 无副作用，
    // 与后续单写调用同组重试安全。
    public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().AddLine(Pt(x1, y1), Pt(x2, y2)))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddLine 失败: {ex.Message}", ex); }
    });

    public void AddText(double x, double y, string text, double height, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().AddText(text, Pt(x, y), height))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddText 失败: {ex.Message}", ex); }
    });

    public void AddCircle(double x, double y, double radius, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().AddCircle(Pt(x, y), radius))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddCircle 失败: {ex.Message}", ex); }
    });

    public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => Run(() =>
    {
        try
        {
            dynamic e = RetryBusy<object?>(() => ModelSpace().InsertBlock(Pt(x, y), blockName, scale, scale, scale, rotation))!;
            SetLayer(e, layer);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"AddInsert 失败: {ex.Message}", ex); }
    });

    public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        try
        {
            // I2：逐基元独立忙重试，无整段外层重试（中途拒绝→整段重试会重复基元）。
            dynamic blocks = RetryBusy<object?>(() => _doc.Blocks)!;
            dynamic? existing = null;
            try { existing = RetryBusy<object?>(() => blocks.Item(blockName)); }
            catch { existing = null; /* 不存在→创建 */ }
            if (existing != null)
            {
                // 重名碰撞守卫（复审微修复波）：名不在本方创建集合=预存用户块（skill 符号名
                // 撞用户自有块）→复用早退（恢复旧非破坏行为），绝不 Delete 用户内容。
                if (!_createdBlocks.Contains(blockName)) return;
                // I2 重入补全（PR#24 修复波）：本方先前失败可能残留部分块（中途忙拒绝耗尽）——
                // 静默早退会留下静默部分块（读回计数闸只数模型空间，兜不住块内容）。
                // 比对块内实体数与期望基元数：完整→复用（幂等保持）；不一致（不足=残留）→
                // 清除残留基元再重建。仅本方创建块（_createdBlocks 守卫）清除安全；
                // 块定义本身保留（origin 不变，引用不受影响）。
                int count = RetryBusy(() => (int)existing.Count);
                if (count == primitives.Count) return;
                for (int i = count - 1; i >= 0; i--)
                {
                    dynamic item = RetryBusy<object?>(() => existing.Item(i))!;
                    RetryBusy(() => { item.Delete(); });
                }
                AddBlockPrimitives(existing, primitives);
                return;
            }
            // AutoCAD 2014 实测序=(origin, Name)（2026-11-24 探针核定，与文档序相反，同 ZWCAD）。
            // 厂商差异仅数据：翻 VendorProfiles.Acad.BlocksAddNameFirst 即可，无需改此逻辑。
            dynamic block = RetryBusy<object?>(() => Profile.BlocksAddNameFirst
                ? blocks.Add(blockName, Pt(0, 0))
                : blocks.Add(Pt(0, 0), blockName))!;
            // 创建成功即入本方集合（先于基元填充）：中途忙拒绝耗尽残留的部分块也属"本方"，
            // 重入走补全重建通道；预存用户块永不入集合。
            _createdBlocks.Add(blockName);
            AddBlockPrimitives(block, primitives);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EnsureBlock 失败: {ex.Message}", ex); }
    });

    // 块基元逐个独立忙重试（I2）：每个 AddX 为单写调用，拒绝=未执行，重试不重复。
    private static void AddBlockPrimitives(dynamic block, IReadOnlyList<BlockPrimitive> primitives)
    {
        foreach (var p in primitives)
        {
            switch (p)
            {
                case BlockLine l: RetryBusy(() => { block.AddLine(Pt(l.X1, l.Y1), Pt(l.X2, l.Y2)); }); break;
                case BlockCircle c: RetryBusy(() => { block.AddCircle(Pt(c.X, c.Y), c.Radius); }); break;
                case BlockText t: RetryBusy(() => { block.AddText(t.Value, Pt(t.X, t.Y), t.Height); }); break;
                default: throw new CadRuntimeException($"不支持的块基元: {p.GetType().Name}");
            }
        }
    }

    // ---- 切片A §4.1：COM 原生读参编排（Task 5）----
    // 天正 profile 名（spike 实证值）：/p 启动仅此 profile 加载 TArch 自定义面。
    // R17：守卫经注册表面读 TRUSTEDPATHS 时也用它（单一口径，不复制字面量）。
    internal const string TArchProfileName = "TElec20V5";

    // 逐实体读缝（spec 切片A §5）：默认走 dynamic COM + RetryBusy；测试注入 fake。
    // UNKNOWNNAME(0x80020006)=属性不存在→记缺省不重试；忙窗拒绝经 RetryBusy 内层处理。
    // 用 InvokeMember 拿真实 COMException.HResult 以区分 UNKNOWNNAME 与忙窗（dynamic 属性访问
    // 对未知成员抛 RuntimeBinderException 不含 HRESULT）——与既有 AddLine 纯 dynamic 不同，此处需 hr 判定。
    internal static Func<dynamic, string?> ObjectNameReader = e =>
        RetryBusy(() => (string?)e.ObjectName);
    internal static Func<dynamic, string?> LayerReader = e =>
        RetryBusy(() => (string?)e.Layer);
    internal static Func<dynamic, string, string?> ParamReader = (e, n) =>
    {
        try
        {
            object? v = RetryBusy(() => e.GetType().InvokeMember(
                n, System.Reflection.BindingFlags.GetProperty, null, (object)e, null));
            return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // 复审 ISTQB I4：反射包装链（TargetInvocationException 等）内层 COMException 才带
            // 真实 HRESULT——沿 InnerException 链判定（镜像 IsBusyRejection 先例）：
            // UNKNOWNNAME=该属性不存在 → null（缺省，不重试）；其余（含忙拒绝——RetryBusy 已
            // 耗尽重试）一律上抛：忙窗耗尽≠属性缺省，不得静默吞成 null。
            if (ContainsHResult(ex, UnknownNameHResult)) return null;
            throw;
        }
    };
    internal static Func<dynamic, double[]?> BboxReader = e =>
    {
        try
        {
            return RetryBusy(() => ReadBbox(e));
        }
        catch (Exception ex)
        {
            // spec §4 bbox nullable 尽力读：无此面（UNKNOWNNAME）/类型不匹配 → null（ReadOk
            // 仍 true）。但复审 ISTQB Minor 13：忙拒绝耗尽≠"face 不存在"（空读≠空义）——
            // RetryBusy 已重试仍被拒=整读失败，诚实上抛（不得把读失败伪装成"无 bbox"）。
            if (ContainsHResult(ex, UnknownNameHResult)) return null;
            if (IsBusyRejection(ex)) throw;
            return null;
        }
    };

    // UNKNOWNNAME（IDispatch 属性不存在）HRESULT：逐属性缺省口径（spec §4.1 不重试）。
    private const int UnknownNameHResult = unchecked((int)0x80020006);

    // COMException 链判定（复审 ISTQB I4/13）：反射/包装异常须沿 InnerException 链找真实
    // HRESULT；命中即返回 true（镜像 IsBusyRejection 的链式先例）。
    private static bool ContainsHResult(Exception ex, int hresult)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is System.Runtime.InteropServices.COMException com && com.HResult == hresult)
                return true;
        return false;
    }

    // GetBoundingBox([ref]min,[ref]max)：IDispatch 出参**必须**以 object[] 占位 + by-ref
    // ParameterModifier 传递——T10 真机实证：仅传 double[3] 占位（不标 by-ref）时 COM 不回写，
    // 读回恒为全零假 bbox（handle-ops 真机首跑 move 分量 before=0/after=0 即此根因）；
    // spike phasea 已实证 by-ref 形态（min/max 回写为真实 3D 点）。非常规返回形态→null（诚实降级）。
    internal static double[]? ReadBbox(object entity)
    {
        object[] args = { null!, null! };
        var byRef = new System.Reflection.ParameterModifier(2);
        byRef[0] = true; byRef[1] = true;
        entity.GetType().InvokeMember("GetBoundingBox",
            System.Reflection.BindingFlags.InvokeMethod, null, entity, args,
            new System.Reflection.ParameterModifier[] { byRef }, null, null);
        double[]? min = ToVec3(args[0]);
        double[]? max = ToVec3(args[1]);
        if (min == null || max == null) return null;
        return new[] { min[0], min[1], min[2], max[0], max[1], max[2] };
    }

    // COM SAFEARRAY(double)×3 → double[3]（I4 归一）。非 3 元数组/不可转数值→null。
    internal static double[]? ToVec3(object? value)
    {
        if (value is Array arr && arr.Length == 3)
        {
            var v = new double[3];
            for (int i = 0; i < 3; i++)
                v[i] = Convert.ToDouble(arr.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            return v;
        }
        return null;
    }

    // spec 切片A §4.1：逐实体 ObjectName 分类→TDb 读自定义名参数；非 TDb 返回 null 跳过。
    // ObjectName 不可读（忙窗耗尽）→ ReadOk=false 占位（不静默当"无此实体"）；单属性缺不降 ReadOk。
    internal static NativeEntityParam? ReadOneEntity(dynamic entity, string handle, DateTimeOffset readAt)
    {
        string? objName = ObjectNameReader(entity);
        if (objName == null)
            return new NativeEntityParam(handle, "", SemanticClass.Unknown,
                new Dictionary<string, string>(), null, readAt, ReadOk: false);
        if (!objName.StartsWith("TDb", StringComparison.Ordinal))
            return null;
        var cls = TDbClassMap.ClassFor(objName);
        var parms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ParamNamesFor(objName))
        {
            string? v = ParamReader(entity, name);
            if (v != null) parms[name] = v;
        }
        return new NativeEntityParam(handle, objName, cls, parms, BboxReader(entity), readAt, ReadOk: true,
            Layer: LayerReader(entity));
    }

    // 自定义名读参清单（初版=probe 实证面；新类增量补，未知 TDb 类仍记实参为空表不分类）。
    internal static string[] ParamNamesFor(string objName) => objName switch
    {
        "TDbWall" => new[] { "LeftWidth", "RightWidth", "Height", "Volume", "IsArc", "TotalWidth" },
        "TDbText" or "TDbDrawingName" => new[] { "Text" },
        "TDbColumn" => new[] { "Width", "Volume" },
        "TDbEquipment" => new[] { "PScale", "LRotation" },
        _ => Array.Empty<string>(),
    };

    // Gate2（spike 协议 open 后）：Open 返回≠实体可读——ModelSpace.Count>0 才可逐实体读参。
    // 与 Gate1 同预算：忙拒绝由 RetryBusy 内层有界重试，耗尽即终局（不叠加外层放大挂死）；
    // 其他未就绪错误按 Gate 预算重试到界。耗尽→响亮拒绝（CLI 据此降级）。
    private void WaitTArchGate2()
    {
        Exception? last = null;
        for (int i = 0; i < StartupRetryAttempts; i++)
        {
            try
            {
                if (RetryBusy(() => (int)_doc!.ModelSpace.Count) > 0) return;
            }
            catch (Exception ex) when (IsBusyRejection(ex))
            { throw new CadRuntimeException("天正实例未就绪（Gate2 超时）", ex); }
            catch (Exception ex) { last = ex; /* 其他未就绪错误：按 Gate2 预算重试到界 */ }
            Thread.Sleep(StartupRetryDelay);
        }
        throw last == null
            ? new CadRuntimeException("天正实例未就绪（Gate2 超时）")
            : new CadRuntimeException("天正实例未就绪（Gate2 超时）", last);
    }

    // 逐实体遍历（索引访问，COM ModelSpace 标准口径）：Handle 按 COM 原样读（spike 实证大写
    // "21E00"）——不强制大小写：overlay Upsert 键精确匹配，消费端按需 OrdinalIgnoreCase。
    private List<NativeEntityParam> ReadModelSpaceEntities()
    {
        var result = new List<NativeEntityParam>();
        dynamic ms = RetryBusy<object?>(() => ModelSpace())!;
        int count = RetryBusy(() => (int)ms.Count);
        for (int i = 0; i < count; i++)
        {
            int index = i;  // 闭包快照（RetryBusy 同步调用，取局部副本防捕获变异）
            dynamic entity = RetryBusy<object?>(() => ms.Item(index))!;
            string handle = RetryBusy(() => (string)entity.Handle);
            var p = ReadOneEntity(entity, handle, DateTimeOffset.UtcNow);
            if (p != null) result.Add(p);
        }
        return result;
    }

    // 读完即关（spec §4.1 自开自关）：正常路径关闭失败响亮上抛（调用方不得以为已关）；
    // 仅成功后清空 _doc（与公开 Close 同语义：失败保留引用）。
    private void CloseTArchDoc()
    {
        try { RetryBusy(() => { _doc!.Close(false); }); _doc = null; }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"Close 失败: {ex.Message}", ex); }
    }

    // 读参失败路径：仅关本方本次开启的文档（best-effort）；异常吞掉不掩盖读参根因。
    private void CloseTArchDocBestEffort()
    {
        if (_doc == null) return;
        try { RetryBusy(() => { _doc!.Close(false); }); _doc = null; }
        catch { /* best-effort：不掩盖已上抛的读参根因 */ }
    }

    // R1 消歧形态：path!=null=CLI 自启自读读完关（**ReadOnly=true 打开原图**，不另建副本——
    // 设计承诺只读；复审 finding 3 更正原读改写实现）；null=native-live 会话读当前已开文档。
    // 全程在 Run<T> STA 队列内；体内只调私有内部件——公开 Open/Close/EntityCount 自身包 Run，
    // 队列内再调 Run 会等待自身→死锁。
    public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) =>
        Run<IReadOnlyList<NativeEntityParam>>(() =>
        {
            if (drawingPath == null)
            {
                if (_doc == null) throw new CadRuntimeException("无打开的图纸");
                return ReadModelSpaceEntities();  // 不启不开不关（native-live 形态）
            }
            // 复审 finding 3 守卫：path 形态要求无打开文档——native-live 执行窗口已有 _doc 时
            // 再开第二份会覆盖 _doc 引用（前一份文档失锚、句柄层操作错文档）。
            if (_doc != null)
                throw new CadRuntimeException("已有打开的图纸；ReadNativeParams(path) 须在无打开文档时调用");

            var app = EnsureAppTArch(TArchProfileName);  // /p 启动+ROT 附着+归属校验+Gate1（须在 Run 内）
            bool opened = false;
            try
            {
                // Documents.Open(FileName, ReadOnly, Password)：第二位置参=true 即只读打开。
                // 读参是理解侧只读通道——原图必须只读打开（设计 §4.1 承诺；复审 finding 3）。
                _doc = RetryBusy<object?>(() => app.Documents.Open(drawingPath, true));
                opened = true;
                _createdBlocks.Clear();  // 换文档即失效本方创建块名集合（与 Open 同口径）
                WaitTArchGate2();
                var list = ReadModelSpaceEntities();
                CloseTArchDoc();
                return list;
            }
            catch
            {
                // 读参失败仍关本方本次开启的副本（自开自关不泄漏）；仅本方 opened 才动 _doc
                // （不得误关 native-live 会话原有文档）。
                if (opened) CloseTArchDocBestEffort();
                throw;
            }
        });

    // ---- 切片A §4.2：句柄寻址执行器（Task 8）----
    // 定位口径：ModelSpace 逐实体读 .Handle（RetryBusy 单读缝，测试注入 fake），
    // OrdinalIgnoreCase 比对（COM 原样大写 "21E00"，调用方可能传小写归一形态）；
    // 找不到→响亮拒绝（消息逐字按 spec，Task 9 准入按此判定 stale 并拒绝执行）。
    // 执行口径：每个 COM 基元各自独立忙重试（I2 单调用口径）——定位是只读组合无副作用，
    // 写入基元被拒（=未执行）仅重试该基元，不重复副作用。
    internal static Func<dynamic, string?> HandleReader = e =>
        RetryBusy(() => (string?)e.Handle);

    private dynamic FindByHandle(string hexHandle)
    {
        // ModelSpace getter 为只读基元（与 ReadModelSpaceEntities 同口径：同组忙重试安全）。
        dynamic ms = RetryBusy<object?>(() => ModelSpace())!;
        int count = RetryBusy(() => (int)ms.Count);
        for (int i = 0; i < count; i++)
        {
            int index = i;  // 闭包快照（RetryBusy 同步调用，取局部副本防捕获变异）
            dynamic item = RetryBusy<object?>(() => ms.Item(index))!;
            string? h = HandleReader(item);
            if (string.Equals(h, hexHandle, StringComparison.OrdinalIgnoreCase)) return item;
        }
        throw new CadRuntimeException($"句柄不存在于模型空间: {hexHandle}");
    }

    public void ChangeLayerByHandle(string hexHandle, string layer) => Run(() =>
    {
        try
        {
            dynamic e = FindByHandle(hexHandle);
            EnsureLayer(layer);                        // 层不存在→创建（既有基元，建层单写独立忙重试）
            RetryBusy(() => { e.Layer = layer; });      // 单写调用独立忙重试（I2 口径）
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"ChangeLayerByHandle 失败: {ex.Message}", ex); }
    });

    public void MoveByHandle(string hexHandle, double dx, double dy) => Run(() =>
    {
        try
        {
            dynamic e = FindByHandle(hexHandle);
            // IAcadEntity.Move(Point1, Point2)：位移=Point2−Point1；此处 (0,0)→(dx,dy)。
            RetryBusy(() => { e.Move(Pt(0, 0), Pt(dx, dy)); });
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"MoveByHandle 失败: {ex.Message}", ex); }
    });

    public void DeleteByHandle(string hexHandle) => Run(() =>
    {
        try
        {
            dynamic e = FindByHandle(hexHandle);
            RetryBusy(() => { e.Delete(); });
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"DeleteByHandle 失败: {ex.Message}", ex); }
    });

    public int EntityCount() => Run(() =>
    {
        // 只读组合（ModelSpace getter + Count getter）无副作用，同组忙重试安全。
        try { return RetryBusy(() => (int)ModelSpace().Count); }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EntityCount 失败: {ex.Message}", ex); }
    });

    // ---- 切片B §3/§4：深度炸开桥通道（Task 5）----
    // 编排：EnsureChannelDir（实例级目录+环境变量，须先于任何启动；复审 F1）→ EnsureAppTArch(/p)
    // → Open(ReadOnly) → NETLOAD+探针（看门狗）→ 写请求 → SendCommand → 轮询 .done（120s）
    // → 解析 TSV → DTO→Core 映射 → Close(false)；通道目录与变量在 Dispose 收尾。
    // 桥内不做任何裁决：解析失败/status fail 一律响亮上抛（绝不空结果扮成功）。
    private const int BridgeReadMaxDepth = 4;

    // 桥程序集名（与桥工程输出名一致）；默认位于宿主 AppContext.BaseDirectory。
    internal const string BridgeDllName = "CadAgent.TArchBridge.dll";

    // 桥目录注入缝（测试指向现成占位文件；真机=部署目录）。
    internal static string? BridgeDllOverride;

    // 看门狗常量（spec §5）：NETLOAD 总上限 3600s；20s 无进展指引；命令等待 120s。
    internal static TimeSpan NetLoadTimeout = TimeSpan.FromHours(1);
    internal static TimeSpan NetLoadNoProgressThreshold = TimeSpan.FromSeconds(20);
    internal static TimeSpan BridgeCommandTimeout = TimeSpan.FromSeconds(120);

    // 20s 无进展指引回调：**默认已接通**（复审 F3——此前缝恒 null，指引永不输出），写
    // stderr 并含 TRUSTEDPATHS 配置/回滚片段；测试或 CLI 可替换（null=不打印）。
    internal static Action<string>? NetLoadSlowGuidance =
        text => Console.Error.WriteLine(text + "（回滚：AutoCAD 选项→文件→信任位置 删除该目录即可恢复。）");

    // 安全框检出缝（默认 user32 枚举；测试注入 fake——单元测试不得触真机窗口）。
    internal static Func<int, bool> SecurityDialogDetector = AcadWindows.HasSecurityDialog;

    // 通道目录创建缝（默认系统临时目录下的唯一新目录；测试注入固定目录以驱动 fake 桥）。
    internal static Func<string> ChannelDirFactory = () =>
        Path.Combine(Path.GetTempPath(), "cadagent-bridge-" + Guid.NewGuid().ToString("N"));

    // 桥进程读取的通道目录环境变量（spec §2：/p 启动时注入，子进程继承）。
    internal const string BridgeChannelEnvVar = "CADAGENT_TARCH_CHANNEL";

    /// <summary>
    /// 实例级通道目录（复审 F1）：首次调用时创建并注入环境变量，此后**始终**返回同一目录。
    /// 必须早于任何 acad.exe 启动（/p 或 COM 激活）——桥进程只在 CreateProcess 时快照该变量，
    /// 启动后再设置无效。目录/变量的清理与还原只在 <see cref="Dispose"/>。
    /// </summary>
    private string EnsureChannelDir()
    {
        if (_channelDir != null) return _channelDir;
        string dir = ChannelDirFactory();
        Directory.CreateDirectory(dir);
        _channelEnvPrevious = Environment.GetEnvironmentVariable(BridgeChannelEnvVar);
        Environment.SetEnvironmentVariable(BridgeChannelEnvVar, dir);
        _channelDir = dir;
        return dir;
    }

    // 通道收尾（仅 Dispose 调用）：先等实例退出（Quit 已在 Dispose 的 Run 内完成），再删目录、
    // 还原宿主原环境变量（best-effort：桥残留句柄导致的删除失败不掩盖退出结论）。
    private void CleanupChannel()
    {
        string? dir = _channelDir;
        _channelDir = null;
        if (dir == null) return;
        BridgeChannel.Cleanup(dir);
        try { Environment.SetEnvironmentVariable(BridgeChannelEnvVar, _channelEnvPrevious); }
        catch { /* 环境变量还原 best-effort */ }
        _channelEnvPrevious = null;
    }

    /// <summary>桥 DLL 全路径；未构建（或测试缝缺失）→ 明确"桥未构建"（先于 NETLOAD，spec §5）。</summary>
    internal static string BridgeDllPath()
    {
        if (!string.IsNullOrWhiteSpace(BridgeDllOverride)) return BridgeDllOverride!;
        string dll = Path.Combine(AppContext.BaseDirectory, BridgeDllName);
        if (!File.Exists(dll))
            throw new CadRuntimeException("桥未构建（需 AutoCAD 19.1 引用）：" + BridgeDllName);
        return dll;
    }

    // 系统变量读缝（**文档面** API；测试注入 fake，无 COM）。真机实证（TRX Skip 原因）：
    // ActiveX **Application** 对象没有 GetSystemVariable 成员——dynamic 绑定抛
    // "'System.__ComObject' does not contain a definition for 'GetSystemVariable'"。
    // 系统变量属 Document：doc.GetVariable(name)。**勿回退到 Application**（已烧过一次）。
    internal static Func<dynamic, string, string> DocumentVariableReader =
        (doc, name) => (string)doc.GetVariable(name);

    /// <summary>
    /// TRUSTEDPATHS 纯助手（无 COM、不启动实例，可单测）：无活动文档→响亮拒绝；
    /// 否则经 <see cref="DocumentVariableReader"/> 读文档面系统变量。
    /// </summary>
    internal static string ReadTrustedPaths(dynamic? doc)
    {
        if (doc == null) throw new CadRuntimeException("无活动文档，无法读取 TRUSTEDPATHS");
        return DocumentVariableReader(doc, "TRUSTEDPATHS");
    }

    // ---- TRUSTEDPATHS 预检（R17：注册表优先，绝不为此启动实例）----
    // 真机根因（Fix round 4）：经 COM 读 TRUSTEDPATHS 必须先 EnsureAppTArch → /p 拉起**整实例**；
    // 启动初始化期 Quit 被忽略 → 孤儿 acad.exe；守卫随后只会自跳过"残留进程"（真因被残留掩盖）。
    // 注册表与「选项→文件→信任位置」是同源配置（只读 REG_SZ），读它即可，无需实例。
    // 产品版本根 R19.1（AutoCAD 2014 + 天正 T20V5 实证）；产品子键形如 ACAD-D001:804。
    internal const string RegistryProductRoot = @"Software\Autodesk\AutoCAD\R19.1";
    internal const string TrustedPathsVariableName = "TRUSTEDPATHS";

    /// <summary>
    /// 注册表面读 TRUSTEDPATHS（**只读**，无 COM、不启动进程）：
    /// <c>HKCU\Software\Autodesk\AutoCAD\R19.1\ACAD-*\Profiles\&lt;profileName&gt;\Variables</c>
    /// 的值 TRUSTEDPATHS（REG_SZ）；枚举 ACAD-* 子键，返回首个非 null 值（空串=「已配置但为空」，
    /// 与「无此值」语义不同，交调用方如实披露）；全无/不可读→null。
    /// 只读访问：绝不写注册表、不改任何 AutoCAD 设置。
    /// </summary>
    internal static string? ReadTrustedPathsFromRegistry(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        // CA1416：宿主机恒为 Windows（AutoCAD COM 宿主，本适配器不跨平台），此处显式静默平台告警。
#pragma warning disable CA1416
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(RegistryProductRoot);
            if (root == null) return null;
            foreach (string product in root.GetSubKeyNames())
            {
                if (!product.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase)) continue;
                using RegistryKey? variables = root.OpenSubKey(
                    product + @"\Profiles\" + profileName + @"\Variables");
                if (variables?.GetValue(TrustedPathsVariableName) is string value) return value;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                   or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;   // 只读探测失败按「无该值」处理：回退 COM 兜底 / 守卫如实 Skip，绝不抛
        }
#pragma warning restore CA1416
        return null;
    }

    // 注册表读取缝（R17）：默认=ReadTrustedPathsFromRegistry；单测注入以钉住
    // 「注册表命中→零 COM/零实例」，注入 (_ => null) 则强制走 COM 回退分支（复现旧行为）。
    internal static Func<string, string?> TrustedPathsSource = ReadTrustedPathsFromRegistry;

    /// <summary>
    /// TRUSTEDPATHS 现值（TRUSTEDPATHS 预检=信息性；真机测试据此决定 Skip/运行）。
    /// R17：**注册表优先**——不启动实例；仅当注册表无该 profile/值时才回退 COM 实读
    /// （回退须 /p 拉起实例：启动初始化期 Quit 被忽略会留孤儿，故只作兜底而非主路）。
    /// </summary>
    internal string GetTrustedPaths()
    {
        string? fromRegistry = TrustedPathsSource(TArchProfileName);
        if (fromRegistry != null) return fromRegistry;

        return Run(() =>
        {
            // 复审 F2：兜底路径必须走天正 profile 启动路径——EnsureApp()（默认 profile，无 /p）会返回
            // 错误 profile 的 TRUSTEDPATHS，且把默认 profile 的实例缓存进 _app，污染后续桥会话。
            try
            {
                EnsureAppTArch(TArchProfileName);
                // 系统变量在文档面（见 DocumentVariableReader 注释）：会话已有文档优先，否则取活动文档。
                // R18（Fix round 5）：活动文档取用也是 COM 调用——同样落在启动忙窗内会被拒，须忙重试。
                dynamic? doc = _doc;
                if (doc == null) doc = RetryBusy<object?>(() => _app!.ActiveDocument);
                return ReadTrustedPaths(doc);
            }
            catch (Exception ex) when (ex is not CadRuntimeException)
            { throw new CadRuntimeException($"读取 TRUSTEDPATHS 失败: {ex.Message}", ex); }
        });
    }

    /// <summary>
    /// 文档面 SendCommand 单调用忙重试（Fix round 5，R18；真机 run7 实证）：桥命令落在 TArch
    /// 启动/再生忙窗内时 IDispatch 直接回 RPC_E_CALL_REJECTED(0x80010001)（消息"被呼叫方拒绝
    /// 接收呼叫"），与 Documents.Open 等基元同形态。SendCommand 是单写调用（被拒=命令未执行），
    /// 故有界重试安全；耗尽→异常上抛（由各桥入口既有 catch 包装 CadRuntimeException，不吞诊断）。
    /// NETLOAD / 注册探针 / 正式命令三处发送**全部**经 <see cref="BridgeChannel.SendCommand"/> 缝
    /// 注入至此（见 <see cref="InstallBridgeSeams"/>），单点包装即覆盖全部发送点。
    /// </summary>
    private static void SendCommandRetry(object doc, string command) =>
        RetryBusy(() => { ((dynamic)doc).SendCommand(command); });

    /// <summary>
    /// 桥缝注入（R6）：静态 SendCommand 在每次公开入口赋值——委托捕获本实例文档。
    /// SendCommand 返回 void（ActiveX 口径），故回显恒空串；"是否注册"由探针握手判定
    /// （BridgeChannel.EnsureLoaded），不依赖回显文本（Ruling R3 修订）。
    /// 同时注入硬超时处理器（复审 F3）：只终止**本方** /p 启动的进程（既有
    /// <see cref="TarchProcessCleanup"/> 缝，绝不碰他方实例），不触任何 COM。
    /// </summary>
    private void InstallBridgeSeams()
    {
        BridgeChannel.SendCommand = (cmd, _) =>
        {
            // R18：忙窗拒绝有界重试（NETLOAD/探针/正式命令共用本缝）。
            SendCommandRetry(_doc!, cmd + "\n");
            return "";
        };
        BridgeChannel.HardTimeoutHandler = () =>
        {
            _bridgeLoaded = false;          // 会话被终止：桥注册随进程销毁
            Process? proc = _launchedProc;  // 只处置本方启动句柄（他方实例由归属校验前置排除）
            if (proc == null) return;
            try { TarchProcessCleanup(proc); }
            catch { /* best-effort：终止失败仍按硬超时上报 */ }
        };
    }

    // 看门狗回调：进展=NETLOAD 已返回（netload.sent）/安全框检出/通道出现结果文件。
    // 返回后的首轮必然因 sent 标记放行（同步 SendCommand 语义）；3600s 上限由 BridgeChannel 施加。
    private Func<TimeSpan, bool> BridgeNetLoadWatchdog(string channelDir)
    {
        int pid;
        try { pid = _launchedProc?.Id ?? 0; } catch { pid = 0; }
        return _ =>
        {
            if (BridgeChannel.NetLoadSent(channelDir)) return true;
            bool dialog;
            try { dialog = SecurityDialogDetector(pid); } catch { dialog = false; }
            return dialog || BridgeChannel.AnyDone(channelDir);
        };
    }

    // NETLOAD+探针（同实例仅一次；成功后 _bridgeLoaded 置位）。
    private void EnsureBridgeLoaded(string channelDir)
    {
        if (_bridgeLoaded) return;
        BridgeChannel.EnsureLoaded(
            BridgeDllPath(), channelDir, BridgeNetLoadWatchdog(channelDir),
            NetLoadTimeout, NetLoadNoProgressThreshold, NetLoadSlowGuidance);
        _bridgeLoaded = true;
    }

    // 桥结果 → Core 证据列表（纯映射：无 COM、无文件 IO，可单测）。
    // 口径：PrimitiveCount=该句柄 PRIM 行数（PRIM-TRUNCATED 截断标记不计）；
    //       NestedTdbCount=该句柄"递归超深/非实体产物"失败行数（桥不输出残留 TDb 行，
    //       这两类失败即"炸不动的残留"上界；其余失败为 extents/xdata/explode 类，不计残留）；
    //       CenterlineJson 由宿主推导（TarchDerive，Task 6）——此处恒 null；
    //       XData：1001 行=天正键名（探针实证 HYDRANT_RADIUS），随后各值行归入该键。
    internal static IReadOnlyList<ExplodeEvidence> MapExplodeEvidence(BridgeResult result)
    {
        var now = DateTimeOffset.UtcNow;
        var blockQueues = GroupBlocksByHandle(result.Blocks);
        var list = new List<ExplodeEvidence>(result.Evidence.Count);
        foreach (var e in result.Evidence)
        {
            var prims = new List<ExplodePrimitive>();
            foreach (var p in result.Prims)
            {
                if (!HandleEquals(p.Handle, e.Handle)) continue;
                if (string.Equals(p.RxClass, "PRIM-TRUNCATED", StringComparison.Ordinal)) continue;
                prims.Add(MapPrimitive(p, blockQueues));
            }
            list.Add(new ExplodeEvidence(
                e.Handle, e.RxClass, e.WrapperType, e.Layer, e.Bbox, e.FullyExplodable,
                prims.Count, CountResidualTdb(result.Fails, e.Handle),
                prims, MapXData(result.XData, e.Handle), null, now));
        }
        return list;
    }

    // BLOCK 记录按句柄分组为 FIFO 队列（复审 F4）：同一承载实体可带多条 BLOCK 记录（多块引用），
    // 旧实现"取首个匹配"会把同一 BLOCK 行反复套给每个块基元（块名/插入点全部错位）。
    // 出现序即桥的输出序，与 PRIM 行序对齐；句柄比较 OrdinalIgnoreCase（与 HandleEquals 同口径）。
    internal static Dictionary<string, Queue<BlockDto>> GroupBlocksByHandle(IReadOnlyList<BlockDto> blocks)
    {
        var map = new Dictionary<string, Queue<BlockDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in blocks)
        {
            if (!map.TryGetValue(b.Handle, out var q))
                map[b.Handle] = q = new Queue<BlockDto>();
            q.Enqueue(b);
        }
        return map;
    }

    // 单基元映射：Kind 按 RxClass 家族；Points 尽力解析（取不到→空数组，诚实降级）；
    // Text/BlockName 取 token 或 BLOCK 行（桥对 AcDbBlockReference 只给 extents token，
    // 块名与插入点来自 BLOCK 记录）。块基元按 FIFO **弹出**下一条 BLOCK 记录（队空→BlockName
    // null 留痕，不猜、不重复使用已配对的记录）。
    internal static ExplodePrimitive MapPrimitive(PrimDto prim, Dictionary<string, Queue<BlockDto>> blocks)
    {
        var points = new List<double>();
        string? text = null;
        string? blockName = null;
        foreach (string token in prim.GeomTokens)
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
            {
                // 无键 token：多边形顶点等裸坐标（"x,y,z"）；解析失败即忽略（best-effort）。
                if (TryParsePoint(token) is double[] bare) points.AddRange(bare);
                continue;
            }
            string key = token.Substring(0, eq);
            string value = token.Substring(eq + 1);
            switch (key)
            {
                case "start":
                case "end":
                case "center":
                case "pos":
                case "loc":
                    if (TryParsePoint(value) is double[] pt) points.AddRange(pt);
                    break;
                case "text":
                case "mtext":
                    text = value;
                    break;
                case "block":
                    blockName = value;
                    break;
                default:
                    // r=/ang=/verts=/extents=/geom=? 无对应 Core 字段（或组合串）：忽略。
                    break;
            }
        }
        if (blockName == null && PrimitiveKind(prim.RxClass) == "block")
        {
            // 每个块基元弹出下一条同句柄 BLOCK 记录（FIFO 配对）；队列耗尽→保持 null。
            if (blocks.TryGetValue(prim.Handle, out var queue) && queue.Count > 0)
            {
                BlockDto b = queue.Dequeue();
                blockName = b.Name;
                if (points.Count == 0) points.AddRange(b.Position);
            }
        }
        return new ExplodePrimitive(PrimitiveKind(prim.RxClass), points.ToArray(), text, blockName);
    }

    // RxClass→Core Kind（小写家族名；AcDb 前缀可省）。折叠与 3D 多边形统一为 polyline。
    internal static string PrimitiveKind(string rxClass)
    {
        string family = rxClass.StartsWith("AcDb", StringComparison.OrdinalIgnoreCase)
            ? rxClass.Substring(4)
            : rxClass;
        switch (family.ToLowerInvariant())
        {
            case "line": return "line";
            case "arc": return "arc";
            case "circle": return "circle";
            case "text": return "text";
            case "mtext": return "mtext";
            case "polyline":
            case "lwpolyline":
            case "2dpolyline":
            case "3dpolyline":
                return "polyline";
            case "hatch": return "hatch";
            case "blockreference": return "block";
            default:
                return family.IndexOf("polyline", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "polyline"
                    : "other";
        }
    }

    // XData 映射：1001（注册应用名，天正用作键名）开启键；之后的值行归入当前键（同键多值取首个，
    // 保证 KEY→单值 契约确定）；无键名先行值行→以类型码为键（不丢证据）；只有键名无值→空串留痕。
    // 约定假设（复审 F6）：1001=键名/1000=值 取自探针实证（1001=HYDRANT_RADIUS,1000=28）；
    // 其它图纸的天正 XData 排布可能不同 → 同键只取首个值，属图纸依赖的证据口径。
    internal static IReadOnlyDictionary<string, string> MapXData(IReadOnlyList<XDataDto> rows, string handle)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyOnly = new List<string>();
        string? key = null;
        foreach (var x in rows)
        {
            if (!HandleEquals(x.Handle, handle)) continue;
            if (x.TypeCode == 1001)
            {
                key = x.Value;
                if (!keyOnly.Contains(key)) keyOnly.Add(key);
                continue;
            }
            string k = key ?? x.TypeCode.ToString(CultureInfo.InvariantCulture);
            if (!dict.ContainsKey(k)) dict[k] = x.Value;
        }
        foreach (string k in keyOnly)
            if (!dict.ContainsKey(k)) dict[k] = "";
        return dict;
    }

    // 残留 TDb 上界（见 MapExplodeEvidence 注释）：递归超深/非实体产物失败行。
    internal static int CountResidualTdb(IReadOnlyList<FailDto> fails, string handle)
    {
        int n = 0;
        foreach (var f in fails)
        {
            if (!HandleEquals(f.Handle, handle)) continue;
            if (f.Reason.StartsWith("递归超深", StringComparison.Ordinal)
                || f.Reason.StartsWith("非实体产物", StringComparison.Ordinal))
                n++;
        }
        return n;
    }

    private static bool HandleEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // "x,y,z"（0.### 不变文化）→ double[3]；非 3 元/非数值→null。
    private static double[]? TryParsePoint(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 3) return null;
        var pt = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out pt[i]))
                return null;
        }
        return pt;
    }

    /// <summary>
    /// 深度炸开取证（spec 切片B §3/§4.1）：自启天正实例→只读开图→桥 READ→映射。
    /// 与 ReadNativeParams(path) 同守卫：已有打开文档时响亮拒绝（不得覆盖 _doc 锚点）。
    /// 通道目录为**实例级**（<see cref="EnsureChannelDir"/>，复审 F1）：自本实例首次启动前注入
    /// 环境变量 <see cref="BridgeChannelEnvVar"/> 起全程复用——AutoCAD 子进程只在 CreateProcess
    /// 时快照该变量，故目录不能每次调用重建（重建=桥看不到新目录）；删除与还原只在 Dispose。
    /// </summary>
    public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath) =>
        Run<IReadOnlyList<ExplodeEvidence>>(() =>
        {
            if (drawingPath == null) throw new CadRuntimeException("无打开的图纸参数");
            if (_doc != null)
                throw new CadRuntimeException("已有打开的图纸；ReadExplodeEvidence(path) 须在无打开文档时调用");

            // 实例级通道（与桥进程启动时快照的目录一致）；调用结束**不**清理（Dispose 才清）。
            string channel = EnsureChannelDir();
            var app = EnsureAppTArch(TArchProfileName);   // /p 启动+ROT 附着+归属校验+Gate1
            try
            {
                _doc = RetryBusy<object?>(() => app.Documents.Open(drawingPath, true));
                _createdBlocks.Clear();                  // 换文档即失效本方创建块名集合
                InstallBridgeSeams();
                EnsureBridgeLoaded(channel);
                string id = BridgeChannel.WriteRequest(channel,
                    new BridgeRequest("read", Path.GetFileName(drawingPath), null, BridgeReadMaxDepth, null).Write());
                BridgeChannel.SendCommand(BridgeChannel.ReadCommand, "cmd");
                string? text = BridgeChannel.WaitDone(channel, id, BridgeCommandTimeout);
                if (text == null)
                    throw new CadRuntimeException(
                        $"桥命令超时（{(int)BridgeCommandTimeout.TotalSeconds}s）：{BridgeChannel.ReadCommand}");
                var result = BridgeResult.Parse(text);
                if (!string.Equals(result.Status, "ok", StringComparison.Ordinal))
                    throw new CadRuntimeException("桥失败: " + (result.Error ?? result.Status));
                var list = MapExplodeEvidence(result);
                CloseTArchDoc();                         // 读完即关（失败响亮上抛）
                return list;
            }
            catch (Exception ex) when (ex is not CadRuntimeException)
            {
                // 解析失败（FormatException）/COM 读失败等一律按运行时契约包装上抛。
                CloseTArchDocBestEffort();
                throw new CadRuntimeException($"深度炸开读取失败: {ex.Message}", ex);
            }
            catch
            {
                // 失败路径仅关本方本次开启的文档（best-effort），不掩盖读根因。
                CloseTArchDocBestEffort();
                throw;
            }
        });

    /// <summary>
    /// 句柄级炸开替换（spec 切片B §4.2）：在当前已开文档内执行桥 EXPLODE_REPLACE，
    /// 桥内单事务校验+替换+另存 liveOut；宿主只做编排与结果映射（裁决全在桥内）。
    /// 通道目录同 <see cref="ReadExplodeEvidence"/>：实例级复用（复审 F1），不在此清理。
    /// </summary>
    public ExplodeReplaceResult ExplodeReplaceByHandle(string hexHandle, string liveOutPath) => Run(() =>
    {
        if (_doc == null) throw new CadRuntimeException("无打开的图纸");
        if (string.IsNullOrWhiteSpace(hexHandle)) throw new CadRuntimeException("无句柄参数");
        if (string.IsNullOrWhiteSpace(liveOutPath)) throw new CadRuntimeException("无 liveout 参数");
        string channel = EnsureChannelDir();
        try
        {
            string dwg = RetryBusy(() => (string)_doc.Name);   // 桥按名防错图（Path.GetFileName 两侧同口径）
            InstallBridgeSeams();
            EnsureBridgeLoaded(channel);
            string id = BridgeChannel.WriteRequest(channel,
                new BridgeRequest("explode-replace", dwg, hexHandle, BridgeReadMaxDepth, liveOutPath).Write());
            BridgeChannel.SendCommand(BridgeChannel.ExplodeReplaceCommand, "cmd");
            string? text = BridgeChannel.WaitDone(channel, id, BridgeCommandTimeout);
            if (text == null)
                throw new CadRuntimeException(
                    $"桥命令超时（{(int)BridgeCommandTimeout.TotalSeconds}s）：{BridgeChannel.ExplodeReplaceCommand}");
            var result = BridgeResult.Parse(text);
            if (!string.Equals(result.Status, "ok", StringComparison.Ordinal))
                throw new CadRuntimeException("桥失败: " + (result.Error ?? result.Status));
            var rep = result.Replace ?? throw new CadRuntimeException("桥未返回 REPLACED");
            return new ExplodeReplaceResult(rep.Handle, rep.NewCount, rep.SavedPath, rep.BboxMatch, rep.Tolerance);
        }
        catch (Exception ex) when (ex is not CadRuntimeException)
        { throw new CadRuntimeException($"EXPLODE_REPLACE 失败: {ex.Message}", ex); }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Run(() =>
            {
                // I2：每个基元独立忙重试（同 Restart）；忙拒绝耗尽由 RetryBusy 上抛→
                // 外层 catch 兜底（Dispose 不抛，best-effort 收尾；有界尝试次数单测钉住）。
                // 其余退出期错误忽略。
                try { RetryBusy(() => { _doc?.Close(false); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 退出时忽略 */ }
                _doc = null;
                // Fix round 3：Quit 前关本方信息性模态框（实证：模态框未关时 Quit 后本方
                // acad.exe 变孤儿）。仅本方启动进程、仅标题命中窗口；best-effort。
                DismissInformationalDialogs(SafeProcessId(_launchedProc));
                try { RetryBusy(() => { _app?.Quit(); }); } catch (Exception ex) when (!IsBusyRejection(ex)) { /* 退出时忽略 */ }
                _app = null;
            });
        }
        catch { /* 队列已关闭等 */ }
        // 通道收尾只在 Dispose（复审 F1）：实例销毁=桥会话结束，删目录并还原宿主环境变量。
        CleanupChannel();
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(10));
        // Quit 返回≠进程退出：CUIX 回写发生在退出尾段（PR#17 机制，厂商无关）。
        VendorGuard.WaitForProcessExit(Profile, VendorGuard.ExitWait);
    }
}
