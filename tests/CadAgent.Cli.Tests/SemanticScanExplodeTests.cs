using System.Text;
using System.Text.Json;
using CadAgent.Adapters.Acad;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;
using Xunit;

namespace CadAgent.Cli.Tests;

/// <summary>
/// CLI `semantic scan --explode`（切片B T8）：深度炸开取证落 overlay（ExplodeEvidence 独立表 +
/// 21E00 中心线推导 + NativeExplode 0.9 标注）+ stdout/审计加性五键
/// explodeRead/explodeFully/explodePartial/explodeFailed/explodeSymbolCandidates（无 --explode 恒 0/空）。
/// 裁定口径（逐条钉住）：
/// R8 —— 测试注入 RuntimeFactory 优先；否则 ResolveRuntimeName()!=acad → stderr 警示
/// "--explode 需 CADAGENT_RUNTIME=acad"并跳过（计数全 0）；acad → 直接 CreateRuntimeInstance("acad")
/// （不走 CreateRuntimeIfAvailable 的 GetInfo 预激活）。
/// R9 —— 构造/读证据任何异常 → 审计 explode-read-failed(params{error}) + stderr 降级提示，
/// 计数/候选保持 0/空，扫描继续到正常 stdout/审计/exit 0（理解侧诚实降级）。
/// 顺序 —— Enrich 必须在 SemanticScanner.Scan 之后且恰一次调用全量证据（增量调用会回收他行标注）。
/// 全用 fake runtime（绝不起真厂商进程）；静态 seam/环境变量用完即还原（本程序集已禁并行）。
/// </summary>
public class SemanticScanExplodeTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"semscan-explode-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly Func<ICadRuntime?>? _originalFactory = CommandRouter.RuntimeFactory;
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;
    private readonly string? _originalEnv = Environment.GetEnvironmentVariable("CADAGENT_RUNTIME");
    // I1（PR#30 终审）：桥缝——默认"可用"（真桥命令标记 + 信任位置=其目录），三态不可用面由用例覆写。
    private readonly string? _originalBridgeDll = AcadRuntime.BridgeDllOverride;
    private readonly Func<string, string?> _originalTrustedSource = AcadRuntime.TrustedPathsSource;
    private readonly string _bridgeDir;

    public SemanticScanExplodeTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-EXPLODETEST"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0),
                EndPoint = (CSMath.XYZ)new CSMath.XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "W-EXPLODETEST")
            });
        });
        // I1：预检先于运行时构造——夹具默认"桥可用"（占位产物含真桥命令标记；信任位置=其目录）。
        _bridgeDir = Path.Combine(_project, "bridge");
        Directory.CreateDirectory(_bridgeDir);
        string bridgeDll = Path.Combine(_bridgeDir, AcadRuntime.BridgeDllName);
        File.WriteAllBytes(bridgeDll, Encoding.ASCII.GetBytes("CADAGENT_TARCH_READ"));
        AcadRuntime.BridgeDllOverride = bridgeDll;
        AcadRuntime.TrustedPathsSource = _ => _bridgeDir;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = _originalFactory;
        CommandRouter.RuntimeSelector = _originalSelector;
        Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", _originalEnv);
        AcadRuntime.BridgeDllOverride = _originalBridgeDll;
        AcadRuntime.TrustedPathsSource = _originalTrustedSource;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    private string OverlayPath => Path.Combine(_project, ".cadagent", "semantic.json");

    /// <summary>brief 逐字 fake：21E00 TDbWall 全炸开（两平行线 → 可推中心线）；
    /// 321A TDbSymbArrow 未全炸开且零产物（=explodeFailed 等价类）。</summary>
    private sealed class FakeExplodeRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath) => new[]
        {
            new ExplodeEvidence("21E00", "TDbWall", "ImpCurve", "0dt",
                new[] { 0.0, 0, 0, 100.0, 300.0, 3000.0 }, true, 2, 0,
                new List<ExplodePrimitive>
                {
                    new("line", new[] { 0.0, 0, 0, 100.0, 0, 0 }, null, null),
                    new("line", new[] { 0.0, 100, 0, 100.0, 100, 0 }, null, null),
                },
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
            new ExplodeEvidence("321A", "TDbSymbArrow", "ImpEntity", "C公用-符号",
                null, false, 0, 0, Array.Empty<ExplodePrimitive>(),
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
        };
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void Dispose() { }
    }

    /// <summary>--explode + --native 并存 fake：两个读通道各自独立供数（Bbox=null 使
    /// TarchDerive 种子不做 bbox 比对，两类标注可共存）。</summary>
    private sealed class FakeExplodeAndNativeRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) => new[]
        {
            new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
                new Dictionary<string, string> { ["LeftWidth"] = "100" },
                null, DateTimeOffset.UtcNow, true, "0dt")
        };
        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath) => new[]
        {
            new ExplodeEvidence("21E00", "TDbWall", "ImpCurve", "0dt",
                new[] { 0.0, 0, 0, 100.0, 300.0, 3000.0 }, true, 2, 0,
                new List<ExplodePrimitive>
                {
                    new("line", new[] { 0.0, 0, 0, 100.0, 0, 0 }, null, null),
                    new("line", new[] { 0.0, 100, 0, 100.0, 100, 0 }, null, null),
                },
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
            new ExplodeEvidence("321A", "TDbSymbArrow", "ImpEntity", "C公用-符号",
                null, false, 0, 0, Array.Empty<ExplodePrimitive>(),
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
        };
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void Dispose() { }
    }

    /// <summary>符号块候选 fake：一个含 $DorLib2D$ 库块的证据（RxClass 未实证=Unknown →
    /// 只披露候选、不落 NativeExplode 标注）。</summary>
    private sealed class FakeSymbolCandidateRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath) => new[]
        {
            new ExplodeEvidence("30B01", "TDbDoor", "ImpEntity", "C门窗",
                null, true, 1, 0,
                new List<ExplodePrimitive>
                {
                    new("block", Array.Empty<double>(), null, "$DorLib2D$测试门"),
                },
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
        };
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void Dispose() { }
    }

    /// <summary>B1（边界补齐）fake：证据一条（全炸开直墙）+ 独立块定义表两条（<c>BDEF</c> 语义的
    /// 宿主映射产物）。用于钉住 `--explode` 的 <c>blockDefTdb</c> 加性披露与 overlay 持久化，
    /// 且**不改变**既有 explode 四计数口径（证据列表未加行）。</summary>
    private sealed class FakeBlockDefRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath) => new[]
        {
            new ExplodeEvidence("21E00", "TDbWall", "ImpCurve", "0dt",
                new[] { 0.0, 0, 0, 100.0, 300.0, 3000.0 }, true, 0, 0,
                Array.Empty<ExplodePrimitive>(),
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow),
        };
        public IReadOnlyList<BlockDefTdbEntry> ReadExplodeBlockDefs(string drawingPath) => new[]
        {
            new BlockDefTdbEntry("$equip$00002832", "TDbECabletry", 1364),
            new BlockDefTdbEntry("门块A", "TDbEFitting", 2),
        };
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void Dispose() { }
    }

    private static (int read, int fully, int partial, int failed) ExplodeCounts(string stdout)    {
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        return (root.GetProperty("explodeRead").GetInt32(),
                root.GetProperty("explodeFully").GetInt32(),
                root.GetProperty("explodePartial").GetInt32(),
                root.GetProperty("explodeFailed").GetInt32());
    }

    private AuditRecord? SingleExplodeReadFailed()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        var rec = all.SingleOrDefault(r => r.Entry.Kind == "explode-read-failed");
        if (rec is not null)
        {
            Assert.Equal("semantic", rec.Entry.Source);
            Assert.Equal(_drawing, rec.Entry.Target);
            // R9：与扫描同 store/session/操作人（扫描照常留痕，失败条目同会话）。
            var scan = all.Single(r => r.Entry.Kind == "semantic-scan");
            Assert.Equal(scan.Entry.Session, rec.Entry.Session);
            Assert.Equal(scan.Entry.Operator, rec.Entry.Operator);
            Assert.Equal(scan.Entry.Role, rec.Entry.Role);
            Assert.True(audit.VerifyChain(out _));
        }
        return rec;
    }

    /// <summary>
    /// brief 主用例：--explode + fake → overlay.ExplodeEvidence=2 行（21E00 CenterlineJson 非空）+
    /// NativeExplode 0.9 标注；stdout 四计数 2/1/0/1 + 候选数组恒在；旧键 version/annotations/
    /// ambiguous/sheets 逐字在；审计 semantic-scan params 加性五键且旧键 annotationCount 仍在。
    /// </summary>
    [Fact]
    public void ScanExplode_SeedsEvidence_AndReportsCounts()
    {
        CommandRouter.RuntimeFactory = () => new FakeExplodeRuntime();
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--explode");
            Assert.Equal(0, code);
            Assert.DoesNotContain("CADAGENT_RUNTIME=acad", stderr); // 注入工厂路径不触发厂商警示

            // ① overlay 落盘（进程结束后磁盘重载）：两行证据，21E00 中心线已推导。
            var overlay = SemanticOverlay.Load(OverlayPath);
            Assert.Equal(2, overlay.ExplodeEvidence.Count);
            var wall = Assert.Single(overlay.ExplodeEvidence, e => e.Handle == "21E00");
            Assert.True(wall.FullyExplodable);
            Assert.False(string.IsNullOrEmpty(wall.CenterlineJson));
            using (var cl = JsonDocument.Parse(wall.CenterlineJson!))
            {
                // 两条 y=0/y=100 平行线 → 中线 y=50、宽 100（固定字段序 kind/p1/p2/width）。
                var root = cl.RootElement;
                Assert.Equal("line", root.GetProperty("kind").GetString());
                Assert.Equal(50.0, root.GetProperty("p1")[1].GetDouble(), 3);
                Assert.Equal(50.0, root.GetProperty("p2")[1].GetDouble(), 3);
                Assert.Equal(100.0, root.GetProperty("width").GetDouble(), 3);
            }
            var sym = Assert.Single(overlay.ExplodeEvidence, e => e.Handle == "321A");
            Assert.False(sym.FullyExplodable);
            Assert.Null(sym.CenterlineJson);

            // ② NativeExplode 0.9 标注（只给全炸开且类已知且 bbox 口径一致的证据）。
            var annotation = Assert.Single(overlay.Annotations, a => a.Provenance == Provenance.NativeExplode);
            Assert.Equal("21E00", annotation.TargetHandle);
            Assert.Equal(SemanticClass.Wall, annotation.Class);
            Assert.Equal(0.9, annotation.Confidence);
            Assert.Equal("NETLOAD 炸开证据: TDbWall", annotation.Evidence);

            // ③ stdout 加性四计数 2/1/0/1 + 候选数组恒在（本 fake 无库块 → 空）+ 旧键逐字在。
            Assert.Equal((2, 1, 0, 1), ExplodeCounts(stdout));
            using (var doc = JsonDocument.Parse(stdout))
            {
                var root = doc.RootElement;
                Assert.Equal(JsonValueKind.Array, root.GetProperty("explodeSymbolCandidates").ValueKind);
                Assert.Equal(0, root.GetProperty("explodeSymbolCandidates").GetArrayLength());
                Assert.True(root.TryGetProperty("version", out _));
                Assert.True(root.TryGetProperty("sheets", out _));
                Assert.NotEqual(JsonValueKind.Undefined, root.GetProperty("ambiguous").ValueKind);
                Assert.Contains(root.GetProperty("annotations").EnumerateArray(),
                    a => a.GetProperty("provenance").GetString() == "NativeExplode");
            }

            // ④ 审计 semantic-scan params：五键 + 旧键 annotationCount 仍在。
            using (var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db")))
            {
                var scan = audit.ReadAll().Single(r => r.Entry.Kind == "semantic-scan");
                using var p = JsonDocument.Parse(scan.Entry.ParamsJson);
                Assert.Equal(2, p.RootElement.GetProperty("explodeRead").GetInt32());
                Assert.Equal(1, p.RootElement.GetProperty("explodeFully").GetInt32());
                Assert.Equal(0, p.RootElement.GetProperty("explodePartial").GetInt32());
                Assert.Equal(1, p.RootElement.GetProperty("explodeFailed").GetInt32());
                Assert.Equal(0, p.RootElement.GetProperty("explodeSymbolCandidates").GetArrayLength());
                Assert.Equal(1, p.RootElement.GetProperty("annotationCount").GetInt32());
                Assert.True(audit.VerifyChain(out _));
            }
            Assert.Null(SingleExplodeReadFailed()); // 成功路径不记失败审计
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>R9：RuntimeFactory 抛 → exit 0 + stderr 降级提示 + 审计 explode-read-failed(error)
    /// + 五键 0/空 + 无证据行/无 NativeExplode 标注（理解侧诚实降级）。</summary>
    [Fact]
    public void ScanExplode_RuntimeFactoryThrows_AuditsExplodeReadFailed_ExitZero()
    {
        CommandRouter.RuntimeFactory = () => throw new CadRuntimeException("fake 构造失败（激活异常）");
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--explode");
            Assert.Equal(0, code); // 理解侧诚实降级：扫描照常完成
            Assert.Contains("fake 构造失败（激活异常）", stderr);
            Assert.Contains("理解侧降级", stderr);
            Assert.Equal((0, 0, 0, 0), ExplodeCounts(stdout));
            using (var doc = JsonDocument.Parse(stdout))
                Assert.Equal(0, doc.RootElement.GetProperty("explodeSymbolCandidates").GetArrayLength());

            var rec = SingleExplodeReadFailed();
            Assert.NotNull(rec);
            using (var p = JsonDocument.Parse(rec!.Entry.ParamsJson))
                Assert.Contains("fake 构造失败（激活异常）", p.RootElement.GetProperty("error").GetString());   // M5：审计键统一小写

            var overlay = SemanticOverlay.Load(OverlayPath);
            Assert.Empty(overlay.ExplodeEvidence);
            Assert.DoesNotContain(overlay.Annotations, a => a.Provenance == Provenance.NativeExplode);
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>--explode + --native 并存：两个块各自独立计数（native 1/1/0，explode 2/1/0/1）、
    /// 两类标注共存、审计 params 同持两组加性键。</summary>
    [Fact]
    public void ScanExplodeWithNative_BothCountsIndependent()
    {
        CommandRouter.RuntimeFactory = () => new FakeExplodeAndNativeRuntime();
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing, "--native", "--explode");
            Assert.Equal(0, code);
            using (var doc = JsonDocument.Parse(stdout))
            {
                var root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("nativeRead").GetInt32());
                Assert.Equal(1, root.GetProperty("nativeClassified").GetInt32());
                Assert.Equal(0, root.GetProperty("nativeReadFailed").GetInt32());
            }
            Assert.Equal((2, 1, 0, 1), ExplodeCounts(stdout));

            var overlay = SemanticOverlay.Load(OverlayPath);
            Assert.Single(overlay.NativeEntities);
            Assert.Equal(2, overlay.ExplodeEvidence.Count);
            Assert.Contains(overlay.Annotations, a => a.Provenance == Provenance.NativeParameter && a.Confidence == 0.95);
            Assert.Contains(overlay.Annotations, a => a.Provenance == Provenance.NativeExplode && a.Confidence == 0.9);

            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            var scan = audit.ReadAll().Single(r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(scan.Entry.ParamsJson);
            Assert.Equal(1, p.RootElement.GetProperty("nativeRead").GetInt32());
            Assert.Equal(2, p.RootElement.GetProperty("explodeRead").GetInt32());
            Assert.Equal(1, p.RootElement.GetProperty("explodeFailed").GetInt32());
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>符号块候选只披露不落标注：候选元素四键 {handle,blockName,kind,classHint}。</summary>
    [Fact]
    public void ScanExplode_DisclosesSymbolCandidates_WithoutAnnotation()
    {
        CommandRouter.RuntimeFactory = () => new FakeSymbolCandidateRuntime();
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing, "--explode");
            Assert.Equal(0, code);

            using (var doc = JsonDocument.Parse(stdout))
            {
                var candidates = doc.RootElement.GetProperty("explodeSymbolCandidates");
                Assert.Equal(1, candidates.GetArrayLength());
                var c = candidates[0];
                Assert.Equal("30B01", c.GetProperty("handle").GetString());
                Assert.Equal("$DorLib2D$测试门", c.GetProperty("blockName").GetString());
                Assert.Equal("door-window", c.GetProperty("kind").GetString());
                Assert.Equal("门窗库块（候选）", c.GetProperty("classHint").GetString());
            }
            // 候选不落标注（未实证类 → Unknown → 不种子）。
            Assert.DoesNotContain(SemanticOverlay.Load(OverlayPath).Annotations,
                a => a.Provenance == Provenance.NativeExplode);
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>R8 非 acad 厂商：CADAGENT_RUNTIME=zwcad（默认 RuntimeSelector 读环境变量）+
    /// RuntimeFactory=null → stderr 警示"--explode 需 CADAGENT_RUNTIME=acad"、五键全 0/空、
    /// 无证据行、不记失败审计（既定降级路径，不构造任何厂商运行时=不起进程）。</summary>
    [Fact]
    public void ScanExplode_NonAcadVendor_WarnsAndZeros()
    {
        Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", "zwcad");
        CommandRouter.RuntimeSelector = _originalSelector; // 默认环境变量读取器
        CommandRouter.RuntimeFactory = null;
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--explode");
            Assert.Equal(0, code);
            Assert.Contains("该运行时不支持深度读（--explode 需 CADAGENT_RUNTIME=acad）", stderr);
            Assert.Equal((0, 0, 0, 0), ExplodeCounts(stdout));
            Assert.Empty(SemanticOverlay.Load(OverlayPath).ExplodeEvidence);
            Assert.Null(SingleExplodeReadFailed()); // 厂商不支持=既定降级路径，不记失败审计
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
            Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", _originalEnv);
        }
    }

    /// <summary>无 --explode → 五键恒 0/空（加性契约）且不触 runtime（工厂置抛：触达即失败）。</summary>
    [Fact]
    public void ScanWithoutExplode_ZeroKeys_NeverTouchesRuntime()
    {
        CommandRouter.RuntimeFactory = () => throw new InvalidOperationException("不带 --explode 不得创建运行时");
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
            Assert.Equal(0, code);
            Assert.Equal((0, 0, 0, 0), ExplodeCounts(stdout));
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal(0, doc.RootElement.GetProperty("explodeSymbolCandidates").GetArrayLength());
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    // ---- I1（PR#30 合并前 ISTQB 终审）：--explode 桥可用性预检 ----

    /// <summary>
    /// I1：`semantic scan --explode` 的桥可用性预检**先于任何运行时构造**（纯只读、零实例、零 COM）。
    /// 三态（dll-missing / stub / untrusted）等价类均：不创建运行时、审计
    /// `explode-read-failed{reason,dllPath,guidance}`、stderr 指引、四计数 0、exit 0（理解侧诚实降级）。
    /// 修复前该路径会照旧构造运行时并深读——缺失/空桩得到伪失败，不受信目录让 NETLOAD 安全框阻塞至 3600s。
    /// </summary>
    [Theory]
    [InlineData("dll-missing")]
    [InlineData("stub")]
    [InlineData("untrusted")]
    public void ScanExplode_BridgeUnavailable_DegradesBeforeRuntime(string state)
    {
        bool created = false;
        CommandRouter.RuntimeFactory = () => { created = true; return new FakeExplodeRuntime(); };
        try
        {
            string dllPath;
            switch (state)
            {
                case "dll-missing":
                    dllPath = Path.Combine(_project, "missing", AcadRuntime.BridgeDllName);
                    AcadRuntime.BridgeDllOverride = dllPath;
                    break;
                case "stub":
                    dllPath = Path.Combine(_bridgeDir, "stub-" + AcadRuntime.BridgeDllName);
                    File.WriteAllBytes(dllPath, Encoding.ASCII.GetBytes("NoAcadRefsStub"));
                    AcadRuntime.BridgeDllOverride = dllPath;
                    break;
                default: // untrusted：桥存在且非空桩，但目录不在 TRUSTEDPATHS。
                    dllPath = Path.Combine(_bridgeDir, AcadRuntime.BridgeDllName);
                    AcadRuntime.TrustedPathsSource = _ => @"C:\not-trusted";
                    break;
            }

            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--explode");

            Assert.Equal(0, code);                 // 理解侧降级：扫描照常完成
            Assert.False(created);                 // 零运行时构造
            Assert.Contains("桥不可用", stderr);
            Assert.Contains(state, stderr);
            Assert.Equal((0, 0, 0, 0), ExplodeCounts(stdout));
            Assert.Empty(SemanticOverlay.Load(OverlayPath).ExplodeEvidence);
            var rec = SingleExplodeReadFailed();
            Assert.NotNull(rec);
            using var p = JsonDocument.Parse(rec!.Entry.ParamsJson);
            Assert.Equal(state, p.RootElement.GetProperty("reason").GetString());
            Assert.Equal(Path.GetFullPath(dllPath), p.RootElement.GetProperty("dllPath").GetString());
            Assert.False(string.IsNullOrWhiteSpace(p.RootElement.GetProperty("guidance").GetString()));
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>
    /// M8（PR#30 终审）：RuntimeFactory 返回 null（未配置运行时）时不得静默跳过——stderr 显式降级
    /// 提示，四计数 0、exit 0，且不记失败审计（属既定降级路径，不是读证据失败）。
    /// </summary>
    [Fact]
    public void ScanExplode_RuntimeFactoryReturnsNull_DegradesWithExplicitStderr()
    {
        CommandRouter.RuntimeFactory = () => null;
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--explode");

            Assert.Equal(0, code);
            Assert.Contains("未配置运行时（RuntimeFactory=null），--explode 跳过", stderr);
            Assert.Equal((0, 0, 0, 0), ExplodeCounts(stdout));
            Assert.Empty(SemanticOverlay.Load(OverlayPath).ExplodeEvidence);
            Assert.Null(SingleExplodeReadFailed());
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    // ---- B1（边界补齐）：块定义 TDb 独立段加性披露 ----

    /// <summary>
    /// B1：`--explode` 在证据之后读**块定义**表（<see cref="ICadRuntime.ReadExplodeBlockDefs"/>，
    /// 与 <see cref="ICadRuntime.ReadExplodeEvidence"/> 同一次桥会话的伴生读数）。断言：
    /// ① stdout 加性键 <c>blockDefTdb</c> 恒在且元素三键 {blockName,rxClass,count}；
    /// ② 审计 semantic-scan 加性 <c>blockDefTdbCount</c>；
    /// ③ overlay.BlockDefTdb 已持久化（独立表，不与 ExplodeEvidence 混同）；
    /// ④ **旧口径不变**：explode 四计数与既有 fake 等价类一致（证据列表未加行）。
    /// </summary>
    [Fact]
    public void ScanExplode_BlockDefTdb_SeparateSection_AdditiveKeys()
    {
        CommandRouter.RuntimeFactory = () => new FakeBlockDefRuntime();
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--explode");
            Assert.Equal(0, code);
            Assert.DoesNotContain("explode-read-failed", stderr);

            // ① stdout 加性键。
            using (var doc = JsonDocument.Parse(stdout))
            {
                var root = doc.RootElement;
                var defs = root.GetProperty("blockDefTdb");
                Assert.Equal(JsonValueKind.Array, defs.ValueKind);
                Assert.Equal(2, defs.GetArrayLength());
                var first = defs[0];
                Assert.Equal("$equip$00002832", first.GetProperty("blockName").GetString());
                Assert.Equal("TDbECabletry", first.GetProperty("rxClass").GetString());
                Assert.Equal(1364, first.GetProperty("count").GetInt32());
                // 旧键逐字在（加性纪律）。
                Assert.True(root.TryGetProperty("version", out _));
                Assert.True(root.TryGetProperty("explodeSymbolCandidates", out _));
            }

            // ④ 旧口径不变：1 行证据全炸开 → 1/1/0/0。
            Assert.Equal((1, 1, 0, 0), ExplodeCounts(stdout));

            // ③ overlay 独立表落盘。
            var overlay = SemanticOverlay.Load(OverlayPath);
            Assert.Equal(2, overlay.BlockDefTdb.Count);
            Assert.Single(overlay.ExplodeEvidence);

            // ② 审计加性键。
            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            var scan = audit.ReadAll().Single(r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(scan.Entry.ParamsJson);
            Assert.Equal(2, p.RootElement.GetProperty("blockDefTdbCount").GetInt32());
            Assert.Equal(1, p.RootElement.GetProperty("explodeRead").GetInt32());
            Assert.True(audit.VerifyChain(out _));
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>B1 加性恒在：无 --explode（或运行时未提供块定义读数）时 <c>blockDefTdb</c> 为空数组、
    /// 审计 <c>blockDefTdbCount</c>=0；overlay.BlockDefTdb 亦为空表（不复用上轮磁盘值）。</summary>
    [Fact]
    public void ScanWithoutExplode_BlockDefTdb_Empty()
    {
        CommandRouter.RuntimeFactory = () => throw new InvalidOperationException("不带 --explode 不得创建运行时");
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal(0, doc.RootElement.GetProperty("blockDefTdb").GetArrayLength());

            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            var scan = audit.ReadAll().Single(r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(scan.Entry.ParamsJson);
            Assert.Equal(0, p.RootElement.GetProperty("blockDefTdbCount").GetInt32());
            Assert.Empty(SemanticOverlay.Load(OverlayPath).BlockDefTdb);
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }
}
