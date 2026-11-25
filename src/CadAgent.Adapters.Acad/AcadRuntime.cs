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

    // 边界补齐 B1：块定义段（BDEF）伴生读数缓存——与 ReadExplodeEvidence 同一次桥会话的读数，
    // 避免为取块定义再开一次图（两次 Open = 双倍耗时且多一个忙窗面）。语义：
    //  · ReadExplodeEvidence(path) **Close 成功之后**才记录归一化路径与本次块定义表
    //    （读失败/Close 失败→缓存保持失效，失败会话的读数不得被当有效事实）；
    //  · ReadExplodeBlockDefs(path) 仅在路径与上次成功读数相等（OrdinalIgnoreCase）时返回缓存，
    //    否则响亮拒绝（绝不隐式二次开图）；每次成功读覆盖缓存（陈旧读数不残留）；
    //  · A3（技术债批次A）：块定义段**按需读取**——只有请求时（includeBlockDefs:true）才缓存；
    //    未请求时缓存标记为"未取"，ReadExplodeBlockDefs 响亮拒绝（"没读"不得冒充"读了 0 行"）；
    //  · Dispose 清空缓存（缓存生命周期=实例/桥会话生命周期）。
    private string? _lastExplodePath;
    private IReadOnlyList<BlockDefTdbEntry> _lastBlockDefs = Array.Empty<BlockDefTdbEntry>();

    // A3：上次成功读数是否**索取了**块定义段（false=缓存里的空表只是占位，不是事实）。
    private bool _lastBlockDefsRequested;

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

    // 本方启动进程 PID 读取缝（fix/acad-rot-pid-filter）：ROT 按 PID 过滤附着需要本方 PID。
    // 默认 SafeProcessId（未 Start/已退出句柄→0）；单测注入固定 PID 以驱动"PID 可判定"路径
    // （fake Process 拿不到真实 PID，不得为测此路径去 Start 真进程）。
    internal static Func<Process, int> ProcessIdReader = p => SafeProcessId(p);

    // 本方进程退出判定缝（评审 follow-up 1 / I2）：附着轮询期间每轮询问——已退出即中止，
    // 不再空转满 180s。默认 HasExited（依 proc 句柄判定：句柄身份≠PID 号，故同号新进程
    // 不会让已退出的本方句柄"复活"，PID 复用窗口因此被关上）；fake/未启动句柄抛
    // InvalidOperationException → 由 ProcessExited 兜为 false（不可判定不中止）。
    internal static Func<Process, bool> ProcExitedReader = p => p.HasExited;

    // 附着轮询上界（缝：单测压到 1 轮，生产 180s 覆盖天正冷启动）。
    internal static int PidAttachAttempts = 180;

    // ROT 过滤耗尽后的**单次** GetActiveObject 回退预算（评审 follow-up 1 / I3）：
    // 小预算即回退——回退只是"过滤这一路可能漏认"的兜底，不应再叠加一个 180s 窗口。
    internal static int PidFallbackAttempts = 3;

    // B8（技术债批次B）：附着总时限（PID 过滤 + 单次回退共用同一 Stopwatch）——超过即响亮中止，
    // 不再让"过滤 180s + 回退 3s + 归属重试 10s"叠成无界等待。默认 120s 覆盖天正冷启动
    // （180 次轮询常量**不是**单路径真实上界：相位预算再按"总时限 - 已耗时 - 回退保留"夹取，
    // 见 ComputeEffectivePidAttachAttempts；总时限是跨路径的硬上界）。
    internal static TimeSpan AttachTotalDeadline = TimeSpan.FromSeconds(120);

    // 总时限测试缝（单测压到毫秒级保持确定性；生产恒走 120s 默认值）。
    internal static TimeSpan? AttachTotalDeadlineOverride;

    // C-I1（技术债批次C）：回退保留预算——相位预算把总时限切成两段：
    //  · 相位1（AttachByPid）上界 = 总时限 - 保留（生产 120s - 15s = 105s）；
    //  · 相位2（单次 AttachOnly 回退）吃掉这 15s 保留（回退预算 3 轮 ≈ 3s，受同一总时限硬约束）。
    // 修复的是"180 轮 > 120s 时限"造成的结构性死代码：相位1 永远先耗尽总时限，回退分支永不执行。
    internal static TimeSpan AttachFallbackReserve = TimeSpan.FromSeconds(15);

    // 保留预算测试缝（单测压到秒级以内保持确定性；生产恒走 15s 默认值）。
    internal static TimeSpan? AttachFallbackReserveOverride;

    /// <summary>C-I1：有效相位1 轮询上界（纯函数，可单测）：总时限减回退保留后取整秒，
    /// 再夹到 [1, 配置常量]。生产 180/120s/15s → **105**；预算被保留吃掉 → 1（不出现 0/负数轮）。
    /// D：无已耗时口径（首轮/无时钟场景）——等价于 elapsed=0 的重载。</summary>
    internal static int ComputeEffectivePidAttachAttempts(int configuredAttempts, TimeSpan total, TimeSpan reserve) =>
        ComputeEffectivePidAttachAttempts(configuredAttempts, total, TimeSpan.Zero, reserve);

    /// <summary>D（微修复波D）：按**剩余预算**夹取的相位轮询上界（纯函数）：
    /// <c>Max(1, Min(configuredAttempts, Ceiling((total - elapsed - reserve).TotalSeconds)))</c>。
    /// 归属重试循环**再次进入**相位1 时必须用本口径——首轮满预算 + 次轮满预算相加可能越过总时限；
    /// 向上取整使不足 1s 的残余预算仍给 1 轮机会（保底不为 0）。</summary>
    internal static int ComputeEffectivePidAttachAttempts(
        int configuredAttempts, TimeSpan total, TimeSpan elapsed, TimeSpan reserve)
    {
        int remainingSeconds = (int)Math.Ceiling((total - elapsed - reserve).TotalSeconds);
        return Math.Max(1, Math.Min(configuredAttempts, remainingSeconds));
    }

    /// <summary>C-I1：按当前配置/测试缝计算的有效相位1 轮询上界。
    /// **测试口径**（生产走 <c>AttachBudget.EffectivePidAttempts</c>，按共享时钟剩余预算夹取）。</summary>
    internal static int EffectivePidAttachAttempts() => ComputeEffectivePidAttachAttempts(
        PidAttachAttempts,
        AttachTotalDeadlineOverride ?? AttachTotalDeadline,
        AttachFallbackReserveOverride ?? AttachFallbackReserve);

    /// <summary>C-I1/I5：PID 路径的总时限耗尽文案（秒数按实际时限向上取整、最小 1s）。
    /// <paramref name="pidFilterSeconds"/> = 相位1（PID 过滤）实际耗时；<paramref name="fallbackRan"/> =
    /// 回退是否已发起（true=回退相位内超时 → "失败"；false=时限先于回退耗尽 → "未完成"）。</summary>
    internal static string AttachDeadlineExhaustedText(TimeSpan total, double pidFilterSeconds, bool fallbackRan) =>
        $"附着总时限（{Math.Max(1, (int)Math.Ceiling(total.TotalSeconds))}s）耗尽：" +
        $"PID 过滤 {(int)pidFilterSeconds}s 已耗，回退{(fallbackRan ? "失败" : "未完成")}";

    /// <summary>C-I1/I5：AttachOnly（PID 不可判定，pid=0）路径的总时限耗尽文案——该路径无回退相位，
    /// 不得出现"回退"措辞（避免排障时误以为还有回退分支）。</summary>
    internal static string AttachDeadlineExhaustedNoPidText(TimeSpan total) =>
        $"附着总时限（{Math.Max(1, (int)Math.Ceiling(total.TotalSeconds))}s）耗尽：GetActiveObject 附着未完成";

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
    // 流程：守卫→/p "profileName" 启动 acad.exe→**按本方 PID 过滤** ROT 附着（AcadAttach.AttachByPid）
    // →归属校验→Gate1 就绪等待。PID 不可判定（fake/未启动进程）时回退既有 AttachOnly（GetActiveObject）。
    // 评审 follow-up 1（I2/I3）：附着轮询带 abortIf=本方进程已退出（fast-fail，不空转 180s）；
    // ROT 过滤耗尽（非进程退出）时只做一次 AttachOnly 小预算回退（见 AttachByPidWithFallback）。
    // fix/acad-rot-pid-filter（真机实证 3/4 全量失败）：类间交接时前一真机类刚 Quit 的实例注册可能
    // 仍在 ROT——AttachOnly 取"当前活动对象"会附着到他方/已退出实例，造成"ROT 附着实例非本方启动
    // （归属校验失败）"伪失败。故本方 PID 可判定时只认"窗口 PID==本方 PID 且进程存活"的实例；
    // 归属校验与有界重试保留为**纵深防御**（注册时序/窗口归属仍在启动期，理论上应恒通过）。
    // 归属校验（复审 finding 4）：若仍附着到他方实例，后续 Cleanup/Dispose 的 Quit 会误杀外人实例并
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
            processId = ProcessIdReader(proc);
            dismissal = StartInformationalDialogDismissal(processId);
            // B8：附着总时限从这里开始计时（PID 过滤、单次回退、归属重试共用同一 Stopwatch）。
            TimeSpan attachTotal = AttachTotalDeadlineOverride ?? AttachTotalDeadline;
            // C-I1（技术债批次C）相位预算：相位1（PID 过滤）只受"进程退出"中止，其轮询上界由
            // 总时限减回退保留收敛（生产 105 轮）；回退相位吃保留、受同一总时限硬约束。
            var budget = new AttachBudget(attachTotal);
            // 相位1 的 abortIf：只看本方进程退出（deadline 由相位预算保证，不在轮询中途打断）。
            Func<bool> pidPhaseAbort = () => ProcessExited(proc);
            // PID 不可判定的 AttachOnly 路径：旧语义（无进程退出早退）保持，只叠加总时限。
            Func<bool> deadlineAbort = () =>
            {
                budget.ThrowIfNoPidPathExhausted();
                return false;
            };
            _app = AttachWithOwnershipRetry(proc, processId, pidPhaseAbort, deadlineAbort, budget);
            WaitTArchGate1();
            return _app;
        }
        // M1（微修复波E）：此处不再有 `catch (RotFilterExhaustedException)`——该类型只在
        // AttachByPidWithFallback 内部流转，包装为 CadRuntimeException 也发生在那里（回退边界），
        // 到本方法时已不可能以原类型出现（旧 catch 为不可达死代码）；通用 catch 照旧清理并 rethrow。
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

    // ---- F3（最终整分支复审）：ROT 附着归属校验的有界重试 ----
    // 真机实证（切片B 门禁 attempt2/attempt3）：桥类与前一真机类交接时，ROT 里可能残留
    // 刚退出实例的注册——`GetActiveObject` 返回的是别人/已死的实例，归属校验伪失败。
    // 归属判定本身同时收紧为**窗口→PID**（见 DefaultOwnershipMatches）：本方实例的闪屏/
    // 主框架窗口差异不再被误判为他方。仍不符时按 10s 有界窗口重试（不无限挂起）：
    //  · 归属不符只清本方**已缓存的附着引用**（_app/_doc/_bridgeLoaded），绝不 Quit 他方实例；
    //  · 本方启动进程句柄 _launchedProc 保留（这不是失败，不得触发进程清理路径）；
    //  · 窗口耗尽 → 沿用原响亮拒绝（外层 catch 的 CleanupTArchFailure 依既有语义清理本方进程）。
    // B8（技术债批次B）+ C-I1（技术债批次C）：整段附着（含本重试循环）受调用方传入的**总时限**
    // 约束，但相位1（PID 过滤）不逐轮判时限（其轮询上界已由相位预算收敛到"总时限 - 回退保留"）；
    // 时限判定发生在轮次之间与回退相位内（超时抛 CadRuntimeException，不进入/不重复回退）。
    // 归属重试窗口只是子上界。
    internal static TimeSpan OwnershipRetryWindow = TimeSpan.FromSeconds(10);

    // 重试窗口测试缝（单测压到毫秒级保持确定性；生产恒走 10s 默认值）。
    internal static TimeSpan? OwnershipRetryWindowOverride;

    // 单次等待上界（规格 Thread.Sleep(1000)；剩余窗口不足时只睡剩余量，保证总时长有界）。
    private static readonly TimeSpan OwnershipRetryDelay = TimeSpan.FromSeconds(1);

    private dynamic AttachWithOwnershipRetry(Process proc, int processId,
        Func<bool> pidPhaseAbort, Func<bool> deadlineAbort, AttachBudget budget)
    {
        TimeSpan window = OwnershipRetryWindowOverride ?? OwnershipRetryWindow;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            // C-I1：跨相位总时限（轮次之间）——归属重试可能再入相位1；时限已耗尽不得再开新一轮。
            if (processId != 0) budget.ThrowIfPidPathExhausted();
            else budget.ThrowIfNoPidPathExhausted();
            // PID 可判定→按本方 PID 过滤的 ROT 附着（不会被交接残留的他方/已退出实例截胡；
            // 进程退出即中止，过滤耗尽则单次回退 GetActiveObject）；
            // PID 不可判定（fake/未启动进程、真机 PID 读取失败）→ 既有 GetActiveObject 口径。
            // B8/C-I1：两条路径都受共享总时限约束（有 PID 时：相位预算 + 回退相位）。
            dynamic app = processId != 0
                ? AttachByPidWithFallback(processId, pidPhaseAbort, budget)
                : AcadAttach.AttachOnly(Profile.ProgId, abortIf: deadlineAbort);
            if (OwnershipMatches(app, proc)) return app;
            // 归属不符：只清本方缓存引用（我方尚未把 app 赋给 _app）；绝不 Quit/触碰他方实例。
            _app = null;
            _doc = null;
            _bridgeLoaded = false;
            if (sw.Elapsed >= window)
                throw new CadRuntimeException("ROT 附着实例非本方启动（归属校验失败）");
            TimeSpan remaining = window - sw.Elapsed;
            Thread.Sleep(remaining < OwnershipRetryDelay ? remaining : OwnershipRetryDelay);
        }
    }

    // ---- 评审 follow-up 1 + C-I1：相位预算（PID 过滤相位 → 单次回退相位）----
    // I2：相位1 轮询带"本方进程是否已退出"谓词——进程没了立即中止（响亮上抛），不空转满轮询上界；
    //     HasExited 依 proc **句柄**判定，亦挡住 PID 复用（同号新进程不会让已退出的本方句柄复活）。
    // C-I1：相位1 无候选轮询上界 = 总时限 - 回退保留（生产 105s），耗尽后**必有一次**回退相位；
    //     回退受同一总时限约束（共享 <see cref="AttachBudget"/> 的 Stopwatch），全局至多一次。
    // I3：回退亦失败→保留原根因再拼回退诊断上抛；总时限耗尽→I5 文案原样上抛（不拼前缀，
    //     避免把"时限"根因埋进"过滤耗尽"文案）。回退结果照旧过归属校验（纵深防御不留后门）。
    private dynamic AttachByPidWithFallback(int processId,
        Func<bool> pidPhaseAbort, AttachBudget budget)
    {
        // D（微修复波D）：轮询上界按**共享时钟**的剩余预算夹取——首入与归属重试后的每次重入
        // 同口径（不沿用首轮满预算，避免各轮预算相加越过总时限）。
        // M1（微修复波E）：进程句柄不经本方法（仅经 pidPhaseAbort 谓词使用），旧 `_ = proc` 残留参数已删。
        int attempts = budget.EffectivePidAttempts(
            PidAttachAttempts, AttachFallbackReserveOverride ?? AttachFallbackReserve);
        try
        {
            return AcadAttach.AttachByPid(Profile.ProgId, processId,
                maxAttempts: attempts, abortIf: pidPhaseAbort);
        }
        catch (RotFilterExhaustedException ex)
        {
            // B7：按**类型**判定"过滤耗尽"（不再匹配消息子串）。相位1 结束：记录 PID 过滤实际耗时。
            budget.NotePidFilterPhaseEnd();
            // 相位1 已耗尽总时限（保留预算也没剩）→ 不再发起回退：I5 "回退未完成" 文案响亮中止。
            budget.ThrowIfPidPathExhausted();
            if (budget.FallbackRan)
            {
                // 回退全局至多一次：嵌套归属重试轮次不重复回退（保留原根因，公开边界统一包装）。
                throw new CadRuntimeException(ex.Message, ex);
            }
            budget.MarkFallbackStarted();
            try
            {
                return AcadAttach.AttachOnly(Profile.ProgId, PidFallbackAttempts,
                    abortIf: () => { budget.ThrowIfPidPathExhausted(); return false; });
            }
            catch (CadRuntimeException fb)
            {
                // 总时限耗尽：I5 文案原样上抛（"回退失败"已在文案中，不再拼回退前缀）。
                if (budget.IsExhausted) throw;
                throw new CadRuntimeException(ex.Message + "；回退 GetActiveObject 亦失败：" + fb.Message, ex);
            }
        }
    }

    /// <summary>C-I1（技术债批次C）：附着相位预算（相位1 轮询上界 + 回退保留共享同一 Stopwatch）。
    /// 只承载时限事实与 I5 文案，不含轮询/COM 逻辑（宿主侧状态机，经 EnsureAppTArch 缝注入驱动）。</summary>
    private sealed class AttachBudget
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        internal AttachBudget(TimeSpan total) => Total = total;

        internal TimeSpan Total { get; }

        /// <summary>回退是否已发起（兼作"全局至多一次"闸门）。</summary>
        internal bool FallbackRan { get; private set; }

        /// <summary>相位1（PID 过滤）实际耗时（相位1 结束时记录；I5 文案用）。</summary>
        internal TimeSpan PidFilterElapsed { get; private set; }

        internal bool IsExhausted => _clock.Elapsed >= Total;

        /// <summary>D（微修复波D）：相位（重）入时的有效轮询上界——配置夹取再按共享时钟的
        /// **剩余预算**（总时限 - 已耗时 - 保留）收敛（见 ComputeEffectivePidAttachAttempts）。</summary>
        internal int EffectivePidAttempts(int configuredAttempts, TimeSpan reserve) =>
            ComputeEffectivePidAttachAttempts(configuredAttempts, Total, _clock.Elapsed, reserve);

        internal void NotePidFilterPhaseEnd() => PidFilterElapsed = _clock.Elapsed;

        internal void MarkFallbackStarted() => FallbackRan = true;

        /// <summary>PID 路径：总时限耗尽 → 抛 I5 文案（回退未发起="未完成"，已发起="失败"）。</summary>
        internal void ThrowIfPidPathExhausted()
        {
            if (!IsExhausted) return;
            throw new CadRuntimeException(AttachDeadlineExhaustedText(
                Total, PidFilterElapsed.TotalSeconds, FallbackRan));
        }

        /// <summary>AttachOnly(pid=0) 路径：总时限耗尽 → 抛不含回退措辞的 I5 文案。</summary>
        internal void ThrowIfNoPidPathExhausted()
        {
            if (!IsExhausted) return;
            throw new CadRuntimeException(AttachDeadlineExhaustedNoPidText(Total));
        }
    }

    // 进程退出判定（不可判定→false，即不中止）：fake/未启动/已释放句柄的 HasExited 会抛。
    private static bool ProcessExited(Process proc)
    {
        try { return ProcExitedReader(proc); }
        catch { return false; }
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

    // 复审 finding 4：ROT 附着归属校验缝。实例 HWND 与窗口→PID 均由 AcadWindows 的**唯一**缝提供
    // （B5 技术债批次B：删除本类重复的 HwndReader——AcadAttach 与 AcadRuntime 共用
    // AcadWindows.HwndReader / AcadWindows.WindowProcessId；单测注入一处即两路同源）；
    // OwnershipMatches=归属判定整体可注入（单测注入 true/false，fake Process 无法携带真实 HWND）。
    internal static Func<dynamic, Process, bool> OwnershipMatches = DefaultOwnershipMatches;

    // 默认归属判定：实例窗口须属本方启动进程。**进程 ID 优先**（F3）：窗口→PID 与
    // proc.Id 直接比对——`Process.MainWindowHandle` 在启动期可能指向闪屏/非主框架窗口，
    // 与实例 HWND 不同会制造"本方实例被误判他方"的伪失败（门禁实证间歇命中）。
    // PID 不可判定（0）时回退旧口径：实例 HWND==proc.MainWindowHandle；两者皆不可判定
    // 即放行（fail-open 到既有行为）：HWND==0（实例未建主窗/无该属性面）、进程主窗句柄==0
    // （ShellExecute 启动后主窗尚未创建）、进程句柄未关联（fake/non-started）
    // 或 COM 读拒——此时无法否认归属，沿用旧行为（守卫 EnsureNoForeignInstance 已前置，
    // 归属校验是纵深防御，不是唯一防线）。
    private static bool DefaultOwnershipMatches(dynamic app, Process proc)
    {
        try
        {
            int hwnd = AcadWindows.HwndReader((object)app);
            if (hwnd == 0) return true;
            proc.Refresh();
            int processId;
            try { processId = proc.Id; } catch { processId = 0; }
            int ownerPid = AcadWindows.WindowProcessId(new IntPtr(hwnd));
            // 双方 PID 可判定 → 精确判定（任何本方窗口都算归属，不因闪屏/主框架差异误拒）。
            if (ownerPid != 0 && processId != 0) return ownerPid == processId;
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

    // 生产可配置桥路径（切片B 复审修复波 I3）：覆盖缝 > 本环境变量 > 宿主部署目录。
    internal const string BridgeDllEnvVar = "CADAGENT_BRIDGE_DLL";

    // 桥产物识别标记（I2）：真桥=CommandMethod 字面量（ASCII 或 UTF-16 任一命中）；
    // 空桩=NoAcadRefsStub.cs 的类型名（唯一编译进空桩程序集的符号）。
    internal const string StubMarker = "NoAcadRefsStub";

    /// <summary>桥缺失文案（逐字沿用；测试断言精确匹配）。</summary>
    internal const string MissingBridgeMessage = "桥未构建（需 AutoCAD 19.1 引用）：" + BridgeDllName;

    /// <summary>空桩产物文案（I2 裁定逐字）：同名空桩不得被 NETLOAD 当可加载桥（会阻塞至 3600s）。</summary>
    internal const string StubBridgeMessage =
        "桥未构建（空桩产物，需 CADAGENT_ACMG_DIR 与 AcMgd/AcDbMgd/AcCoreMgd 引用）";

    // 看门狗常量（spec §5）：NETLOAD 总上限 3600s；20s 无进展指引；
    // 命令等待 120s（切片B 复审修复波 I6：移到 BridgeChannel.CommandWait 测试缝，单测压到毫秒级）。
    internal static TimeSpan NetLoadTimeout = TimeSpan.FromHours(1);
    internal static TimeSpan NetLoadNoProgressThreshold = TimeSpan.FromSeconds(20);

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

    // 通道收尾（仅 Dispose 调用）：M1（复审修复波）——**在 WaitForProcessExit 之后**调用：
    // Quit 返回≠进程退出（CUIX 回写等退出尾段仍在跑），桥进程可能仍持通道目录内的句柄；
    // 先删目录会与退出尾段竞态（删除失败残留/退出尾段写回孤儿文件）。故顺序固定为
    // Close+Quit → STA 线程收敛 → WaitForProcessExit → 删目录 + 还原宿主原环境变量
    // （best-effort：删除失败不掩盖退出结论）。
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

    /// <summary>
    /// 桥 DLL 全路径；未构建（或测试缝缺失）→ 明确"桥未构建"（先于 NETLOAD，spec §5）。
    /// 切片B 复审修复波 I2/I3：解析顺序=覆盖缝 → 环境变量 <see cref="BridgeDllEnvVar"/> → 宿主部署目录
    /// （AppContext.BaseDirectory）；存在后做**空桩识别**（同名空桩产物不得被当成可加载桥）。
    /// </summary>
    internal static string BridgeDllPath()
    {
        var (dll, problem) = ProbeBridgeDll();
        return problem switch
        {
            "dll-missing" => throw new CadRuntimeException(MissingBridgeMessage),
            "stub" => throw new CadRuntimeException(StubBridgeMessage),
            _ => dll,
        };
    }

    /// <summary>桥 DLL 路径解析（I3，不做 IO 判定）：覆盖缝 → 环境变量 → 宿主部署目录。</summary>
    internal static string ResolveBridgeDllPath()
    {
        if (!string.IsNullOrWhiteSpace(BridgeDllOverride)) return BridgeDllOverride!;
        string? fromEnv = Environment.GetEnvironmentVariable(BridgeDllEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv!;
        return Path.Combine(AppContext.BaseDirectory, BridgeDllName);
    }

    /// <summary>
    /// 桥 DLL 探针（I1/I2）：返回解析路径与问题词——null=可加载、"dll-missing"=不存在、
    /// "stub"=空桩产物。纯只读（File.Exists + 字节扫描），不启动进程、不触 COM。
    /// </summary>
    internal static (string DllPath, string? Problem) ProbeBridgeDll()
    {
        string dll = ResolveBridgeDllPath();
        if (!File.Exists(dll)) return (dll, "dll-missing");
        if (StubScan(dll)) return (dll, "stub");
        return (dll, null);
    }

    /// <summary>空桩扫描缝（默认=真实字节扫描；单测注入两态驱动拒绝面）。</summary>
    internal static Func<string, bool> StubScan = IsStubAssembly;

    /// <summary>
    /// 空桩识别（I2 裁定）：读全文件（桥产物 ~几十 KB）做 ASCII 与 UTF-16 双编码标记检索——
    /// 真桥含 READ / EXPLODE_REPLACE 命令标记**任一**即非空桩（.NET 元数据中命令字面量可能以
    /// UTF-8 自定义特性 blob 或 UTF-16 用户串堆出现，故双编码都查）；两者皆无且含
    /// <see cref="StubMarker"/> 才是空桩。文件不可读→false（不在此误判，交给后续 NETLOAD
    /// 的响亮失败/指引）。
    /// </summary>
    internal static bool IsStubAssembly(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return false; }
        string ascii = System.Text.Encoding.ASCII.GetString(bytes);
        string utf16 = System.Text.Encoding.Unicode.GetString(bytes);
        bool Has(string marker) => ascii.Contains(marker, StringComparison.Ordinal)
            || utf16.Contains(marker, StringComparison.Ordinal);
        if (Has(BridgeChannel.ReadCommand) || Has(BridgeChannel.ExplodeReplaceCommand)) return false;
        return Has(StubMarker);
    }

    /// <summary>TRUSTEDPATHS 只读现值（注册表优先、零实例零 COM；无值→null）。
    /// 用途：① <see cref="CheckBridgeAvailability"/> 的**硬门**判据（深读前拒绝面）；② CLI 深读会话的
    /// 信任位置快照（加性审计，I4）。两用途同源同值（M1 口径更正：不是"仅信息性"）。</summary>
    public static string? CurrentTrustedPaths() => TrustedPathsSource(TArchProfileName);

    /// <summary>
    /// 信任目录判定（纯函数，仅路径/目录只读探测）：TRUSTEDPATHS 分号分隔、条目可带引号/环境变量；
    /// 条目本身或其**子目录**受信（AutoCAD 信任位置含子目录），比较 OrdinalIgnoreCase + 全路径归一。
    /// M5（PR#30 终审）：盘根条目（<c>C:\</c>/<c>C:/</c>）= **整盘信任**——这是列盘根的自然语义，
    /// 改为显式判据（目标首字符为同一盘符即命中），不再依赖 <c>"C:" + StartsWith("C:\\")</c> 的
    /// 隐式归一匹配；同时**残缺形态 <c>C:</c>（无分隔符，用户手写）不再被 <c>GetFullPath</c> 解析成
    /// "该盘当前目录"后悄悄放行**——此类条目按非法处理，不匹配任何路径（fail-closed）。
    /// </summary>
    public static bool IsTrustedDirectory(string? trustedPaths, string directory)
    {
        if (string.IsNullOrEmpty(trustedPaths)) return false;
        string target = NormalizeTrustedPath(directory);
        foreach (string raw in trustedPaths.Split(';'))
        {
            string entry = raw.Trim().Trim('"');
            if (entry.Length == 0) continue;
            try { entry = Environment.ExpandEnvironmentVariables(entry); } catch { /* 保留原串 */ }
            entry = entry.Trim();
            if (entry.Length == 0) continue;
            // 盘根条目（"C:\"/"C:/"）：整盘信任（显式判据，见上）。
            if (IsDriveRootEntry(entry, out char drive))
            {
                if (MatchesDrive(target, drive)) return true;
                continue;
            }
            // 残缺盘符条目（"C:"）：非法，不匹配（绝不退化为"该盘当前目录"）。
            if (IsBareDriveEntry(entry)) continue;
            string trusted = NormalizeTrustedPath(entry);
            if (trusted.Length == 0) continue;
            if (string.Equals(target, trusted, StringComparison.OrdinalIgnoreCase)) return true;
            if (target.StartsWith(trusted + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 盘根条目判定：恰好 "<盘符>:\" 或 "<盘符>:/"（3 字符，无尾部路径分量）。
    private static bool IsDriveRootEntry(string entry, out char drive)
    {
        drive = default;
        if (entry.Length != 3 || entry[1] != ':') return false;
        if (entry[2] != '\\' && entry[2] != '/') return false;
        if (!char.IsAsciiLetter(entry[0])) return false;
        drive = char.ToUpperInvariant(entry[0]);
        return true;
    }

    // 残缺盘符条目判定：恰好 "<盘符>:"（用户手写无分隔符）——非法，不参与匹配。
    private static bool IsBareDriveEntry(string entry) =>
        entry.Length == 2 && entry[1] == ':' && char.IsAsciiLetter(entry[0]);

    // 目标是否在指定盘上（盘根信任的命中判据）：归一后形如 "<盘符>:\..." 或 "<盘符>:"。
    private static bool MatchesDrive(string target, char drive) =>
        target.Length >= 2 && target[1] == ':' && char.ToUpperInvariant(target[0]) == drive;

    private static string NormalizeTrustedPath(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* 非法路径→按原串比较 */ }
        return path.TrimEnd('\\', '/');
    }

    /// <summary>
    /// 桥可用性预检（切片B 复审修复波 I1）：起会话前 fail-fast——解析桥 DLL（覆盖缝/环境变量/
    /// 部署目录）、识别空桩、判定桥目录是否在 TRUSTEDPATHS。**纯只读、零实例、零 COM**：
    /// 深读（NETLOAD）一旦在桥缺失/空桩/不受信时开跑，要么"桥未构建"伪失败、要么安全框阻塞
    /// 至 3600s——故必须在 Open/NETLOAD 之前给出可执行指引并拒绝。
    /// </summary>
    public static BridgeAvailability CheckBridgeAvailability()
    {
        var (dll, problem) = ProbeBridgeDll();
        if (problem == "dll-missing")
            return new BridgeAvailability(false, dll, problem,
                "请先构建桥（需 CADAGENT_ACMG_DIR 下 AcMgd/AcDbMgd/AcCoreMgd），"
                + "或设置 CADAGENT_BRIDGE_DLL 指向已构建的桥 DLL");
        if (problem == "stub")
            return new BridgeAvailability(false, dll, problem,
                StubBridgeMessage + "；或设置 CADAGENT_BRIDGE_DLL 指向真桥产物");

        string? trusted = CurrentTrustedPaths();
        string dir;
        try { dir = Path.GetDirectoryName(dll) ?? dll; }
        catch { dir = dll; }
        if (!IsTrustedDirectory(trusted, dir))
            return new BridgeAvailability(false, dll, "untrusted",
                "桥目录不在 TRUSTEDPATHS（桥目录：" + dir + "；当前值："
                + (string.IsNullOrEmpty(trusted) ? "<空>" : trusted) + "）——请把桥目录加入信任位置"
                + "（AutoCAD 选项→文件→信任位置，需重启 AutoCAD），否则 NETLOAD 安全框最长阻塞 3600s");
        return new BridgeAvailability(true, dll, null, null);
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
    /// 的值 TRUSTEDPATHS（REG_SZ）；枚举 ACAD-* 子键，选择候选见
    /// <see cref="SelectTrustedPathsCandidate"/>（PROFILE 命中优先，其次首个非 null 值）；
    /// 全无/不可读→null。空串=「已配置但为空」，与「无此值」语义不同，交调用方如实披露。
    /// 只读访问：绝不写注册表、不改任何 AutoCAD 设置。
    /// M6（PR#30 终审）：多产品子键（同机多 ACAD-* 安装/语言包）不再"首个非 null 即返回"，
    /// 而是优先取**确实含目标 profile** 的子键（避免拿到无关产品的同名 Variables 值）。
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
            // 候选定序（Ordinal）：枚举顺序不承诺稳定，候选选择须确定（回退"首个"亦确定）。
            var products = root.GetSubKeyNames()
                .Where(n => n.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (products.Count == 0) return null;
            return SelectTrustedPathsCandidate(products, product =>
            {
                using RegistryKey? profile = root.OpenSubKey(product + @"\Profiles\" + profileName);
                if (profile == null) return (false, null);
                using RegistryKey? variables = profile.OpenSubKey("Variables");
                return (true, variables?.GetValue(TrustedPathsVariableName) as string);
            });
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                   or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;   // 只读探测失败按「无该值」处理：回退 COM 兜底 / 守卫如实 Skip，绝不抛
        }
#pragma warning restore CA1416
    }

    /// <summary>
    /// 多产品子键候选选择（纯函数，M6）：按给定顺序逐个探测——
    /// ① 子键含目标 profile 且值为非 null（含空串=已配置但为空）→ 立即返回该值（PROFILE 命中优先）；
    /// ② 不含目标 profile 的子键不参与"命中"，但其非 null 值记作回退候选；
    /// ③ 无任何 PROFILE 命中 → 返回首个非 null 回退值（旧"首个非 null 即返回"行为，仅作兜底）；全无→null。
    /// </summary>
    internal static string? SelectTrustedPathsCandidate(
        IReadOnlyList<string> acadSubkeys,
        Func<string, (bool HasProfile, string? Value)> probe)
    {
        string? fallback = null;
        foreach (string key in acadSubkeys)
        {
            var (hasProfile, value) = probe(key);
            if (!hasProfile)
            {
                fallback ??= value;
                continue;
            }
            if (value != null) return value;
        }
        return fallback;
    }

    // 注册表读取缝（R17）：默认=ReadTrustedPathsFromRegistry；单测注入以钉住
    // 「注册表命中→零 COM/零实例」，注入 (_ => null) 则强制走 COM 回退分支（复现旧行为）。
    internal static Func<string, string?> TrustedPathsSource = ReadTrustedPathsFromRegistry;

    /// <summary>
    /// TRUSTEDPATHS 现值（<see cref="CheckBridgeAvailability"/> 的硬门判据 + 深读会话快照；真机测试
    /// 据此决定 Skip/运行）。R17：**注册表优先**——不启动实例；仅当注册表无该 profile/值时才回退 COM
    /// 实读（回退须 /p 拉起实例：启动初始化期 Quit 被忽略会留孤儿，故只作兜底而非主路）。
    /// M1 口径更正：预检已从"信息性、不阻断"收紧为**硬门**（native-live 不受信→exit 1；
    /// `semantic scan --explode`→跳过深读并留痕），见设计 §7.1(f)。
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

    // 安全框探测（F4）：仅面向本方 /p 进程 PID；异常→false（诊断增强不升级为失败）。
    // 与看门狗同一探测缝（SecurityDialogDetector），但由 BridgeChannel 监视线程按节拍调用，
    // 命中即让 20s 指引文案附加"（已检测到安全框）"。
    private Func<bool> BridgeSecurityDialogProbe()
    {
        int pid;
        try { pid = _launchedProc?.Id ?? 0; } catch { pid = 0; }
        return () =>
        {
            try { return SecurityDialogDetector(pid); } catch { return false; }
        };
    }

    // NETLOAD+探针（同实例仅一次；成功后 _bridgeLoaded 置位）。
    private void EnsureBridgeLoaded(string channelDir)
    {
        if (_bridgeLoaded) return;
        BridgeChannel.EnsureLoaded(
            BridgeDllPath(), channelDir, BridgeNetLoadWatchdog(channelDir),
            NetLoadTimeout, NetLoadNoProgressThreshold, NetLoadSlowGuidance,
            BridgeSecurityDialogProbe());
        _bridgeLoaded = true;
    }

    // 桥结果 → Core 证据列表（纯映射：无 COM、无文件 IO，可单测）。
    // 口径：PrimitiveCount=该句柄 PRIM 行数（PRIM-TRUNCATED 截断标记不计，非有限坐标基元不计）；
    //       NestedTdbCount=该句柄"递归超深/非实体产物"失败行数（桥不输出残留 TDb 行，
    //       这两类失败即"炸不动的残留"上界；其余失败为 extents/xdata/explode 类，不计残留）；
    //       CenterlineJson 由宿主推导（TarchDerive，Task 6）——此处恒 null；
    //       XData：1001 行=天正键名（探针实证 HYDRANT_RADIUS），随后各值行归入该键。
    // C-I3（技术债批次C）非有限卫生：NaN/∞ 进 overlay JSON 会让 System.Text.Json 抛异常
    //（overlay 静默保存失败）——故在映射边界清洗：bbox 任一分量非有限 → 整盒置 null 并落
    // "bbox:nonfinite" 去因；基元 Points 含非有限 → 整条丢弃，按句柄追加一次
    // "prims:nonfinite:{n}"（n=丢弃数）。D（微修复波D）：两类去因同口径——无既有 FAIL 时
    // **均补为 FailReason 首条**（仅追加不覆盖）。清洗后可复核（原因留在证据行）。
    internal static IReadOnlyList<ExplodeEvidence> MapExplodeEvidence(BridgeResult result)
    {
        var now = DateTimeOffset.UtcNow;
        var blockQueues = GroupBlocksByHandle(result.Blocks);
        var list = new List<ExplodeEvidence>(result.Evidence.Count);
        foreach (var e in result.Evidence)
        {
            var prims = new List<ExplodePrimitive>();
            int droppedPrims = 0;
            foreach (var p in result.Prims)
            {
                if (!HandleEquals(p.Handle, e.Handle)) continue;
                if (string.Equals(p.RxClass, "PRIM-TRUNCATED", StringComparison.Ordinal)) continue;
                var prim = MapPrimitive(p, blockQueues);
                if (HasNonFinite(prim.Points)) { droppedPrims++; continue; }   // C-I3：NaN/∞ 坐标不得进 overlay
                // M6（微修复波E）：整条丢弃**不回写** FullyExplodable/PrimitiveCount 之外的元数据——
                // PrimitiveCount 只计保留基元；FullyExplodable 保持桥原值不改。当前无消费者依赖
                // "丢弃后 FullyExplodable 是否转 false"，风险低（如需台账可加加性字段）。
                prims.Add(prim);
            }
            double[]? bbox = e.Bbox;
            bool bboxNonFinite = bbox != null && HasNonFinite(bbox);
            if (bboxNonFinite) bbox = null;
            string? firstReason = FirstFailReason(result.Fails, e.Handle);
            List<string>? allReasons = AllFailReasons(result.Fails, e.Handle) is { } r
                ? new List<string>(r)
                : null;
            if (bboxNonFinite)
            {
                (allReasons ??= new List<string>()).Add("bbox:nonfinite");
                firstReason ??= "bbox:nonfinite";   // 首条语义：无既有 FAIL 时该原因即首条
            }
            if (droppedPrims > 0)
            {
                (allReasons ??= new List<string>()).Add($"prims:nonfinite:{droppedPrims}");
                // D（微修复波D）：与 bbox 同口径——无既有 FAIL 时该去因即首条（仅追加不覆盖）。
                firstReason ??= $"prims:nonfinite:{droppedPrims}";
            }
            list.Add(new ExplodeEvidence(
                e.Handle, e.RxClass, e.WrapperType, e.Layer, bbox, e.FullyExplodable,
                prims.Count, CountResidualTdb(result.Fails, e.Handle),
                prims, MapXData(result.XData, e.Handle), null, now,
                // A2（边界补齐）：该句柄**首条** FAIL 原因（extents:degenerate / extents:Exception /
                // explode:eInvalidInput 等）如实透出；无失败行=null。宿主不做二次推断。
                firstReason,
                // PR32：该句柄**全部** FAIL 原因（按桥输出序；加性 JSON 尾字段）——首条之外
                // 的失败同样可复核（如 SymbArrow 首条 extents:Exception + 次条 explode:eInvalidInput）。
                allReasons));
        }
        return list;
    }

    /// <summary>C-I3：坐标序列是否含非有限分量（NaN/±∞）。</summary>
    private static bool HasNonFinite(double[] values)
    {
        foreach (double v in values)
            if (!double.IsFinite(v)) return true;
        return false;
    }

    /// <summary>该句柄首条 FAIL 行原因（出现序=桥输出序；无匹配→null）。</summary>
    internal static string? FirstFailReason(IReadOnlyList<FailDto> fails, string handle)
    {
        foreach (var f in fails)
            if (HandleEquals(f.Handle, handle)) return f.Reason;
        return null;
    }

    /// <summary>该句柄全部 FAIL 行原因（桥输出序保序；无匹配→null，与旧 overlay 反序列化口径一致）。</summary>
    internal static IReadOnlyList<string>? AllFailReasons(IReadOnlyList<FailDto> fails, string handle)
    {
        List<string>? list = null;
        foreach (var f in fails)
            if (HandleEquals(f.Handle, handle)) (list ??= new List<string>()).Add(f.Reason);
        return list;
    }

    /// <summary>B1：BDEF 记录 → Core 块定义段（纯映射；块定义计数 ≠ 模型空间实例数）。</summary>
    internal static IReadOnlyList<BlockDefTdbEntry> MapBlockDefs(BridgeResult result)
    {
        var list = new List<BlockDefTdbEntry>(result.BlockDefs.Count);
        foreach (var d in result.BlockDefs)
            list.Add(new BlockDefTdbEntry(d.BlockName, d.RxClass, d.Count));
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
    /// A3（技术债批次A）：<paramref name="includeBlockDefs"/> 随请求下发（协议键 <c>bdef</c>）——
    /// true 才让桥扫描块定义段并在 Close 成功后缓存（见 <see cref="ReadExplodeBlockDefs"/>）；
    /// false（native-live 等不消费定义段的调用面）不付该成本、也不产生可命中的定义段缓存。
    /// </summary>
    public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath, bool includeBlockDefs = false) =>
        Run<IReadOnlyList<ExplodeEvidence>>(() =>
        {
            if (drawingPath == null) throw new CadRuntimeException("无打开的图纸参数");
            if (_doc != null)
                throw new CadRuntimeException("已有打开的图纸；ReadExplodeEvidence(path) 须在无打开文档时调用");

            // B1：任何一次读取都会使上一轮块定义缓存失效（失败路径不得残留旧块定义表）。
            InvalidateBlockDefCache();
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
                    new BridgeRequest("read", Path.GetFileName(drawingPath), null, BridgeReadMaxDepth, null)
                    {
                        IncludeBlockDefs = includeBlockDefs
                    }.Write());
                BridgeChannel.SendCommand(BridgeChannel.ReadCommand, "cmd");
                string? text = BridgeChannel.WaitDone(channel, id, BridgeChannel.CommandWait);
                if (text == null)
                    throw new CadRuntimeException(
                        $"桥命令超时（{(int)BridgeChannel.CommandWait.TotalSeconds}s）：{BridgeChannel.ReadCommand}");
                var result = BridgeResult.Parse(text);
                if (!string.Equals(result.Status, "ok", StringComparison.Ordinal))
                    throw new CadRuntimeException("桥失败: " + (result.Error ?? result.Status));
                var list = MapExplodeEvidence(result);
                // A3：未索取定义段时桥不输出 BDEF 行（也不缓存）——空表只是占位，由 _lastBlockDefsRequested 区分。
                var blockDefs = includeBlockDefs ? MapBlockDefs(result) : Array.Empty<BlockDefTdbEntry>();
                CloseTArchDoc();                         // 读完即关（失败响亮上抛）
                // M-3(b)（修复波1）：缓存写入必须在 Close 成功**之后**——Close 失败时本读取整体
                // 上抛，块定义缓存保持失效（否则一次失败会话的块定义会被 ReadExplodeBlockDefs
                // 当有效读数返回）。B1：块定义段与证据同源同会话，路径按归一化形态记录。
                _lastExplodePath = NormalizeDrawingPath(drawingPath);
                _lastBlockDefs = blockDefs;
                _lastBlockDefsRequested = includeBlockDefs;
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
    /// 块定义内 TDb 聚合（边界补齐 B1）：与 <see cref="ReadExplodeEvidence"/> **同一次桥会话**的
    /// 伴生读数，由实例缓存直接返回（不二次开图、不发命令）。缓存语义：
    /// ① <c>ReadExplodeEvidence(path)</c> 读成功且 <c>Close</c> 成功后缓存归一化路径与本次块定义表；
    /// ② 读取失败/Close 失败/再次读取即失效（失败会话不残留旧表）；
    /// ③ 本方法仅在路径与上次成功读数相等（OrdinalIgnoreCase）时返回缓存，否则**响亮拒绝**——
    ///    绝不隐式二次开图（两次 Open = 双倍耗时且多一个忙窗面，与"一次会话一份事实"相悖）；
    /// ⑤ A3（技术债批次A）：上次读数**未索取**块定义段（<c>includeBlockDefs=false</c>）时同样响亮拒绝
    ///    （"本轮没读块定义"与"读了但零定义"是不同事实，不得用空表冒充）；
    /// ④ <c>Dispose</c> 清空缓存（实例销毁后不得返回上一会话读数）。
    /// 调用约定：CLI `semantic scan --explode` 在 `ReadExplodeEvidence(drawingPath, includeBlockDefs: true)`
    /// 之后以同一 path 调用本方法（必然缓存命中）。native-live 不取块定义段（无 Enrich 消费面）。
    /// </summary>
    public IReadOnlyList<BlockDefTdbEntry> ReadExplodeBlockDefs(string drawingPath)
    {
        if (drawingPath == null) throw new CadRuntimeException("无打开的图纸参数");
        string path = NormalizeDrawingPath(drawingPath);
        if (_lastExplodePath != null
            && string.Equals(path, _lastExplodePath, StringComparison.OrdinalIgnoreCase))
        {
            if (_lastBlockDefsRequested) return _lastBlockDefs;
            throw new CadRuntimeException(
                "块定义缓存缺失：上次 ReadExplodeEvidence 未请求块定义段（includeBlockDefs=false）"
                + "——须以 includeBlockDefs:true 重读（不隐式二次开图）");
        }
        throw new CadRuntimeException(
            "块定义缓存缺失：ReadExplodeBlockDefs(path) 须紧接同 path 的 ReadExplodeEvidence(path) 之后调用"
            + "（不隐式二次开图）");
    }

    private void InvalidateBlockDefCache()
    {
        _lastExplodePath = null;
        _lastBlockDefs = Array.Empty<BlockDefTdbEntry>();
        _lastBlockDefsRequested = false;
    }

    /// <summary>路径归一化（缓存键）：可行时取全路径，异常（非法字符等）按原样——比较两侧同法归一。</summary>
    private static string NormalizeDrawingPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

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
            string? text = BridgeChannel.WaitDone(channel, id, BridgeChannel.CommandWait);
            if (text == null)
                throw new CadRuntimeException(
                    $"桥命令超时（{(int)BridgeChannel.CommandWait.TotalSeconds}s）：{BridgeChannel.ExplodeReplaceCommand}");
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
        // M-3(a)（修复波1）：实例销毁=桥会话结束——块定义缓存随会话清空，
        // 已销毁实例不得再返回上一会话的读数（缓存生命周期与通道目录/桥加载标记同界）。
        InvalidateBlockDefCache();
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
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(10));
        // Quit 返回≠进程退出：CUIX 回写发生在退出尾段（PR#17 机制，厂商无关）。
        VendorGuard.WaitForProcessExit(Profile, VendorGuard.ExitWait);
        // M1（复审修复波）：通道收尾**必须**在进程退出之后——桥进程可能仍持通道目录句柄，
        // 先删目录会与退出尾段竞态（删除失败残留/孤儿写回）。最后一个对象也随之退出：
        // 实例销毁=桥会话结束，故删目录与还原环境变量固定收在退出等待之后。
        CleanupChannel();
    }
}

/// <summary>
/// 桥可用性预检结果（切片B 复审修复波 I1）：Available=false 时 <paramref name="Problem"/> ∈
/// <c>dll-missing</c>|<c>stub</c>|<c>untrusted</c>，<paramref name="Guidance"/> 为用户可执行下一步。
/// 纯只读结论（零实例、零 COM）——供 CLI 在 Open/NETLOAD 之前 fail-fast（杜绝 3600s 阻塞）。
/// </summary>
public sealed record BridgeAvailability(bool Available, string? DllPath, string? Problem, string? Guidance);
