using System.Diagnostics;
using CadAgent.Core.Cad;
using Xunit.Abstractions;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 切片B Task 10：桥通道**真机**证据（条件测试，镜像切片A <see cref="AcadNativeLiveTests"/> 纪律）。
/// 三道守卫（任一不满足→SkippableFact 显式 Skip，原因如实，绝不伪造绿）：
/// ① 桥 DLL 已构建（<c>CADAGENT_BRIDGE_DLL</c> 覆盖，或仓库
///    <c>src/CadAgent.TArchBridge/bin/{Debug|Release}/net45/CadAgent.TArchBridge.dll</c>）；
/// ② 桥目录在 <c>TRUSTEDPATHS</c> 内（经 <c>AcadRuntime.GetTrustedPaths()</c> 实读；变量为空/
///    不可读/不含桥目录 → Skip 并输出 spec §5 一次性配置指引）——**不尝试 NETLOAD**：
///    AutoCAD 2014 对未签名程序集弹安全确认框，SendCommand 同步阻塞（spike 实测 45 分钟）；
/// ③ 标本副本可用（复用 <see cref="AcadNativeLiveFixture"/>：副本 + 源件/副本 SHA + CUIX 指纹 + acad 零残留金丝雀）。
/// 纪律：所有修改只作用**副本**；源件只 SHA256 比对、绝不打开写入；每测试自建 <see cref="AcadRuntime"/>
/// 并 Dispose（Close(false)+Quit+WaitForProcessExit）；绝不配置 TRUSTEDPATHS / 改任何 AutoCAD 设置。
/// 三条事实：
/// T1 READ 基准（证据数 ≥300，探针基准 343；21E00 extents；XData HYDRANT_RADIUS；TDbSymbArrow 永久 FAIL 披露）；
/// T2 递归（NestedTdbCount&gt;0 残留行；标本无此类行→Skip）；
/// T3 EXPLODE_REPLACE（NewCount&gt;0/BboxMatch、读回无 21E00、.live.dwg 落盘、源件/副本/CUIX 不变）。
/// </summary>
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadBridgeLiveTests : IClassFixture<AcadNativeLiveFixture>, IDisposable
{
    private const string WallHandle = "21E00";

    // 桥 extents 以 "0.###" 不变文化输出（127749.9958→127749.996）：1e-2 容差覆盖三位小数取整。
    private const double BboxTolerance = 1e-2;

    // 探针基准（PR#28 tarch-dotnet-probe）：TDb 实体 343 条。下限 300 吸收标本/版本可变，
    // 精确 343 只记日志（不硬断言——标本与天正版本可变是设计内噪声）。
    private const int MinEvidenceCount = 300;
    private const int ProbeEvidenceCount = 343;

    // 探针实证 extents（切片A基准逐字：120300/130100/3000）。
    private static readonly double[] WallBbox = { 127749.996, 120300, 0, 127849.996, 130100, 3000 };

    private readonly AcadNativeLiveFixture _fx;
    private readonly ITestOutputHelper _out;
    private readonly string? _origBridgeDll = AcadRuntime.BridgeDllOverride;
    private readonly TimeSpan _origNetLoadTimeout = AcadRuntime.NetLoadTimeout;

    public AcadBridgeLiveTests(AcadNativeLiveFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
        // 测试侧硬上限（生产默认 3600s 不动）：若信任检查通过但 SECURELOAD 仍弹框，
        // 本测试最多等 10 分钟即 fail-closed，不让 CI/本机挂死一小时。
        AcadRuntime.NetLoadTimeout = TimeSpan.FromMinutes(10);
    }

    public void Dispose()
    {
        // 静态缝还原（本测试类只做真机；thread-safe 由 xunit.runner.json parallelizeTestCollections=false 保证）。
        AcadRuntime.BridgeDllOverride = _origBridgeDll;
        AcadRuntime.NetLoadTimeout = _origNetLoadTimeout;
    }

    /// <summary>
    /// T1 READ 基准：ReadExplodeEvidence(副本) → 证据数 ≥300（精确 343 记日志）；
    /// 21E00（TDbWall）extents 与切片A基准逐字（1e-2）、FullyExplodable=true；
    /// XData 至少一行含 HYDRANT_RADIUS；TDbSymbArrow 至少一行 FullyExplodable=false
    /// （桥内 eInvalidInput 永久 FAIL 如实呈现——Core 无逐实体 Fail 列表，FAIL 只映射为 Fully=false）。
    /// </summary>
    [SkippableFact]
    public void T1_Read_BaselineCountBboxXDataSymbArrowFail()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        var sw = Stopwatch.StartNew();
        var evidences = rt.ReadExplodeEvidence(_fx.CopyPath);
        sw.Stop();

        int tdbRows = evidences.Count(e => e.RxClass.StartsWith("TDb", StringComparison.Ordinal));
        int fully = evidences.Count(e => e.FullyExplodable);
        int notFully = evidences.Count(e => !e.FullyExplodable);
        _out.WriteLine($"[T1] evidence={evidences.Count} (probe={ProbeEvidenceCount}) tdb={tdbRows} " +
                       $"fully={fully} notFully={notFully} elapsed={sw.ElapsedMilliseconds}ms");
        _out.WriteLine("[T1] rxClassTop=" + string.Join("/", evidences
            .GroupBy(e => e.RxClass, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => $"{g.Key}:{g.Count()}")));

        Assert.True(evidences.Count >= MinEvidenceCount,
            $"深度读证据数 {evidences.Count} < 下限 {MinEvidenceCount}（探针基准 {ProbeEvidenceCount}；空读/桥截断须显式失败）");
        if (evidences.Count != ProbeEvidenceCount)
            _out.WriteLine($"[T1] 注意：证据数 {evidences.Count} ≠ 探针基准 {ProbeEvidenceCount}（标本/天正版本可变，只记录不判失败）");

        // 21E00（TDbWall）：extents 与切片A基准逐字、完全炸开。
        var wall = evidences.FirstOrDefault(e => HandleEquals(e.Handle, WallHandle));
        Assert.True(wall != null, $"深度读未见句柄 {WallHandle}（TDbWall 应存在）");
        Assert.Equal("TDbWall", wall!.RxClass);
        Assert.True(wall.FullyExplodable, $"{WallHandle} FullyExplodable=false（直墙递归炸开应全成功）");
        Assert.True(wall.Bbox is { Length: 6 },
            $"{WallHandle} extents 缺失（bbox=null 或长度非 6）——切片A基准不可复核");
        for (int i = 0; i < 6; i++)
            Assert.True(Math.Abs(wall.Bbox![i] - WallBbox[i]) <= BboxTolerance,
                $"{WallHandle} bbox 分量{i} 不符：实际={wall.Bbox![i]:R} 期望={WallBbox[i]:R}（容差 {BboxTolerance}）");
        _out.WriteLine($"[T1] {WallHandle} rx={wall.RxClass} wrapper={wall.WrapperType} prims={wall.PrimitiveCount} " +
                       $"nested={wall.NestedTdbCount} bbox={Fmt(wall.Bbox!)}");

        // XData：至少一行含天正键名 HYDRANT_RADIUS（探针基线 =28，只披露实读值）。
        var hydrant = evidences.Where(e => e.XData.ContainsKey("HYDRANT_RADIUS")).ToList();
        Assert.True(hydrant.Count > 0, "全量证据中无任何 HYDRANT_RADIUS XData 键（天正 XData 通道失效或标本不含设备）");
        foreach (var row in hydrant.Take(5))
            _out.WriteLine($"[T1] XData HYDRANT_RADIUS handle={row.Handle} rx={row.RxClass} " +
                           $"value={row.XData["HYDRANT_RADIUS"]}（探针基线 28）");

        // TDbSymbArrow：几何双通道 eInvalidInput → 永久 FAIL，Core 只映射为 FullyExplodable=false。
        var arrows = evidences.Where(e => e.RxClass == "TDbSymbArrow").ToList();
        _out.WriteLine($"[T1] TDbSymbArrow rows={arrows.Count} notFully={arrows.Count(e => !e.FullyExplodable)}");
        Skip.If(arrows.Count == 0, "标本无 TDbSymbArrow 样本");
        var arrowFailed = arrows.Where(e => !e.FullyExplodable).ToList();
        Assert.True(arrowFailed.Count > 0,
            $"TDbSymbArrow {arrows.Count} 行全部 FullyExplodable=true（探针实证 eInvalidInput 失败应如实披露为 false）");
        foreach (var row in arrowFailed.Take(5))
            _out.WriteLine($"[T1] TDbSymbArrow FAIL handle={row.Handle} prims={row.PrimitiveCount} " +
                           $"nested={row.NestedTdbCount} bbox={(row.Bbox == null ? "null" : Fmt(row.Bbox))}");

        rt.Dispose();
        _fx.AssertCanaries("bridge-read");
    }

    /// <summary>
    /// T2 递归：取证中任一 NestedTdbCount&gt;0（炸开后残留 TDb 叶）→ 断言该行 >0 且与
    /// FullyExplodable=false 自洽（Core Primitives 不带 depth 字段，无更深断言；残留 TDb 行
    /// 只由桥内"递归超深/非实体产物"失败产生）。标本无此类行 → Skip("标本无嵌套 TDb 样本")，
    /// 诊断日志披露 TDbSpace 等嵌套类型行的实际形态（不伪造残留）。
    /// </summary>
    [SkippableFact]
    public void T2_Read_Recursion_NestedTdbResidual()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        var evidences = rt.ReadExplodeEvidence(_fx.CopyPath);
        rt.Dispose();

        var nested = evidences.Where(e => e.NestedTdbCount > 0).ToList();
        var spaceRows = evidences.Where(e => e.RxClass == "TDbSpace").ToList();
        _out.WriteLine($"[T2] evidence={evidences.Count} nestedResidualRows={nested.Count}");
        _out.WriteLine("[T2] TDbSpace（递归穿过嵌套 TDb 的类型）rows=" + spaceRows.Count
                       + (spaceRows.Count == 0 ? "" :
                          " prims=" + string.Join("/", spaceRows.Take(5)
                              .Select(e => $"{e.Handle}:prims{e.PrimitiveCount},nested{e.NestedTdbCount},fully={e.FullyExplodable}"))));

        _fx.AssertCanaries("bridge-recursion");

        Skip.If(nested.Count == 0, "标本无嵌套 TDb 样本");
        foreach (var e in nested)
        {
            Assert.True(e.NestedTdbCount > 0, $"{e.Handle} NestedTdbCount 非正（筛选条件自洽性破坏）");
            Assert.False(e.FullyExplodable,
                $"{e.Handle} 残留 TDb {e.NestedTdbCount} 个却标记 FullyExplodable=true（证据自相矛盾）");
            _out.WriteLine($"[T2] residual handle={e.Handle} rx={e.RxClass} nested={e.NestedTdbCount} " +
                           $"prims={e.PrimitiveCount} fully={e.FullyExplodable}");
        }
    }

    /// <summary>
    /// T3 EXPLODE_REPLACE（**副本**上，生产 native-live 会话序）：先 ReadNativeParams(副本) 完成
    /// /p 天正会话开路（单独 Open 走的是 EnsureApp() 默认 profile——天正未载，TDb 实体不可炸），
    /// 再 Open(副本) + ExplodeReplaceByHandle("21E00", 副本+".live.dwg")。
    /// 断言：NewCount&gt;0、BboxMatch=true、读回无 21E00、.live.dwg 落盘；
    /// 金丝雀：源件 SHA / 副本 SHA（Open RW 但无 Save ⇒ 字节不变）/ CUIX 指纹 / acad 零残留。
    /// </summary>
    [SkippableFact]
    public void T3_ExplodeReplace_OnCopy_NewCountBboxMatch_LiveDwg_SourceCuixUnchanged()
    {
        RequireLiveEnvironment();

        string liveDwg = _fx.CopyPath + ".live.dwg";
        if (File.Exists(liveDwg)) File.Delete(liveDwg);

        using var rt = new AcadRuntime();
        // 会话开路：/p 天正 + 只读开副本读参后即关（后续 Open 复用同一实例；也给出替换前基线）。
        var before = rt.ReadNativeParams(_fx.CopyPath);
        var wallBefore = before.FirstOrDefault(r => HandleEquals(r.Handle, WallHandle));
        Assert.True(wallBefore != null, $"开路读未见句柄 {WallHandle}（替换基线缺失）");
        _out.WriteLine($"[T3] open-warm records={before.Count} {WallHandle}.{wallBefore!.ObjectName} layer={wallBefore.Layer}");

        rt.Open(_fx.CopyPath);
        var rep = rt.ExplodeReplaceByHandle(WallHandle, liveDwg);
        _out.WriteLine($"[T3] REPLACED handle={rep.Handle} newCount={rep.NewCount} bboxMatch={rep.BboxMatch} " +
                       $"tolerance={rep.Tolerance:R} saved={rep.SavedPath}");

        Assert.True(rep.NewCount > 0, $"替换叶数 {rep.NewCount} 非正（炸开空产物）");
        Assert.True(rep.BboxMatch,
            $"替换前后 bbox 不一致（BboxMatch=false，容差 {rep.Tolerance:R}）——包含性判据失败");
        Assert.True(string.Equals(rep.Handle, WallHandle, StringComparison.OrdinalIgnoreCase),
            $"REPLACED 句柄 {rep.Handle} ≠ 请求句柄 {WallHandle}");

        // 读回（null 形态=当前已开文档）：原句柄必须消失（已替换为叶产物）。
        var after = rt.ReadNativeParams();
        Assert.True(after.All(r => !HandleEquals(r.Handle, WallHandle)),
            $"替换后模型空间仍见句柄 {WallHandle}（原实体未删）");
        _out.WriteLine($"[T3] readback records={after.Count} containsWall={after.Any(r => HandleEquals(r.Handle, WallHandle))}");

        rt.Close(false);   // 丢弃内存修改：不 SaveAs 回副本 ⇒ 副本字节不变
        rt.Dispose();

        Assert.True(File.Exists(liveDwg), $".live.dwg 未落盘: {liveDwg}");
        var live = new FileInfo(liveDwg);
        Assert.True(live.Length > 0, $".live.dwg 为空文件: {liveDwg}");
        _out.WriteLine($"[T3] live.dwg size={live.Length} path={liveDwg}");

        _fx.AssertCanaries("bridge-explode-replace");
    }

    // ---- guards ----

    private void RequireLiveEnvironment()
    {
        Skip.If(!_fx.SpecimenReady, _fx.SpecimenSkipReason ?? "真实电气标本不可用，跳过真机测试");
        Assert.Equal(0, _fx.AcadProcessCountBefore);

        // 守卫顺序：桥/信任判定**先于** AcadProbe——AcadProbe 会先拉起默认 profile 实例，
        // 该实例在退出时可能被启动期模态框滞留（本机实证"不支持硬件加速"框），滞留进程会让
        // 后续 /p 启动的 EnsureNoForeignInstance 拒绝、TRUSTEDPATHS 永远读不到；先读才拿得到真值。
        var guard = BridgeLiveGuard.State;
        _out.WriteLine($"[guard] bridgeDll={guard.DllPath ?? "<缺失>"}");
        _out.WriteLine($"[guard] TRUSTEDPATHS={(guard.TrustedPaths == null ? "<未读取>" : FmtTrusted(guard.TrustedPaths))}");
        Skip.If(!guard.Ok, guard.SkipReason);

        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        AcadRuntime.BridgeDllOverride = guard.DllPath;   // 测试必须指向构建产物（net45 桥）
    }

    // ---- helpers ----

    private static bool HandleEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Fmt(double[] v) =>
        "(" + string.Join(",", v.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + ")";

    private static string FmtTrusted(string v) =>
        v.Length == 0 ? "<空字符串>" : "\"" + v + "\"";
}

/// <summary>
/// 桥真机守卫（Lazy 一次求值，类内三测试共享）：
/// 桥 DLL 定位 → 实读 TRUSTEDPATHS（<c>AcadRuntime.GetTrustedPaths()</c>，/p 天正 profile，F2 口径）
/// → 桥目录是否落在信任位置（TRUSTEDPATHS 含子目录，故祖先目录亦算信任）。
/// 任何失败（含 COM 异常）→ Skip 原因，绝不上抛失败；绝不写注册表 / 改任何 AutoCAD 设置。
/// </summary>
internal static class BridgeLiveGuard
{
    internal const string BridgeDllName = "CadAgent.TArchBridge.dll";

    /// <summary>spec §5 一次性指引（20s 无进展/信任未配置时逐字输出）。</summary>
    internal const string OneTimeGuidance =
        "若 AutoCAD 弹出安全确认框，请点击【加载】；如需永久免弹窗，将桥目录加入 TRUSTEDPATHS";

    private static readonly Lazy<BridgeGuard> _state =
        new(Evaluate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static BridgeGuard State => _state.Value;

    private static BridgeGuard Evaluate()
    {
        string? dll = ResolveBridgeDll(out string? missing);
        if (dll == null) return new BridgeGuard(false, missing!, null, null);

        string? trusted;
        try
        {
            using var rt = new AcadRuntime();
            trusted = rt.GetTrustedPaths();   // /p TArch profile；Dispose=Close(false)+Quit+等进程退出
        }
        catch (Exception ex)
        {
            // 本机真机实证（2026-11-25，Task 10）：AutoCAD 2014 每次启动弹「AutoCAD - 不支持硬件加速」
            // WPF 模态框；人工未关闭前 COM 调用持续 RPC_E_CALL_REJECTED → Gate1 超时、Quit 滞留
            // （acad.exe 残留须人工处理）。属环境前置，不改任何 AutoCAD 设置，只如实 Skip。
            return new BridgeGuard(false,
                $"TRUSTEDPATHS 不可读（{ex.GetType().Name}: {ex.Message}）——不尝试 NETLOAD（安全框会阻塞）。"
                + "诊断提示：若为天正实例未就绪/Gate1 超时，先确认启动期模态框（如「不支持硬件加速」）已人工关闭。"
                + Guidance(dll), dll, null);
        }

        var leftover = AcadNativeLiveFixture.AcadProcessTokens();
        string leftoverNote = leftover.Count == 0
            ? ""
            : $"（注意：TRUSTEDPATHS 预检后残留 acad 进程 {string.Join(",", leftover)}——退出期模态框滞留，须人工处理后方可重跑）";

        string dir = Path.GetDirectoryName(dll) ?? dll;
        if (string.IsNullOrWhiteSpace(trusted))
            return new BridgeGuard(false,
                $"TRUSTEDPATHS 为空/未配置（实读值: <空>；桥目录 {dir}）——不尝试 NETLOAD（安全框会阻塞）。"
                + leftoverNote + Guidance(dll), dll, trusted);
        if (!IsTrusted(trusted, dir))
            return new BridgeGuard(false,
                $"TRUSTEDPATHS 不含桥目录（实读值: \"{trusted}\"；桥目录 {dir}）——不尝试 NETLOAD（安全框会阻塞）。"
                + leftoverNote + Guidance(dll), dll, trusted);
        if (leftover.Count > 0)
            return new BridgeGuard(false,
                $"TRUSTEDPATHS 预检后残留 acad 进程 {string.Join(",", leftover)}（不并发激活）" + leftoverNote, dll, trusted);

        return new BridgeGuard(true, null, dll, trusted);
    }

    /// <summary>桥 DLL 定位：CADAGENT_BRIDGE_DLL 覆盖优先，否则仓库 src/CadAgent.TArchBridge/bin/{Debug|Release}/net45。</summary>
    internal static string? ResolveBridgeDll(out string? reason)
    {
        string? env = Environment.GetEnvironmentVariable("CADAGENT_BRIDGE_DLL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (File.Exists(env))
            {
                reason = null;
                return Path.GetFullPath(env);
            }
            reason = $"桥未构建（CADAGENT_BRIDGE_DLL 指向的文件不存在: {env}）";
            return null;
        }

        string? root = RepoRoot();
        if (root == null)
        {
            reason = $"桥未构建（未定位仓库根；找不到含 src/ 的目录，起点 {AppContext.BaseDirectory}）";
            return null;
        }

        var candidates = new[] { "Debug", "Release" }
            .Select(c => Path.Combine(root, "src", "CadAgent.TArchBridge", "bin", c, "net45", BridgeDllName))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)   // 两个配置都在→取较新产物
            .ToList();
        if (candidates.Count == 0)
        {
            reason = $"桥未构建（src/CadAgent.TArchBridge/bin/{{Debug|Release}}/net45/{BridgeDllName} 均不存在）";
            return null;
        }
        reason = null;
        return candidates[0];
    }

    /// <summary>TRUSTEDPATHS 信任判定：分号分隔、可带引号/环境变量；条目本身或其子目录均受信。
    /// 比较口径 OrdinalIgnoreCase + 归一化全路径（AutoCAD 路径语义与 Windows 一致）。</summary>
    internal static bool IsTrusted(string trustedPaths, string dir)
    {
        string target = NormalizeDir(dir);
        foreach (string raw in trustedPaths.Split(';'))
        {
            string entry = raw.Trim().Trim('"');
            if (entry.Length == 0) continue;
            try { entry = Environment.ExpandEnvironmentVariables(entry); } catch { /* 保留原串 */ }
            string trusted = NormalizeDir(entry);
            if (trusted.Length == 0) continue;
            if (string.Equals(target, trusted, StringComparison.OrdinalIgnoreCase)) return true;
            if (target.StartsWith(trusted + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string NormalizeDir(string p)
    {
        try { p = Path.GetFullPath(p); } catch { /* 非法路径→按原串比较 */ }
        return p.TrimEnd('\\', '/');
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? srcOnly = null;
        while (dir is not null)
        {
            bool hasSrc = Directory.Exists(Path.Combine(dir.FullName, "src"));
            if (hasSrc && File.Exists(Path.Combine(dir.FullName, "CadAgent.sln"))) return dir.FullName;
            if (hasSrc && srcOnly == null) srcOnly = dir;
            dir = dir.Parent;
        }
        return srcOnly?.FullName!;
    }

    private static string Guidance(string dll)
    {
        string dir = Path.GetDirectoryName(dll) ?? dll;
        return OneTimeGuidance + "。" +
               "一次性配置（**由用户执行**，回滚见末行；生效需重启 AutoCAD，SECURELOAD 保持默认 1）：" +
               "① 读取现值: reg query \"HKCU\\Software\\Autodesk\\AutoCAD\\R19.1\\ACAD-<id>\\Profiles\\<profile>\\Variables\" /v TRUSTEDPATHS；" +
               $"② 追加桥目录（保留原值，引号分号分隔）: ... /v TRUSTEDPATHS /t REG_SZ /d \"\\\"<原值>;{dir}\\\"\" /f；" +
               "③ 回滚: reg add ... /d \"<原值>\" /f（或删除该值恢复默认）。";
    }
}

/// <summary>守卫求值结果：Ok=false 时 SkipReason 必含实读值与配置指引；DllPath 为构建产物全路径。</summary>
internal sealed class BridgeGuard
{
    public BridgeGuard(bool ok, string? skipReason, string? dllPath, string? trustedPaths)
    {
        Ok = ok;
        SkipReason = skipReason;
        DllPath = dllPath;
        TrustedPaths = trustedPaths;
    }

    public bool Ok { get; }
    public string? SkipReason { get; }
    public string? DllPath { get; }
    public string? TrustedPaths { get; }
}
