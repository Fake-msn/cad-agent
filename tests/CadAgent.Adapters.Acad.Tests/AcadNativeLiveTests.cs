using System.Diagnostics;
using System.Security.Cryptography;
using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit.Abstractions;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 切片A Task 10：条件真机测试（无 AutoCAD/缺标本→SkippableFact 显式 Skip，不静默绿）。
/// 真机协议：docs/verification/autocad-adapter-protocol.md + reliability spike
/// （outputs/api-probe/tarch-reliability-spike/report.md：/p 附着、忙窗 A/B、Close/Quit 可被拒）。
/// 纪律：标本先复制到临时目录，所有修改只作用**副本**；源件只 SHA256 比对、绝不打开写入；
/// 每测试自建 AcadRuntime（"创建后首读"即忙窗 A 注入点，生产 RetryBusy 吸收，无额外 sleep）；
/// Dispose 内 Close(false)+Quit 并 WaitForProcessExit；金丝雀=CUIX/源件/副本 SHA + acad 进程
/// 零残留，逐测试断言。期望基准（spike 实证）：TDbWall 21E00
/// LeftWidth=100/Height=3000/Volume=2.895，bbox≈(127749.9958,120299.9999,0)..(127849.9959,130099.9999,3000)。
/// </summary>
[Trait("Category", "RequiresAutoCAD")]
[Collection("AcadLive")]
public class AcadNativeLiveTests : IClassFixture<AcadNativeLiveFixture>
{
    private const string WallHandle = "21E00";
    // 读回 bbox 位移容差：镜像生产 CommandRouter.NativeLive.CheckReadback 的 BboxTolerance=1e-6。
    private const double BboxTolerance = 1e-6;

    private readonly AcadNativeLiveFixture _fx;
    private readonly ITestOutputHelper _out;

    public AcadNativeLiveTests(AcadNativeLiveFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>
    /// ① 读参基准（spike 逐字闭合）：copyPath 形态自启 /p 天正 + 自开 + 读完即关，
    /// TDbWall 21E00 的 LeftWidth/Height/Volume 逐字相等，Class=Wall、ReadOk=true、Layer 非空。
    /// census 计数只记日志（不硬断言 census 数字——标本/版本可变）。
    /// </summary>
    [SkippableFact]
    public void ReadNativeParams_LiveSpecimen_TDbWallParamsVerbatim()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        var sw = Stopwatch.StartNew();
        var records = rt.ReadNativeParams(_fx.CopyPath);
        sw.Stop();

        AssertWallBaseline(Find(records, WallHandle), "首读");

        // 全读无失败（nativeReadFailed 近零的诚实边界；ReadOk=false 不得被当成"无此实体"吞掉）。
        Assert.False(records.Any(r => !r.ReadOk), "全读出现 ReadOk=false（忙窗耗尽/空读）");
        var tdb = records.Count(r => r.ObjectName.StartsWith("TDb", StringComparison.Ordinal));
        _out.WriteLine($"records={records.Count} tdb={tdb} elapsed={sw.ElapsedMilliseconds}ms " +
                       $"layers={DistinctLayers(records)}");
        Assert.True(tdb > 0, "TDb ObjectName 计数为 0（天正未加载或类名通道失效）");

        rt.Dispose();
        _fx.AssertCanaries("read-params");
    }

    /// <summary>
    /// ② 忙窗 A 注入：运行时**创建后第一个** COM 数据调用即 ReadNativeParams(copyPath)——
    /// 不额外 sleep，启动窗口拒绝由生产 RetryBusy/Gate1 吸收；结果须全量可用（21E00 在列）。
    /// </summary>
    [SkippableFact]
    public void BusyWindow_FirstReadRightAfterCreation_AbsorbedByProductionRetries()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        var sw = Stopwatch.StartNew();
        var records = rt.ReadNativeParams(_fx.CopyPath); // 创建后首读（忙窗注入点）
        sw.Stop();

        AssertWallBaseline(Find(records, WallHandle), "创建后首读");
        Assert.False(records.Any(r => !r.ReadOk), "创建后首读出现 ReadOk=false（忙窗未被吸收）");
        Assert.True(records.Count > 0, "创建后首读模型空间读空");

        _out.WriteLine($"firstReadAfterCreation elapsed={sw.ElapsedMilliseconds}ms records={records.Count}");
        rt.Dispose();
        _fx.AssertCanaries("busy-window");
    }

    /// <summary>
    /// ③④ 句柄寻址执行器真机（**副本**上，单会话链式执行，顺序与生产 native-live 一致）：
    /// a. ChangeLayerByHandle(21E00, 记录中既有层) → 重读 Layer 相等（OrdinalIgnoreCase）；
    /// b. MoveByHandle(21E00, 10, 0) → 重读 bbox min/max x 平移 +10（bbox 不可得→诚实降级断言，
    ///    镜像生产 R6 reread-bbox-null 口径，不伪造）；
    /// ④ DeleteByHandle(DEADBEEF)（删 21E00 **之前**）→ 响亮拒绝且消息含"句柄不存在于模型空间"；
    /// c. DeleteByHandle(21E00) 最后 → 重读缺该句柄。
    /// 全程不 SaveAs：关闭丢弃内存修改，副本字节不变（金丝雀断言）。
    /// </summary>
    [SkippableFact]
    public void HandleOps_OnCopy_ChangeLayerMoveStaleDelete_ReadBack()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        // 前读：自启 /p 天正 + 自开 + 读完即关（同时完成 TArch 会话开路，后续 Open 复用同实例）。
        var before = rt.ReadNativeParams(_fx.CopyPath);
        var wallBefore = Find(before, WallHandle);
        AssertWallBaseline(wallBefore, "句柄操作前读");
        string currentLayer = wallBefore!.Layer!;
        string targetLayer = PickTargetLayer(before, currentLayer);
        bool targetIsExisting = before.Any(r => string.Equals(r.Layer, targetLayer, StringComparison.OrdinalIgnoreCase));
        _out.WriteLine($"currentLayer={currentLayer} targetLayer={targetLayer} existing={targetIsExisting}");

        rt.Open(_fx.CopyPath);
        try
        {
            // a. 改层 → 重读层名
            rt.ChangeLayerByHandle(WallHandle, targetLayer);
            var afterLayer = rt.ReadNativeParams();
            var w1 = Find(afterLayer, WallHandle);
            Assert.True(w1 != null, $"change-layer 后重读缺失句柄 {WallHandle}");
            Assert.Equal(SemanticClass.Wall, w1!.Class);
            Assert.True(w1.ReadOk, "change-layer 后重读 ReadOk=false");
            Assert.True(w1.Layer != null && string.Equals(w1.Layer, targetLayer, StringComparison.OrdinalIgnoreCase),
                $"change-layer 图层不符（期望 {targetLayer}，实际 {w1.Layer ?? "null"}）");

            // b. 平移 → 重读 bbox 平移（参考 before-bbox；before 不可得退用改层后 bbox）
            rt.MoveByHandle(WallHandle, 10, 0);
            var afterMove = rt.ReadNativeParams();
            var w2 = Find(afterMove, WallHandle);
            Assert.True(w2 != null, $"move 后重读缺失句柄 {WallHandle}");
            Assert.True(w2!.Layer != null && string.Equals(w2.Layer, targetLayer, StringComparison.OrdinalIgnoreCase),
                $"move 后图层被破坏（期望 {targetLayer}，实际 {w2.Layer ?? "null"}）");
            double[]? refBbox = wallBefore.Bbox ?? w1.Bbox;
            if (refBbox is { Length: 6 } refBox && w2.Bbox is { Length: 6 } movedBox)
            {
                double[] deltas = { 10, 0, 0, 10, 0, 0 };
                for (int i = 0; i < 6; i++)
                {
                    Assert.True(Math.Abs(movedBox[i] - refBox[i] - deltas[i]) <= BboxTolerance,
                        $"move bbox 分量{i} 不符：before={refBox[i]:R} after={movedBox[i]:R} " +
                        $"期望={refBox[i] + deltas[i]:R}");
                }
                _out.WriteLine($"bbox before={Fmt(refBox)} after={Fmt(movedBox)}");
            }
            else
            {
                // 诚实降级（镜像生产 R6 reread-bbox-null）：bbox 面不可得时只断言原生参数不变，
                // 证据里如实标注该降级，绝不伪造 bbox 通过。
                Assert.True(w1.Params.Count == w2.Params.Count,
                    $"move 后原生参数项数变化（bbox 不可得的退化判据失败）: {w1.Params.Count} -> {w2.Params.Count}");
                foreach (var kv in w1.Params)
                    Assert.True(w2.Params.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal),
                        $"move 后原生参数变化（bbox 不可得的退化判据失败）: {kv.Key}");
                _out.WriteLine("bbox 不可得（GetBoundingBox 面缺失）→ 退化为 params 不变判据（reread-bbox-null，诚实降级）");
            }

            // ④ stale 未命中（删 21E00 之前）：句柄不存在必须响亮拒绝，不得静默成功。
            var stale = Assert.Throws<CadRuntimeException>(() => rt.DeleteByHandle("DEADBEEF"));
            Assert.Contains("句柄不存在于模型空间", stale.Message);
            Assert.Contains("DEADBEEF", stale.Message);
            _out.WriteLine($"stale-refused: {stale.Message}");

            // c. 删 21E00（最后一步）→ 重读缺该句柄
            int beforeDelete = afterMove.Count;
            rt.DeleteByHandle(WallHandle);
            var afterDelete = rt.ReadNativeParams();
            Assert.True(Find(afterDelete, WallHandle) == null,
                $"delete 后重读仍存在句柄 {WallHandle}（未删除）");
            _out.WriteLine($"delete: records {beforeDelete} -> {afterDelete.Count}");
        }
        finally
        {
            rt.Close(false); // 丢弃内存修改：无 SaveAs → 副本字节不变
        }

        rt.Dispose();
        _fx.AssertCanaries("handle-ops");
    }

    // ---- helpers ----

    private void RequireLiveEnvironment()
    {
        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        Skip.If(!_fx.SpecimenReady, _fx.SpecimenSkipReason ?? "真实电气标本不可用，跳过真机测试");
        // 前置进程纪律（AcadProbe 第四态已在探测时守过；此处留起始计数证据）。
        _out.WriteLine($"acadProcessesBefore={_fx.AcadProcessCountBefore} cuixCount={_fx.CuixCount}");
        Assert.Equal(0, _fx.AcadProcessCountBefore);
    }

    private static NativeEntityParam? Find(IReadOnlyList<NativeEntityParam> records, string handle) =>
        records.FirstOrDefault(r => string.Equals(r.Handle, handle, StringComparison.Ordinal));

    private static string DistinctLayers(IReadOnlyList<NativeEntityParam> records) =>
        string.Join("/", records.Select(r => r.Layer).Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8));

    private static string Fmt(double[] v) =>
        "(" + string.Join(",", v.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + ")";

    /// <summary>spike 基准逐字闭合（①/② 共用）：TDbWall 21E00 三个参数 + 分类 + ReadOk + Layer。</summary>
    private static void AssertWallBaseline(NativeEntityParam? wall, string stage)
    {
        Assert.True(wall != null, $"{stage}: 重读未见句柄 {WallHandle}（TDbWall 应存在）");
        Assert.Equal("TDbWall", wall!.ObjectName);
        Assert.Equal(SemanticClass.Wall, wall.Class);
        Assert.True(wall.Params.TryGetValue("LeftWidth", out var lw) && lw == "100",
            $"{stage}: LeftWidth 期望 100，实际 {wall.Params.GetValueOrDefault("LeftWidth") ?? "缺失"}");
        Assert.True(wall.Params.TryGetValue("Height", out var h) && h == "3000",
            $"{stage}: Height 期望 3000，实际 {wall.Params.GetValueOrDefault("Height") ?? "缺失"}");
        Assert.True(wall.Params.TryGetValue("Volume", out var vol) && vol == "2.895",
            $"{stage}: Volume 期望 2.895，实际 {wall.Params.GetValueOrDefault("Volume") ?? "缺失"}");
        Assert.True(wall.ReadOk, $"{stage}: 21E00 ReadOk=false");
        Assert.False(string.IsNullOrWhiteSpace(wall.Layer), $"{stage}: 21E00 Layer 为空（change-layer 读回判据不可用）");
    }

    /// <summary>改层目标：优先记录中**既有**且不同于当前层的层名（brief 口径）；
    /// 记录里只有单层时退用新层名（EnsureLayer 建层通道一并行使），证据日志如实标注。</summary>
    private string PickTargetLayer(IReadOnlyList<NativeEntityParam> records, string currentLayer)
    {
        var alt = records.Select(r => r.Layer)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .FirstOrDefault(l => !string.Equals(l, currentLayer, StringComparison.OrdinalIgnoreCase));
        if (alt != null) return alt;
        string created = $"CADAGT_T10_{Guid.NewGuid():N}";
        _out.WriteLine($"记录中无第二既有层可用→使用新层 {created}（行使 EnsureLayer 建层通道）");
        return created;
    }
}

/// <summary>
/// Task 10 真机夹具：真实电气标本副本 + 金丝雀基线（CUIX 指纹 / 源件与副本 SHA256 / 起始 acad 进程数）。
/// 构造不触 COM（无 AutoCAD 时整套 Skip 也不得拉起进程）；副本目录用后即删（含 AutoCAD 锁文件重试）。
/// </summary>
public sealed class AcadNativeLiveFixture : IDisposable
{
    private const string SpecimenRelative = "outputs/tarch-elec/16#1116.dwg";

    public AcadNativeLiveFixture()
    {
        CopyDir = Path.Combine(Path.GetTempPath(), $"acadnative-{Guid.NewGuid():N}");
        Directory.CreateDirectory(CopyDir);
        SourcePath = Path.Combine(RepoRoot(), SpecimenRelative);
        if (!File.Exists(SourcePath))
        {
            SpecimenSkipReason = $"真实电气标本不在本机（local-only 不入库），跳过真机测试: {SpecimenRelative}";
            CopyPath = string.Empty;
            SourceSha256 = string.Empty;
            CopySha256 = string.Empty;
            CuixBeforeFingerprint = string.Empty;
            return;
        }
        SourceSha256 = Sha256File(SourcePath);
        CopyPath = Path.Combine(CopyDir, Path.GetFileName(SourcePath));
        File.Copy(SourcePath, CopyPath, overwrite: true);
        CopySha256 = Sha256File(CopyPath);
        var cuix = CuixHashes();
        CuixCount = cuix.Count;
        CuixBeforeFingerprint = CuixFingerprint(cuix);
        AcadProcessCountBefore = AcadProcessTokens().Count;
    }

    public string CopyDir { get; }
    public string SourcePath { get; }
    public string CopyPath { get; }
    public string? SpecimenSkipReason { get; }
    public bool SpecimenReady => SpecimenSkipReason == null;
    public string SourceSha256 { get; }
    public string CopySha256 { get; }
    public string CuixBeforeFingerprint { get; }
    public int CuixCount { get; }
    public int AcadProcessCountBefore { get; }

    /// <summary>金丝雀（每真机测试收尾断言）：源件/副本字节不变 + CUIX 指纹不变 + acad 零残留。
    /// 调用前提：本测试自建 AcadRuntime 已 Dispose（内部 Close(false)+Quit+WaitForProcessExit）。</summary>
    public void AssertCanaries(string stage)
    {
        Assert.True(SpecimenReady, $"[{stage}] 标本不可用却走到金丝雀断言");
        Assert.Equal(SourceSha256, Sha256File(SourcePath));
        Assert.Equal(CopySha256, Sha256File(CopyPath));
        var cuix = CuixHashes();
        Assert.True(cuix.Count == CuixCount, $"[{stage}] CUIX 数量变化: {CuixCount} -> {cuix.Count}");
        Assert.Equal(CuixBeforeFingerprint, CuixFingerprint(cuix));
        var acad = AcadProcessTokens();
        Assert.True(acad.Count == 0,
            $"[{stage}] 残留 acad.exe 进程: {string.Join(",", acad)}（自家实例 Dispose 后须零残留）");
    }

    /// <summary>CUIX SHA256 指纹（路径排序 + "path=sha" 拼接）：前后逐字相等即无损坏。
    /// 未安装/无 profile CUIX → 空串（两侧相等仍成立；数量单独披露，不假装已验）。</summary>
    public static string CuixFingerprint(IReadOnlyDictionary<string, string> map) =>
        string.Join("|", map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

    public static Dictionary<string, string> CuixHashes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in CuixLocator.FindCuix(VendorProfiles.Acad)) map[f] = Sha256File(f);
        return map;
    }

    /// <summary>acad 进程（tasklist 口径）："acad#pid" 列表；进程已退出/不可读跳过不抛。</summary>
    public static List<string> AcadProcessTokens()
    {
        var list = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Equals("acad", StringComparison.OrdinalIgnoreCase)) list.Add($"acad#{p.Id}");
            }
            catch
            {
                // 进程已退出或访问受限：与 tasklist 口径一致，不计入。
            }
            finally
            {
                p.Dispose();
            }
        }
        return list;
    }

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("未找到仓库根（CadAgent.sln）");
        return dir.FullName;
    }

    public void Dispose()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(CopyDir)) Directory.Delete(CopyDir, true);
                return;
            }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }
}
