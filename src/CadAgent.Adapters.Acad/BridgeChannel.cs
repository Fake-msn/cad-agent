using System.Diagnostics;
using System.Globalization;
using CadAgent.Core.Cad;
using CadAgent.TArchBridge.Protocol;

namespace CadAgent.Adapters.Acad;

/// <summary>
/// NETLOAD 桥通道（spec 切片B §2/§5）：文件三件套（req-/res-/done）写入、.done 轮询、
/// NETLOAD 看门狗（3600s 上限 + 20s 无进展指引）与"误拒"归因。
/// 本类不持有 COM 对象：命令发送经静态缝 <see cref="SendCommand"/>，由 AcadRuntime 在
/// 每个公开入口注入（委托捕获该实例的文档）。**同进程仅支持单运行时实例**（CLI 单实例、
/// 测试串行）——多实例交替注入会互相覆盖该缝（Ruling R6）。
/// 全部成员 internal：生产调用方只在 CadAgent.Adapters.Acad 内，测试经 InternalsVisibleTo。
/// </summary>
internal static class BridgeChannel
{
    /// <summary>READ 命令名（与桥 CommandMethod 逐字一致）。</summary>
    internal const string ReadCommand = "CADAGENT_TARCH_READ";

    /// <summary>EXPLODE_REPLACE 命令名（与桥 CommandMethod 逐字一致）。</summary>
    internal const string ExplodeReplaceCommand = "CADAGENT_TARCH_EXPLODE_REPLACE";

    /// <summary>探针请求的 dwg 哨兵名：与当前文档名必然不符，桥会立即回 status fail 并写 done。</summary>
    internal const string ProbeDwg = "__probe__";

    /// <summary>误拒明文归因（规格逐字：点击【不加载】/TRUSTEDPATHS/SECURELOAD 三因）。</summary>
    internal const string DeniedMessage =
        "NETLOAD 被拒绝：可能点击【不加载】/TRUSTEDPATHS 未含桥目录/SECURELOAD 阻止";

    /// <summary>
    /// 硬超时明文（规格逐字）：监视线程在 NETLOAD 总预算耗尽且 SendCommand 仍阻塞时触发
    /// （宿主已终止本方 AutoCAD 会话），随后的 COM 异常据此包装。
    /// </summary>
    internal const string NetLoadHardTimeoutMessage =
        "NETLOAD 超时（3600s）：已终止本方 AutoCAD 会话";

    /// <summary>NETLOAD 请求标记文件（诊断用：证明本次 NETLOAD 已发出）。</summary>
    internal const string NetLoadRequestMarker = "netload.req";

    /// <summary>NETLOAD 已返回标记：SendCommand 同步语义下，返回即 NETLOAD 已结束（加载或拒绝）。</summary>
    internal const string NetLoadSentMarker = "netload.sent";

    /// <summary>
    /// 命令发送缝：<c>(command, echoMark) → 回显</c>。echoMark 区分 netload/probe/cmd。
    /// 默认实现响亮拒绝——必须由 AcadRuntime 入口注入（本类不持 COM）。
    /// </summary>
    internal static Func<string, string, string> SendCommand =
        (_, _) => throw new CadRuntimeException("桥通道未注入 SendCommand（AcadRuntime 公开入口负责注入）");

    /// <summary>完成标记轮询间隔（spec §5：500ms）。</summary>
    internal static TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>注册探针预算：NETLOAD 返回后 10s 内探针仍无 done → 判"命令未注册"（误拒）。</summary>
    internal static TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 桥命令（READ / EXPLODE_REPLACE）结果等待上限（spec §5：120s）。切片B 复审修复波 I6：
    /// 从 AcadRuntime 移到本类成统一测试缝——单测压到 200ms 即可确定性驱动"命令超时"分支，
    /// 无需真等 120s；生产默认值不变。
    /// </summary>
    internal static TimeSpan CommandWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 硬超时处理器（复审 F3）：宿主（AcadRuntime）在桥入口注入，默认实现=终止**本方**经 /p
    /// 启动的进程（经既有 TarchProcessCleanup 缝）并置标志，使随后从阻塞 SendCommand 抛出的
    /// COM 异常被包装为 <see cref="NetLoadHardTimeoutMessage"/>。null=不武装硬超时
    /// （无终止能力时不得声称"已终止会话"，仍由软超时/调用方降级兜底）。
    /// </summary>
    internal static Action? HardTimeoutHandler;

    /// <summary>
    /// NETLOAD 总预算测试缝（null=用调用方传入值，生产 3600s）。仅供单测把 3600s 压低到毫秒级，
    /// 与软超时/硬超时同口径生效。
    /// </summary>
    internal static TimeSpan? NetLoadTimeoutOverride;

    /// <summary>监视线程轮询切片（指引/硬超时的判定粒度；20ms 足够，测试据此确定性收敛）。</summary>
    private static readonly TimeSpan MonitorSlice = TimeSpan.FromMilliseconds(20);

    private static readonly System.Text.Encoding Utf8NoBom =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 写请求文件 <c>req-&lt;id&gt;.txt</c>（id=目录内现有最大数字+1，1 起），返回 id。
    /// 目录不存在时先创建（跨实例复用同一通道目录时幂等）。
    /// 写前先<b>放弃</b>目录内所有"有 req 无 done"的历史请求：通道目录改为实例级复用（复审 F1）
    /// 后，超时/异常路径留下的请求不会被清理，桥的 NextPending 恒拾取最早那条旧请求——
    /// 新请求将永远轮不到（第二次桥操作必超时）。标记 done 即让桥跳过该旧请求。
    /// </summary>
    internal static string WriteRequest(string channelDir, string requestText)
    {
        if (string.IsNullOrEmpty(channelDir)) throw new CadRuntimeException("桥通道目录为空");
        Directory.CreateDirectory(channelDir);
        int max = 0;
        foreach (var f in Directory.GetFiles(channelDir, "req-*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length <= 4) continue;
            string existing = name.Substring(4);
            if (!int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
            if (n > max) max = n;
            AbandonPendingRequest(channelDir, existing);
        }
        string id = (max + 1).ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(channelDir, "req-" + id + ".txt"), requestText, Utf8NoBom);
        return id;
    }

    // 放弃历史未完成请求：先落结果文件再置 done（与桥的"结果先、done 后"顺序一致），
    // 使桥的 NextPending 跳过它；失败 best-effort（真超时仍由调用方 120s 兜底）。
    private static void AbandonPendingRequest(string channelDir, string id)
    {
        string done = Path.Combine(channelDir, "res-" + id + ".done");
        if (File.Exists(done)) return;
        try
        {
            File.WriteAllText(Path.Combine(channelDir, "res-" + id + ".txt"),
                "status\tfail\nerror\t宿主已放弃（超时/失败）\n", Utf8NoBom);
            File.WriteAllText(done, "1", Utf8NoBom);
        }
        catch { /* best-effort：写入失败时由本次请求的超时兜底 */ }
    }

    /// <summary>
    /// 轮询 <c>res-&lt;id&gt;.done</c>（间隔 <see cref="PollInterval"/>，最多 <paramref name="timeout"/>）：
    /// done 出现→返回 <c>res-&lt;id&gt;.txt</c> 全文；超时→null（调用方 fail-closed）。
    /// 结果尚未落盘（done 先于文件可见的极端窗口）/占用→下一轮再读。
    /// </summary>
    internal static string? WaitDone(string channelDir, string id, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(channelDir)) throw new CadRuntimeException("桥通道目录为空");
        string done = Path.Combine(channelDir, "res-" + id + ".done");
        string res = Path.Combine(channelDir, "res-" + id + ".txt");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(done))
            {
                try { return File.ReadAllText(res); }
                catch (IOException) { /* 写入窗口：下一轮再读 */ }
                catch (UnauthorizedAccessException) { /* 杀软/索引占用：下一轮再读 */ }
            }
            if (sw.Elapsed >= timeout) return null;
            var remaining = timeout - sw.Elapsed;
            Thread.Sleep(remaining < PollInterval ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : PollInterval);
        }
    }

    /// <summary>通道目录内是否已出现任一结果（进展信号；异常→false，看门狗不因读目录失败中断）。</summary>
    internal static bool AnyDone(string channelDir)
    {
        try
        {
            return !string.IsNullOrEmpty(channelDir)
                && Directory.Exists(channelDir)
                && Directory.GetFiles(channelDir, "res-*.done").Length > 0;
        }
        catch { return false; }
    }

    /// <summary>NETLOAD 是否已返回（见 <see cref="NetLoadSentMarker"/>）。</summary>
    internal static bool NetLoadSent(string channelDir)
    {
        try
        {
            return !string.IsNullOrEmpty(channelDir)
                && File.Exists(Path.Combine(channelDir, NetLoadSentMarker));
        }
        catch { return false; }
    }

    /// <summary>
    /// NETLOAD LISP 外壳：<c>(command "NETLOAD" "&lt;dll&gt;")</c>。
    /// LISP 字符串转义：反斜杠须加倍（Windows 路径），双引号转义（路径含引号属非法输入但不得破协议）。
    /// </summary>
    internal static string NetLoadLisp(string bridgeDll) =>
        "(command \"NETLOAD\" \"" + LispEscape(bridgeDll) + "\")";

    /// <summary>LISP 字符串字面量转义（反斜杠→\\、引号→\"）。</summary>
    internal static string LispEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// 发送 NETLOAD 并等待"已返回/有进展"：
    /// ① 写 <see cref="NetLoadRequestMarker"/> 请求标记；
    /// ② 启监视线程（复审 F3）：SendCommand 在 STA 内**同步阻塞**（COM 调用不返回），故
    ///    20s 无进展指引与 3600s 硬超时只能由独立后台线程施加——
    ///    · 到达 <paramref name="noProgressThreshold"/>：经 <paramref name="slowGuidance"/> 打印一次指引；
    ///    · 到达总预算（<see cref="NetLoadTimeoutOverride"/> ?? <paramref name="netLoadTimeout"/>）时
    ///      发送仍未返回：调用 <see cref="HardTimeoutHandler"/>（终止本方会话）→ 随后的发送异常
    ///      包装为 <see cref="NetLoadHardTimeoutMessage"/>；
    ///    发送返回即取消监视线程（快速返回不打印指引、不误报硬超时）。
    ///    F4：<paramref name="securityDialogProbe"/> 非空时监视线程按节拍探测安全框，
    ///    命中至少一次 → 指引文案加性附加 <see cref="SecurityDialogSeenSuffix"/>（只一次）。
    /// ③ 写 <see cref="NetLoadSentMarker"/>（发送已返回=NETLOAD 已结束，加载/拒绝由探针判定）；
    /// ④ 轮询 <paramref name="watchdog"/>：true=有进展→返回；超时→软超时 <see cref="CadRuntimeException"/>。
    /// 说明：软超时循环只覆盖"发送已返回但看门狗仍无进展"的场景；发送阻塞期由②兜底。
    /// </summary>
    internal static void EnsureNetLoad(
        string bridgeDll,
        string channelDir,
        Func<TimeSpan, bool> watchdog,
        TimeSpan netLoadTimeout,
        TimeSpan? noProgressThreshold = null,
        Action<string>? slowGuidance = null,
        Func<bool>? securityDialogProbe = null)
    {
        if (string.IsNullOrWhiteSpace(bridgeDll)) throw new CadRuntimeException("桥 DLL 路径为空");
        if (string.IsNullOrEmpty(channelDir)) throw new CadRuntimeException("桥通道目录为空");
        Directory.CreateDirectory(channelDir);
        File.WriteAllText(Path.Combine(channelDir, NetLoadRequestMarker), bridgeDll, Utf8NoBom);

        TimeSpan timeout = NetLoadTimeoutOverride ?? netLoadTimeout;
        TimeSpan? guideAfter = noProgressThreshold is TimeSpan t && t > TimeSpan.Zero ? t : null;
        string? guideText = slowGuidance != null ? GuidanceText(bridgeDll, channelDir) : null;
        TimeSpan? hardAfter = HardTimeoutHandler != null ? timeout : null;

        int sendReturned = 0;
        int hardFired = 0;
        // 不 Dispose：监视线程可能仍停在 WaitHandle.WaitOne 上，Dispose 会把取消句柄抽走
        // （ObjectDisposedException 在本线程=进程级未处理异常）；只用 Cancel 收敛。
        var monitor = new CancellationTokenSource();
        if (guideText != null || hardAfter != null)
        {
            StartNetLoadMonitor(monitor.Token, guideAfter, guideText, slowGuidance, hardAfter,
                () => Volatile.Read(ref sendReturned) != 0,
                () =>
                {
                    if (Interlocked.Exchange(ref hardFired, 1) != 0) return;
                    try { HardTimeoutHandler?.Invoke(); }
                    catch { /* 终止本方会话 best-effort：失败仍按硬超时响亮上抛 */ }
                },
                securityDialogProbe);
        }

        try { SendCommand(NetLoadLisp(bridgeDll), "netload"); }
        catch (Exception ex) when (Volatile.Read(ref hardFired) != 0)
        { throw new CadRuntimeException(NetLoadHardTimeoutMessage, ex); }
        finally
        {
            Volatile.Write(ref sendReturned, 1);
            monitor.Cancel();
        }
        // 硬超时后发送仍"正常"返回（进程已被终止的竞态）：不得当成加载成功继续探针。
        if (Volatile.Read(ref hardFired) != 0) throw new CadRuntimeException(NetLoadHardTimeoutMessage);

        File.WriteAllText(Path.Combine(channelDir, NetLoadSentMarker), "1", Utf8NoBom);

        var sw = Stopwatch.StartNew();
        while (!SafeWatchdog(watchdog, sw.Elapsed))
        {
            if (sw.Elapsed >= timeout)
                throw new CadRuntimeException(
                    $"NETLOAD 超时（{(int)timeout.TotalSeconds}s）：可能安全弹窗未处理");
            Thread.Sleep(PollInterval);
        }
    }

    // 监视线程（复审 F3）：发送阻塞期间唯一能观察墙钟的线程。发送返回/取消即退出；
    // 指引只回调一次；硬超时在总预算到点且发送仍未返回时触发一次。异常一律吞掉
    // （诊断增强不得替代主路径的 fail-closed 结论）。
    // F4：安全框探测（可空）按 DialogProbeInterval 节拍锁存——命中至少一次时指引文案
    // 加性附加 <see cref="SecurityDialogSeenSuffix"/>（原文案其余部分逐字不变）。
    private static void StartNetLoadMonitor(
        CancellationToken token,
        TimeSpan? guideAfter,
        string? guideText,
        Action<string>? guidance,
        TimeSpan? hardAfter,
        Func<bool> sendReturned,
        Action onHardTimeout,
        Func<bool>? securityDialogProbe = null)
    {
        var thread = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            bool guided = false;
            bool dialogSeen = false;
            // 首发立即探测（模态框通常先于 NETLOAD 返回出现；测试据此确定性锁存），
            // 此后每 DialogProbeInterval 复测（弹窗晚到亦能命中）。
            if (securityDialogProbe != null)
            {
                try { dialogSeen = securityDialogProbe(); } catch { /* 探测失败=未检出 */ }
            }
            var lastDialogProbe = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (token.IsCancellationRequested || sendReturned()) return;
                    // F4：节拍探测（首发立即，随后每 DialogProbeInterval）；命中即锁存。
                    if (securityDialogProbe != null && lastDialogProbe.Elapsed >= DialogProbeInterval)
                    {
                        lastDialogProbe.Restart();
                        try { if (securityDialogProbe()) dialogSeen = true; } catch { /* 探测失败=未检出 */ }
                    }
                    if (!guided && guideAfter is TimeSpan after && sw.Elapsed >= after)
                    {
                        guided = true;
                        string text = dialogSeen ? guideText! + SecurityDialogSeenSuffix : guideText!;
                        try { guidance!(text); } catch { /* 指引打印失败不影响主路径 */ }
                    }
                    if (hardAfter is TimeSpan hard && sw.Elapsed >= hard)
                    {
                        onHardTimeout();
                        return;
                    }
                    if (token.WaitHandle.WaitOne(MonitorSlice)) return;
                }
            }
            catch
            {
                // 诊断线程的任何异常都不得升级为进程级未处理异常（主路径仍 fail-closed）。
            }
        })
        {
            IsBackground = true,
            Name = "cadagent-netload-watchdog",
        };
        thread.Start();
    }

    /// <summary>
    /// NETLOAD + 注册探针（两阶段握手）：
    /// ① <see cref="EnsureNetLoad"/>（看门狗）；
    /// ② 探针 READ：写 op=read/dwg="__probe__" 请求并发送 READ 命令——
    ///    命令已注册→桥因错图立即回 status fail 并写 done（done 出现即成功）；
    ///    命令未注册（被 NETLOAD 拒绝）→ AutoCAD 吞命令、通道无任何文件→
    ///    <see cref="ProbeTimeout"/> 内无 done→判误拒（<see cref="DeniedMessage"/>，Ruling R3）。
    /// </summary>
    internal static void EnsureLoaded(
        string bridgeDll,
        string channelDir,
        Func<TimeSpan, bool> watchdog,
        TimeSpan netLoadTimeout,
        TimeSpan? noProgressThreshold = null,
        Action<string>? slowGuidance = null,
        Func<bool>? securityDialogProbe = null)
    {
        EnsureNetLoad(bridgeDll, channelDir, watchdog, netLoadTimeout, noProgressThreshold,
            slowGuidance, securityDialogProbe);
        if (ProbeRegistered(channelDir, ProbeTimeout)) return;
        throw new CadRuntimeException(DeniedMessage);
    }

    /// <summary>
    /// 探针握手单轮：写探针请求→发 READ→等 done。返回 done 是否出现（任何 status 均算"已注册"——
    /// 探针 dwg 必然错图，桥回 fail 正是期望路径；"无 done"才代表命令未注册）。
    /// </summary>
    internal static bool ProbeRegistered(string channelDir, TimeSpan timeout)
    {
        string id = WriteRequest(channelDir, new BridgeRequest("read", ProbeDwg, null, 0, null).Write());
        SendCommand(ReadCommand, "probe");
        return WaitDone(channelDir, id, timeout) != null;
    }

    /// <summary>20s 无进展指引文案（含桥目录与 TRUSTEDPATHS 配置提示；CLI 注入打印）。</summary>
    internal static string GuidanceText(string bridgeDll, string channelDir)
    {
        string bridgeDir;
        try { bridgeDir = Path.GetDirectoryName(bridgeDll) ?? bridgeDll; }
        catch { bridgeDir = bridgeDll; }
        return "NETLOAD 已等待超过 " + (int)NoProgressGuideSeconds + " 秒：若 AutoCAD 弹出安全确认框，请点击【加载】；"
            + "如需永久免弹窗，请将桥目录加入 TRUSTEDPATHS（桥目录：" + bridgeDir + "；通道目录：" + channelDir + "）。";
    }

    /// <summary>
    /// 安全框检出时的诊断后缀（F4：指引文案加性拼接；检测未命中不改变原文案）。
    /// 监视线程在无进展阈值到达前按固定节拍探测一次（一次命中即锁存），打印指引时附加。
    /// </summary>
    internal const string SecurityDialogSeenSuffix = "（已检测到安全框）";

    /// <summary>监视线程内安全框探测节拍（与轮询口径一致；避免 20ms 切片级窗口枚举）。</summary>
    private static readonly TimeSpan DialogProbeInterval = TimeSpan.FromMilliseconds(500);

    // 指引文案的秒数（与 AcadRuntime.NetLoadNoProgressThreshold 默认值一致；文案冗余说明用）。
    internal const int NoProgressGuideSeconds = 20;

    /// <summary>清理通道目录（best-effort：桥可能仍持句柄，删除失败不掩盖主流程结论）。</summary>
    internal static void Cleanup(string? channelDir)
    {
        if (string.IsNullOrEmpty(channelDir)) return;
        try { if (Directory.Exists(channelDir)) Directory.Delete(channelDir, true); }
        catch { /* best-effort：临时目录残留由 OS 清理 */ }
    }

    // 看门狗回调异常（窗口枚举/目录读失败）→ 视为"无进展"，不中断主流程（超时仍 fail-closed）。
    private static bool SafeWatchdog(Func<TimeSpan, bool> watchdog, TimeSpan elapsed)
    {
        try { return watchdog(elapsed); }
        catch { return false; }
    }
}
