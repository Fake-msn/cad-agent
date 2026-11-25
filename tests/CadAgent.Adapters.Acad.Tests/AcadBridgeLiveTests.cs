using System.Diagnostics;
using CadAgent.Core.Cad;
using Xunit.Abstractions;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 切片B Task 10：桥通道**真机**证据（条件测试，镜像切片A <see cref="AcadNativeLiveTests"/> 纪律）。
/// 四道守卫（任一不满足→SkippableFact 显式 Skip，原因如实，绝不伪造绿）：
/// ① 桥 DLL 已构建（<c>CADAGENT_BRIDGE_DLL</c> 覆盖，或仓库
///    <c>src/CadAgent.TArchBridge/bin/{Debug|Release}/net45/CadAgent.TArchBridge.dll</c>）；
/// ② 本机 AutoCAD ProgID 注册 + 桥目录在 <c>TRUSTEDPATHS</c> 内（ProgID 检查**先于任何 COM**；
///    TRUSTEDPATHS 经 <c>AcadRuntime.ReadTrustedPathsFromRegistry</c> 读**注册表**（R17：不建实例、
///    不发 COM——旧 COM 实读会启动实例并在初始化期留孤儿 acad.exe）；缺失/为空/不可读/不含桥目录 →
///    Skip 并附只读 leftover acad PID 诊断 + spec §5 一次性配置指引）——**不尝试 NETLOAD**：
///    AutoCAD 2014 对未签名程序集弹安全确认框，SendCommand 同步阻塞（spike 实测 45 分钟）；
/// ③ 标本副本可用（复用 <see cref="AcadNativeLiveFixture"/>：副本 + 源件/副本 SHA + acad 零残留金丝雀）；
/// ④ CUIX 金丝雀基线**非空**（与切片A 同一定位器 <see cref="AcadNativeLiveFixture.CuixHashes"/>；
///    空集 → Skip("金丝雀空集（CuixCount=0）")，不静默通过；非空 → 前后指纹比对 + 输出 "CUIX count=N sha=…"）。
/// 纪律：所有修改只作用**副本**；源件只 SHA256 比对、绝不打开写入；每测试自建 <see cref="AcadRuntime"/>
/// 并 Dispose（Close(false)+Quit+WaitForProcessExit）；绝不配置 TRUSTEDPATHS / 改任何 AutoCAD 设置。
/// 四条事实：
/// T1 READ 基准（证据数 ≥300，探针基准 343 只记日志；21E00 extents；XData HYDRANT_RADIUS=28）；
/// T2 递归（NestedTdbCount&gt;0 残留行；标本无此类行→Skip）；
/// T3 EXPLODE_REPLACE（NewCount&gt;0/BboxMatch、实体数 before-1+NewCount、读回无 21E00、.live.dwg 落盘、源件/副本/CUIX 不变）；
/// A4 TDbSymbArrow 永久 FAIL 披露（自 T1 拆出：标本无样本→Skip，不拖累 T1 已证部分）。
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

    // XData 探针基线：天正设备键 HYDRANT_RADIUS = 28（字面值，不断言浮点等价）。
    private const string HydrantRadiusKey = "HYDRANT_RADIUS";
    private const string HydrantRadiusExpected = "28";

    // TDbSymbArrow：几何双通道 eInvalidInput → 永久 FAIL（Core 只映射为 FullyExplodable=false）。
    private const string SymbolArrowRxClass = "TDbSymbArrow";

    // 探针实证 extents（切片A基准逐字：120300/130100/3000）。
    private static readonly double[] WallBbox = { 127749.996, 120300, 0, 127849.996, 130100, 3000 };

    private readonly AcadNativeLiveFixture _fx;
    private readonly ITestOutputHelper _out;
    private readonly string? _origBridgeDll = AcadRuntime.BridgeDllOverride;
    private readonly TimeSpan _origNetLoadTimeout = AcadRuntime.NetLoadTimeout;

    // CUIX 金丝雀基线（RequireLiveEnvironment 在任何 COM 之前经同一定位器实算；非空才继续）。
    private Dictionary<string, string> _cuixBefore = new(StringComparer.OrdinalIgnoreCase);

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
    /// T1 READ 基准：ReadExplodeEvidence(副本) → 证据数 ≥300（精确 343 只记日志，不硬断言）；
    /// 21E00（TDbWall）RxClass 逐字 + extents 与切片A基准逐字（1e-2）、FullyExplodable=true；
    /// XData HYDRANT_RADIUS 实读值 == 28（不止断言键存在）。
    /// TDbSymbArrow 已拆到 <see cref="A4_Read_SymbArrowFailDisclosure"/>（A4）——
    /// 其标本缺样本时的 Skip 不会连带作废本测试已证部分。
    /// </summary>
    [SkippableFact]
    public void T1_Read_BaselineCountBboxXData()
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

        // 下限硬断言；探针精确值 343 只记日志（标本/天正版本可变，硬等 343 会伪失败）。
        Assert.True(evidences.Count >= MinEvidenceCount,
            $"深度读证据数 {evidences.Count} < 下限 {MinEvidenceCount}（探针基准 {ProbeEvidenceCount}；空读/桥截断须显式失败）");
        if (evidences.Count != ProbeEvidenceCount)
            _out.WriteLine($"[T1] 注意：证据数 {evidences.Count} ≠ 探针基准 {ProbeEvidenceCount}（只记录不判失败）");
        else
            _out.WriteLine($"[T1] 证据数与探针基准一致（{ProbeEvidenceCount}）");

        // 21E00（TDbWall）：RxClass 逐字、extents 与切片A基准逐字、完全炸开。
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

        // XData：HYDRANT_RADIUS 键必须存在且实读值 == 28（探针基线；不止断言键存在）。
        var hydrant = evidences.Where(e => e.XData.ContainsKey(HydrantRadiusKey)).ToList();
        Assert.True(hydrant.Count > 0, $"全量证据中无任何 {HydrantRadiusKey} XData 键（天正 XData 通道失效或标本不含设备）");
        foreach (var row in hydrant)
            _out.WriteLine($"[T1] XData {HydrantRadiusKey} handle={row.Handle} rx={row.RxClass} " +
                           $"value={row.XData[HydrantRadiusKey]}（探针基线 {HydrantRadiusExpected}）");
        Assert.True(hydrant.Any(e => string.Equals(e.XData[HydrantRadiusKey], HydrantRadiusExpected, StringComparison.Ordinal)),
            $"{HydrantRadiusKey} 共 {hydrant.Count} 行，无一等于探针基线 {HydrantRadiusExpected}；" +
            "实读值=" + string.Join("/", hydrant.Select(e => $"{e.Handle}:{e.XData[HydrantRadiusKey]}").Take(10)));

        rt.Dispose();
        AssertCuixCanary("bridge-read");
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

        AssertCuixCanary("bridge-recursion");
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
    /// A4（自 T1 拆出）TDbSymbArrow 永久 FAIL 披露：桥内几何双通道 eInvalidInput → 永久 FAIL，
    /// Core 无逐实体 Fail 列表，FAIL 只映射为 FullyExplodable=false，此处断言至少一行 false。
    /// 标本无 arrow 行 → Skip("标本无 TDbSymbArrow 样本")——独立成事实，
    /// 其样本缺失不再连带作废 T1 已证的计数/bbox/XData 部分。
    /// </summary>
    [SkippableFact]
    public void A4_Read_SymbArrowFailDisclosure()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        var evidences = rt.ReadExplodeEvidence(_fx.CopyPath);
        rt.Dispose();

        var arrows = evidences.Where(e => e.RxClass == SymbolArrowRxClass).ToList();
        var arrowFailed = arrows.Where(e => !e.FullyExplodable).ToList();
        _out.WriteLine($"[A4] evidence={evidences.Count} {SymbolArrowRxClass} rows={arrows.Count} " +
                       $"notFully={arrowFailed.Count}");

        AssertCuixCanary("bridge-symb-arrow");
        _fx.AssertCanaries("bridge-symb-arrow");

        Skip.If(arrows.Count == 0, "标本无 TDbSymbArrow 样本");
        Assert.True(arrowFailed.Count > 0,
            $"{SymbolArrowRxClass} {arrows.Count} 行全部 FullyExplodable=true（探针实证 eInvalidInput 失败应如实披露为 false）");
        foreach (var row in arrowFailed.Take(5))
            _out.WriteLine($"[A4] {SymbolArrowRxClass} FAIL handle={row.Handle} prims={row.PrimitiveCount} " +
                           $"nested={row.NestedTdbCount} bbox={(row.Bbox == null ? "null" : Fmt(row.Bbox))}");
    }

    /// <summary>
    /// T3 EXPLODE_REPLACE（**副本**上，生产 native-live 会话序）：先 ReadNativeParams(副本) 完成
    /// /p 天正会话开路（单独 Open 走的是 EnsureApp() 默认 profile——天正未载，TDb 实体不可炸），
    /// 再 Open(副本) + ExplodeReplaceByHandle("21E00", 副本+".live.dwg")。
    /// 断言：NewCount&gt;0、BboxMatch=true、实体数守恒（after == before − 1 + NewCount，即 1 换 NewCount）、
    /// 读回无 21E00、.live.dwg 落盘；
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
        // 替换前实体数（Open 后、替换前；爆炸替换=删 1 换 NewCount，总数须守恒）。
        int entityBefore = rt.EntityCount();
        _out.WriteLine($"[T3] modelSpace entityCount before={entityBefore}");

        var rep = rt.ExplodeReplaceByHandle(WallHandle, liveDwg);
        int entityAfter = rt.EntityCount();
        _out.WriteLine($"[T3] REPLACED handle={rep.Handle} newCount={rep.NewCount} bboxMatch={rep.BboxMatch} " +
                       $"tolerance={rep.Tolerance:R} saved={rep.SavedPath} " +
                       $"entityCount {entityBefore}->{entityAfter}（期望 {entityBefore - 1 + rep.NewCount}）");

        Assert.True(rep.NewCount > 0, $"替换叶数 {rep.NewCount} 非正（炸开空产物）");
        Assert.True(rep.BboxMatch,
            $"替换前后 bbox 不一致（BboxMatch=false，容差 {rep.Tolerance:R}）——包含性判据失败");
        Assert.True(string.Equals(rep.Handle, WallHandle, StringComparison.OrdinalIgnoreCase),
            $"REPLACED 句柄 {rep.Handle} ≠ 请求句柄 {WallHandle}");
        // 实体数守恒：1 实体被替换为 NewCount 个叶产物（after == before − 1 + NewCount）。
        Assert.True(entityAfter == entityBefore - 1 + rep.NewCount,
            $"替换后模型空间实体数不守恒：before={entityBefore} after={entityAfter} newCount={rep.NewCount}"
            + $"（期望 {entityBefore - 1 + rep.NewCount}）——替换漏删/漏插或桥读回窗口失真");

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

        AssertCuixCanary("bridge-explode-replace");
        _fx.AssertCanaries("bridge-explode-replace");
    }

    // ---- 边界补齐（A1/A2/B1）真机骨架：TDbECabletry/TDbEFitting + 块定义独立段 + 退化 extents ----

    private const string ElecTdbeSpecimenDirEnv = "CADAGENT_ELEC_TDBE_SPECIMEN_DIR";
    private const string ElecCabletryRx = "TDbECabletry";
    private const string ElecFittingRx = "TDbEFitting";
    // spike 实证：16#1116 退化 extents 恰 47 行；下限 40 吸收标本/版本可变，精确值只记日志。
    private const int MinDegenerateExtentsRows = 40;
    private const int SpikeDegenerateExtentsRows = 47;

    /// <summary>
    /// T5（A2/B1 电气类）：elec-tdbe-01 副本 → ReadExplodeEvidence → 证据含
    /// <c>TDbECabletry</c> 与 <c>TDbEFitting</c> 各至少一行（缺失即 Skip + 直方图，绝不伪造绿）；
    /// 逐类记录 extents 可得性与炸开状态；源副本 SHA 不变 + CUIX/acad 零残留金丝雀。
    /// </summary>
    [SkippableFact]
    public void T5_ElecTdbeClasses_Read()
    {
        // M-6：电类标本自证存在性（CreateElecCopy 内 Skip），不依赖 16#1116 夹具。
        RequireLiveEnvironment(needsWallSpecimen: false);
        string copy = CreateElecCopy("elec-tdbe-01.dwg");
        try
        {
            string shaBefore = AcadNativeLiveFixture.Sha256File(copy);
            using var rt = new AcadRuntime();
            var evidences = rt.ReadExplodeEvidence(copy);
            rt.Dispose();

            string histogram = string.Join("/", evidences
                .GroupBy(e => e.RxClass, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(12)
                .Select(g => $"{g.Key}:{g.Count()}"));
            _out.WriteLine($"[T5] evidence={evidences.Count} rxClassTop={histogram}");

            var cabletry = evidences.Where(e => e.RxClass == ElecCabletryRx).ToList();
            var fitting = evidences.Where(e => e.RxClass == ElecFittingRx).ToList();
            LogClassRows("T5", ElecCabletryRx, cabletry);
            LogClassRows("T5", ElecFittingRx, fitting);

            AssertCuixCanary("elec-tdbe-read");
            AssertElecCanaries("elec-tdbe-read");
            Assert.Equal(shaBefore, AcadNativeLiveFixture.Sha256File(copy));

            Skip.If(cabletry.Count == 0,
                $"elec-tdbe-01 未见 {ElecCabletryRx} 证据行（直方图 {histogram}）——标本缺类或桥读空，不伪造绿");
            Skip.If(fitting.Count == 0,
                $"elec-tdbe-01 未见 {ElecFittingRx} 证据行（直方图 {histogram}）——标本缺类或桥读空，不伪造绿");
            Assert.All(cabletry, e => Assert.Equal(ElecCabletryRx, e.RxClass));
            Assert.All(fitting, e => Assert.Equal(ElecFittingRx, e.RxClass));
        }
        finally { CleanupElecCopy(copy); }
    }

    /// <summary>
    /// T6（B1 块定义独立段）：elec-tdbe-02 副本 → ReadExplodeEvidence + ReadExplodeBlockDefs
    ///（同一次桥会话的伴生读数，缓存命中不二次开图）。断言块定义表非空且含电气类之一；
    /// 块定义行（按块名）**不得**混入模型空间证据句柄集（定义 ≠ 实例，不得双计）；
    /// (blockName,rxClass) 唯一；金丝雀同上。
    /// M-6 覆盖面说明：本真机骨架只证"真桥/真标本"一段；语义边界由 fake 测试钉住——
    /// Data <c>Enrich_BlockDefs_DisclosureOnly_DoesNotTouchAnnotationsOrNativeEntities</c>
    ///（不改标注/原生表、只入独立表）与 Cli <c>ScanExplode_BlockDefTdb_SeparateSection_AdditiveKeys</c>
    ///（stdout 与本轮输入逐字一致、explode 四计数不变）。
    /// </summary>
    [SkippableFact]
    public void T6_BlockDefTdb_SeparateSection()
    {
        // M-6：电类标本自证存在性（CreateElecCopy 内 Skip），不依赖 16#1116 夹具。
        RequireLiveEnvironment(needsWallSpecimen: false);
        string copy = CreateElecCopy("elec-tdbe-02.dwg");
        try
        {
            string shaBefore = AcadNativeLiveFixture.Sha256File(copy);
            using var rt = new AcadRuntime();
            var evidences = rt.ReadExplodeEvidence(copy);
            var blockDefs = rt.ReadExplodeBlockDefs(copy);
            rt.Dispose();

            _out.WriteLine($"[T6] evidence={evidences.Count} blockDefs={blockDefs.Count}");
            foreach (var d in blockDefs.Take(12))
                _out.WriteLine($"[T6] BDEF block={d.BlockName} rx={d.RxClass} count={d.Count}");

            AssertCuixCanary("elec-tdbe-blockdef");
            AssertElecCanaries("elec-tdbe-blockdef");
            Assert.Equal(shaBefore, AcadNativeLiveFixture.Sha256File(copy));

            Skip.If(blockDefs.Count == 0,
                "elec-tdbe-02 未见任何块定义 TDb 行（桥 BlockTable 遍历失效或标本无 TDb 块定义），不伪造绿");
            Assert.Contains(blockDefs, d => d.RxClass == ElecCabletryRx || d.RxClass == ElecFittingRx);

            // 分离段口径：块定义按块名聚合，绝不冒充模型空间实例（证据整表按句柄）。
            var handles = new HashSet<string>(evidences.Select(e => e.Handle), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(blockDefs, d => handles.Contains(d.BlockName));
            Assert.Equal(blockDefs.Count, blockDefs
                .Select(d => d.BlockName + "\u0000" + d.RxClass)
                .Distinct(StringComparer.Ordinal)
                .Count());
        }
        finally { CleanupElecCopy(copy); }
    }

    /// <summary>
    /// T7（A1 退化 extents）：16#1116 副本 → 证据中 FailReason=="extents:degenerate" 行数 ≥40
    ///（spike 实证 47 只记日志），且每行 Bbox==null（ENTITY 行 bbox 字段留空）；
    /// 反向不变量：任何非空 bbox 行不得六分量全近零（退化判据漏网）。
    /// </summary>
    [SkippableFact]
    public void T7_DegenerateExtents_NullBbox()
    {
        RequireLiveEnvironment();

        using var rt = new AcadRuntime();
        var evidences = rt.ReadExplodeEvidence(_fx.CopyPath);
        rt.Dispose();

        var degenerate = evidences.Where(e => e.FailReason == "extents:degenerate").ToList();
        _out.WriteLine($"[T7] evidence={evidences.Count} degenerate={degenerate.Count} " +
                       $"(spike={SpikeDegenerateExtentsRows})");
        _out.WriteLine("[T7] degenerateTop=" + string.Join("/", degenerate
            .GroupBy(e => e.RxClass, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => $"{g.Key}:{g.Count()}")));

        AssertCuixCanary("degenerate-extents");
        _fx.AssertCanaries("degenerate-extents");

        Assert.True(degenerate.Count >= MinDegenerateExtentsRows,
            $"退化 extents 行 {degenerate.Count} < 下限 {MinDegenerateExtentsRows}"
            + $"（spike 实证 {SpikeDegenerateExtentsRows}）——A1 判据失效或标本变更，不得伪造绿");
        foreach (var e in degenerate)
            Assert.Null(e.Bbox);   // A1：退化 → null，不得落 0,0,0..0,0,0
        foreach (var e in evidences.Where(x => x.Bbox != null))
            Assert.True(e.Bbox!.Any(v => Math.Abs(v) >= 1e-6),
                $"{e.Handle} 出现六分量全近零 bbox（应判退化→null，A1 判据漏网）");
    }

    /// <summary>逐类日志：句柄/bbox/炸开状态/FailReason（真机证据可复核）。</summary>
    private void LogClassRows(string stage, string rx, IReadOnlyList<ExplodeEvidence> rows)
    {
        foreach (var r in rows.Take(5))
            _out.WriteLine($"[{stage}] {rx} handle={r.Handle} bbox={(r.Bbox == null ? "null" : Fmt(r.Bbox))} " +
                           $"fully={r.FullyExplodable} prims={r.PrimitiveCount} fail={r.FailReason ?? "-"}");
    }

    /// <summary>电气标本目录：环境变量 <c>CADAGENT_ELEC_TDBE_SPECIMEN_DIR</c> 覆盖，
    /// 默认仓库 <c>outputs/specimens/elec-tdbe</c>；缺失 → Skip（标本 local-only 不入库）。</summary>
    private string CreateElecCopy(string fileName)
    {
        string? dir = Environment.GetEnvironmentVariable(ElecTdbeSpecimenDirEnv);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(RepoRoot(), "outputs", "specimens", "elec-tdbe");
        string source = Path.Combine(dir, fileName);
        Skip.If(!File.Exists(source),
            $"电气标本不在本机: {source}（可用环境变量 {ElecTdbeSpecimenDirEnv} 覆盖目录），跳过真机测试");

        string copyDir = Directory.CreateTempSubdirectory("elec-tdbe").FullName;
        string copy = Path.Combine(copyDir, fileName);
        File.Copy(source, copy);
        return copy;
    }

    private static void CleanupElecCopy(string copyPath)
    {
        try { Directory.Delete(Path.GetDirectoryName(copyPath)!, true); }
        catch { /* best-effort（AutoCAD 句柄释放延迟） */ }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("未找到仓库根（CadAgent.sln）");
        return dir.FullName;
    }

    // ---- guards ----

    private void RequireLiveEnvironment(bool needsWallSpecimen = true)
    {
        // M-6（修复波1）：16#1116 标本闸只对依赖它的 T1-T4/T7 生效。T5/T6 用电气标本
        // （elec-tdbe-01/02），其存在性由 CreateElecCopy 各自 Skip——不得因 16#1116 缺失
        // 而把电类用例误 Skip 成"标本不可用"（反之亦然）。
        if (needsWallSpecimen)
            Skip.If(!_fx.SpecimenReady, _fx.SpecimenSkipReason ?? "真实电气标本不可用，跳过真机测试");

        // 前置进程纪律（与 <see cref="AcadProbe"/> 第四态同口径）：起始就有 acad 进程 → **Skip**
        // （并发激活= CUIX 竞态，且残留实例必被前次启动期模态框滞留）；残留非本测试缺陷，
        // 硬失败会掩盖真因。收尾金丝雀（AssertCanaries）仍**硬断言** Dispose 后 0 残留。
        // 计数独立实算：夹具仅在 16#1116 存在时赋值 AcadProcessCountBefore，本闸对电类用例同样适用。
        int acadBefore = AcadNativeLiveFixture.AcadProcessTokens().Count;
        Skip.If(acadBefore > 0,
            $"起始已有 acad 进程 {acadBefore} 个（不并发激活/CUIX 竞态守卫）——"
            + "已运行的实例可能仍挂着启动期模态框，须人工关闭后重跑"
            + BridgeLiveGuard.LeftoverNote("测试启动时"));

        // CUIX 金丝雀基线（纯只读文件 I/O，无 COM，故置于守卫之前——每次 live 运行输出都带基线）：
        // 与切片A **同一定位器**（AcadNativeLiveFixture.CuixHashes，遍历
        // %APPDATA%\Autodesk\AutoCAD*\**\acad.cuix；Windows 文件名不区分大小写 ⇒ acad.CUIX 亦命中，
        // 这正是首版报告"0 cuix"的误判根源：大小写敏感的目录检索判空，定位器本身是对的）。
        // 空集 → 显式 Skip（金丝雀恒等=弱证据，不静默通过）；非空 → 记录基线供收尾逐字比对。
        _cuixBefore = AcadNativeLiveFixture.CuixHashes();
        _out.WriteLine("[canary] CUIX before count=" + _cuixBefore.Count + " sha=" + FmtCuix(_cuixBefore));
        Skip.If(_cuixBefore.Count == 0,
            "金丝雀空集（CuixCount=0）——同一定位器 "
            + "AcadNativeLiveFixture.CuixHashes()（%APPDATA%\\Autodesk\\AutoCAD*\\**\\acad.cuix）未找到任何 CUIX；"
            + "CUIX 金丝雀将退化为空集恒等（弱证据），按纪律显式 Skip 而非静默通过");
        if (_cuixBefore.Count != _fx.CuixCount)
            _out.WriteLine($"[canary] 注意：本测试实算 CUIX 数 {_cuixBefore.Count} ≠ 夹具基线 {_fx.CuixCount}（应同时点采样，只记录）");

        // 守卫顺序：桥/信任判定**先于** AcadProbe——AcadProbe 会先拉起默认 profile 实例，
        // 该实例在退出时可能被启动期模态框滞留（本机实证"不支持硬件加速"框），滞留进程会让
        // 后续 /p 启动的 EnsureNoForeignInstance 拒绝、TRUSTEDPATHS 永远读不到；先读才拿得到真值。
        // 守卫自身首步是 ProgID 注册检查（不触 COM）：无 AutoCAD 时立即 Skip，绝不激活进程。
        var guard = BridgeLiveGuard.State;
        _out.WriteLine($"[guard] bridgeDll={guard.DllPath ?? "<缺失>"}");
        _out.WriteLine($"[guard] TRUSTEDPATHS={(guard.TrustedPaths == null ? "<未读取>" : FmtTrusted(guard.TrustedPaths))}");
        Skip.If(!guard.Ok, guard.SkipReason);

        Skip.If(!AcadProbe.Available, AcadProbe.SkipReason);
        AcadRuntime.BridgeDllOverride = guard.DllPath;   // 测试必须指向构建产物（net45 桥）
    }

    // ---- helpers ----

    /// <summary>收尾 CUIX 金丝雀（同一定位器实算）：数量 + 逐文件 SHA 指纹逐字相等，并把
    /// "CUIX count=N sha=…" 打入测试输出（真机证据可复核）。调用前提：本方 AcadRuntime 已 Dispose。</summary>
    private void AssertCuixCanary(string stage)
    {
        var after = AcadNativeLiveFixture.CuixHashes();
        _out.WriteLine($"[{stage}] CUIX count={after.Count} sha={FmtCuix(after)}");
        Assert.Equal(_cuixBefore.Count, after.Count);
        Assert.Equal(AcadNativeLiveFixture.CuixFingerprint(_cuixBefore),
            AcadNativeLiveFixture.CuixFingerprint(after));
    }

    /// <summary>电类标本收尾金丝雀（T5/T6 专用）：CUIX 指纹 + acad 零残留。
    /// 不复用 <see cref="AcadNativeLiveFixture.AssertCanaries"/>——后者硬断言 16#1116 标本可用/字节不变，
    /// 而 T5/T6 的标本是 elec-tdbe-01/02（副本 SHA 由用例内联断言）。</summary>
    private void AssertElecCanaries(string stage)
    {
        AssertCuixCanary(stage);
        var acad = AcadNativeLiveFixture.AcadProcessTokens();
        Assert.True(acad.Count == 0,
            $"[{stage}] 残留 acad.exe 进程: {string.Join(",", acad)}（自家实例 Dispose 后须零残留）");
    }

    /// <summary>CUIX 指纹摘要（路径排序；"文件名=sha256"，多文件逗号分隔）。</summary>
    private static string FmtCuix(IReadOnlyDictionary<string, string> map) =>
        map.Count == 0
            ? "<空集>"
            : string.Join(",", map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{Path.GetFileName(kv.Key)}={kv.Value}"));

    private static bool HandleEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Fmt(double[] v) =>
        "(" + string.Join(",", v.Select(d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + ")";

    private static string FmtTrusted(string v) =>
        v.Length == 0 ? "<空字符串>" : "\"" + v + "\"";
}

/// <summary>
/// 桥真机守卫（Lazy 一次求值，类内各测试共享）：
/// 桥 DLL 定位 → **ProgID 注册前置检查（不触 COM/不启动进程）** → 注册表面读 TRUSTEDPATHS
/// （<c>AcadRuntime.ReadTrustedPathsFromRegistry(TArchProfileName)</c>，R17：**不建实例、不发 COM**；
/// 旧写法 <c>GetTrustedPaths()</c> 会 /p 拉起整实例、启动期 Quit 被忽略而留孤儿，已改）
/// → 桥目录是否落在信任位置（TRUSTEDPATHS 含子目录，故祖先目录亦算信任）。
/// 任何失败 → Skip 原因（附**只读** leftover acad PID 诊断；绝不强杀，人工清理）；绝不上抛失败；
/// 绝不写注册表 / 改任何 AutoCAD 设置。
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

        // COM 前置：ProgID 未注册 → 直接 Skip（Type.GetTypeFromProgID 只读注册表，不激活进程）。
        // 否则 Activated 路径会先拉起 acad.exe（或抛孤儿注册异常），Skip 原因离开真因。
        if (Type.GetTypeFromProgID(VendorProfiles.Acad.ProgId) == null)
            return new BridgeGuard(false,
                $"本机无 AutoCAD ProgID 注册（{VendorProfiles.Acad.ProgId}）——无法实读 TRUSTEDPATHS，"
                + "不尝试 NETLOAD。" + LeftoverNote("ProgID 预检"), dll, null);

        // R17：注册表优先——**不创建 AcadRuntime、不触 COM、不启动 acad.exe**。
        // 真机根因（Fix round 4）：经 COM 读 TRUSTEDPATHS 必须先 /p 拉起整实例，而启动初始化期
        // Quit 被忽略 → 孤儿 acad.exe；守卫随后只会自跳过"残留进程"，真因被残留掩盖。
        // 注册表值即「选项→文件→信任位置」的持久化镜像（只读 REG_SZ），读它无需实例。
        string? trusted = AcadRuntime.ReadTrustedPathsFromRegistry(AcadRuntime.TArchProfileName);
        if (trusted == null)
        {
            // 注册表也无值：**不以启动实例为代价回退**（正是本轮要消除的根因），如实 Skip。
            return new BridgeGuard(false,
                $"注册表未找到 TRUSTEDPATHS（HKCU\\{AcadRuntime.RegistryProductRoot}"
                + $"\\ACAD-*\\Profiles\\{AcadRuntime.TArchProfileName}\\Variables）——"
                + "守卫不启动 AutoCAD 实例（启动期 Quit 会被忽略、留孤儿），故不尝试 NETLOAD。"
                + LeftoverNote("预检前") + Guidance(dll), dll, null);
        }

        var leftover = AcadNativeLiveFixture.AcadProcessTokens();
        string leftoverNote = leftover.Count == 0
            ? ""
            : $"（注意：预检前已存在 acad 进程 {string.Join(",", leftover)}，不并发激活；须人工处理后方可重跑，测试不做强杀）";

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
                $"预检前已存在 acad 进程 {string.Join(",", leftover)}（不并发激活；非本守卫产生的残留）" + leftoverNote, dll, trusted);

        return new BridgeGuard(true, null, dll, trusted);
    }

    /// <summary>只读 leftover 诊断（tasklist 口径，<c>Process.GetProcessesByName("acad")</c> 等价集合）：
    /// 计数/PID 打进 Skip 原因；**绝不 kill**——残留由用户人工清理。</summary>
    internal static string LeftoverNote(string when)
    {
        var leftover = AcadNativeLiveFixture.AcadProcessTokens();
        return leftover.Count == 0
            ? $"（{when}：无残留 acad 进程）"
            : $"（{when}：残留 acad 进程 {leftover.Count} 个 [{string.Join(",", leftover)}]——"
              + "测试不做强杀，须人工清理后方可重跑）";
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
