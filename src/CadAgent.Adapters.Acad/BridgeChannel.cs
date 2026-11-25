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
        "NETLOAD 被拒绝：可能点击了【不加载】/TRUSTEDPATHS 未含桥目录/SECURELOAD 阻止";

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
    /// 20s 无进展指引的调度缝：默认后台延时执行（真机 SendCommand 阻塞期间无法自本线程轮询，
    /// 故指引由后台一次性打印）；测试替换为立即执行以确定性钉住"只回调一次"。
    /// </summary>
    internal static Action<TimeSpan, Action> ScheduleGuidance = (delay, action) =>
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                action();
            }
            catch
            {
                // 一次性提示：后台失败不影响 NETLOAD 主路径。
            }
        });
    };

    private static readonly System.Text.Encoding Utf8NoBom =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 写请求文件 <c>req-&lt;id&gt;.txt</c>（id=目录内现有最大数字+1，1 起），返回 id。
    /// 目录不存在时先创建（跨实例复用同一通道目录时幂等）。
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
            if (int.TryParse(name.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && n > max)
                max = n;
        }
        string id = (max + 1).ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(channelDir, "req-" + id + ".txt"), requestText, Utf8NoBom);
        return id;
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
    /// 发送 NETLOAD 并等待"已返回/有进展"（看门狗）：
    /// ① 写 <see cref="NetLoadRequestMarker"/> 请求标记；
    /// ② 若给了指引预算/回调，按 <paramref name="noProgressThreshold"/> 后台调度一次指引
    ///    （返回后由完成标志抑制——快速返回不打印）；
    /// ③ <see cref="SendCommand"/> 同步发送（COM 阻塞即 NETLOAD 墙钟等待）；
    /// ④ 写 <see cref="NetLoadSentMarker"/>（返回=NETLOAD 已结束，加载/拒绝由探针判定）；
    /// ⑤ 轮询 <paramref name="watchdog"/>：true=有进展→返回；超过 <paramref name="netLoadTimeout"/>→
    ///    <see cref="CadRuntimeException"/>（规格逐字超时文案）。
    /// 说明：SendCommand 在 STA 内同步阻塞，阻塞期间无法自本线程轮询——故 3600s 上限覆盖
    /// "返回后仍无进展"的恒假看门狗场景（真机返回即写 sent 标记，循环首轮即放行）。
    /// </summary>
    internal static void EnsureNetLoad(
        string bridgeDll,
        string channelDir,
        Func<TimeSpan, bool> watchdog,
        TimeSpan netLoadTimeout,
        TimeSpan? noProgressThreshold = null,
        Action<string>? slowGuidance = null)
    {
        if (string.IsNullOrWhiteSpace(bridgeDll)) throw new CadRuntimeException("桥 DLL 路径为空");
        if (string.IsNullOrEmpty(channelDir)) throw new CadRuntimeException("桥通道目录为空");
        Directory.CreateDirectory(channelDir);
        File.WriteAllText(Path.Combine(channelDir, NetLoadRequestMarker), bridgeDll, Utf8NoBom);

        int completed = 0;
        if (slowGuidance != null && noProgressThreshold is TimeSpan threshold && threshold > TimeSpan.Zero)
        {
            string text = GuidanceText(bridgeDll, channelDir);
            ScheduleGuidance(threshold, () =>
            {
                if (Volatile.Read(ref completed) == 0) slowGuidance(text);
            });
        }

        try { SendCommand(NetLoadLisp(bridgeDll), "netload"); }
        finally { Volatile.Write(ref completed, 1); }

        File.WriteAllText(Path.Combine(channelDir, NetLoadSentMarker), "1", Utf8NoBom);

        var sw = Stopwatch.StartNew();
        while (!SafeWatchdog(watchdog, sw.Elapsed))
        {
            if (sw.Elapsed >= netLoadTimeout)
                throw new CadRuntimeException(
                    $"NETLOAD 超时（{(int)netLoadTimeout.TotalSeconds}s）：可能安全弹窗未处理");
            Thread.Sleep(PollInterval);
        }
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
        Action<string>? slowGuidance = null)
    {
        EnsureNetLoad(bridgeDll, channelDir, watchdog, netLoadTimeout, noProgressThreshold, slowGuidance);
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
