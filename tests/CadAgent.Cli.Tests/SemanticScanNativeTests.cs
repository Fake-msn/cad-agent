using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;
using Xunit;

namespace CadAgent.Cli.Tests;

/// <summary>
/// CLI `semantic scan --native`（切片A T6）：COM 原生读参落 overlay（NativeEntities 独立表 +
/// NativeParameter 0.95 标注）+ stdout/审计加性三键 nativeRead/nativeClassified/nativeReadFailed。
/// 控制器裁定（覆盖 brief 草图，逐条实现）：
/// R8 —— --native 不得走 CreateRuntimeIfAvailable（其 GetInfo()→EnsureApp() 预激活缓存默认
/// profile 的 _app，ReadNativeParams 内 EnsureAppTArch 会静默复用该缓存、永不加载 TArch →
/// nativeRead=0）。测试注入 RuntimeFactory 优先；否则 ResolveRuntimeName()!=acad → stderr
/// 警示"--native 需 CADAGENT_RUNTIME=acad"并跳过（计数全 0）；acad → 直接 CreateRuntimeInstance("acad")。
/// R9 —— 构造/读参任何异常 → 审计 native-read-failed（同一 store/session/op，params{error}）+
/// stderr 含上下文警示，计数保持 0，扫描继续到正常 stdout/审计/exit 0（理解侧诚实降级）。
/// 顺序 —— Seed 必须在 SemanticScanner.Scan（末尾按存活句柄剪枝）之后，否则标注被当陈旧清除。
/// 全用 fake runtime（绝不起真厂商进程）；静态 seam/环境变量用完即还原（本程序集已禁并行）。
/// </summary>
public class SemanticScanNativeTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"semscan-native-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly Func<ICadRuntime?>? _originalFactory = CommandRouter.RuntimeFactory;
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;
    private readonly string? _originalEnv = Environment.GetEnvironmentVariable("CADAGENT_RUNTIME");

    public SemanticScanNativeTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-NATIVETEST"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0),
                EndPoint = (CSMath.XYZ)new CSMath.XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "W-NATIVETEST")
            });
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeFactory = _originalFactory;
        CommandRouter.RuntimeSelector = _originalSelector;
        Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", _originalEnv);
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

    /// <summary>brief 逐字 fake runtime：一条已分类 TDbWall（handle 21E00，ReadOk=true）。</summary>
    private sealed class FakeRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) => new[]
        {
            new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
                new Dictionary<string, string> { ["LeftWidth"] = "100" },
                new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, true, "0dt")
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

    /// <summary>R9 fake：构造成功但 ReadNativeParams 抛（读参失败路径），跟踪 Dispose。</summary>
    private sealed class ReadNativeThrowsRuntime : ICadRuntime
    {
        public bool Disposed { get; private set; }
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) =>
            throw new CadRuntimeException("fake 读参失败（COM 忙）");
        public void Open(string p) { } public void SaveAs(string p) { }
        public void Close(bool s) { } public void Restart() { }
        public void AddLine(double a, double b, double c, double d, string? l = null) { }
        public void AddText(double x, double y, string t, double h, string? l = null) { }
        public void AddCircle(double x, double y, double r, string? l = null) { }
        public void AddInsert(string n, double x, double y, double s = 1, double r = 0, string? l = null) { }
        public void EnsureBlock(string n, IReadOnlyList<BlockPrimitive> p) { }
        public int EntityCount() => 0;
        public void Dispose() => Disposed = true;
    }

    /// <summary>Minor 5 fake：一条已分类读参成功（TDbWall/ReadOk=true）+ 一条 ReadOk=false
    /// （ObjectName 可分类=Wall 但读参不完整）——考核 nativeReadFailed&gt;0 的等价类与标注分离。</summary>
    private sealed class FakeMixedRuntime : ICadRuntime
    {
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) => new[]
        {
            new NativeEntityParam("21E00", "TDbWall", SemanticClass.Wall,
                new Dictionary<string, string> { ["LeftWidth"] = "100" },
                new[] { 0.0, 0, 0, 1, 1, 1 }, DateTimeOffset.UtcNow, true, "0dt"),
            new NativeEntityParam("22A00", "TDbWall", SemanticClass.Wall,
                new Dictionary<string, string>(), null, DateTimeOffset.UtcNow, false, null)
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

    private static (int read, int classified, int failed) NativeCounts(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        return (root.GetProperty("nativeRead").GetInt32(),
                root.GetProperty("nativeClassified").GetInt32(),
                root.GetProperty("nativeReadFailed").GetInt32());
    }

    private AuditRecord? SingleNativeReadFailed()
    {
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var all = audit.ReadAll();
        var rec = all.SingleOrDefault(r => r.Entry.Kind == "native-read-failed");
        if (rec is not null)
        {
            Assert.Equal("semantic", rec.Entry.Source);
            Assert.Equal(_drawing, rec.Entry.Target);
            // R9：同一审计 store/session/操作人（扫描照常留痕，失败条目与之同会话）。
            var scan = all.Single(r => r.Entry.Kind == "semantic-scan");
            Assert.Equal(scan.Entry.Session, rec.Entry.Session);
            Assert.Equal(scan.Entry.Operator, rec.Entry.Operator);
            Assert.Equal(scan.Entry.Role, rec.Entry.Role);
            Assert.True(audit.VerifyChain(out _));
        }
        return rec;
    }

    /// <summary>
    /// brief 主用例：--native + fake runtime → overlay 落 NativeEntities(21E00) +
    /// NativeParameter(Wall 0.95) 标注；stdout 三键 1/1/0 且旧键 version/annotations/ambiguous 在；
    /// 审计 semantic-scan params 加性三键（旧键 annotationCount 仍在）。
    /// </summary>
    [Fact]
    public void ScanNative_SeedsOverlay_AndReportsCounts()
    {
        CommandRouter.RuntimeFactory = () => new FakeRuntime();
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--native");
            Assert.Equal(0, code);
            Assert.DoesNotContain("CADAGENT_RUNTIME=acad", stderr); // 注入工厂路径不触发厂商警示

            // ① overlay 落盘（结束进程后从磁盘重载）：NativeEntities 含 21E00；标注含 NativeParameter(Wall)。
            var overlay = SemanticOverlay.Load(OverlayPath);
            var native = Assert.Single(overlay.NativeEntities);
            Assert.Equal("21E00", native.Handle);
            Assert.Equal("TDbWall", native.ObjectName);
            Assert.Equal(SemanticClass.Wall, native.Class);
            Assert.True(native.ReadOk);
            var annotation = Assert.Single(overlay.Annotations, a => a.Provenance == Provenance.NativeParameter);
            Assert.Equal("21E00", annotation.TargetHandle);
            Assert.Equal(SemanticClass.Wall, annotation.Class);
            Assert.Equal(0.95, annotation.Confidence);
            Assert.Equal("COM 原生读参: TDbWall", annotation.Evidence);

            // ② stdout 加性三键：1/1/0。
            Assert.Equal((1, 1, 0), NativeCounts(stdout));
            // ③ 旧键逐字兼容（存在性）+ 标注同步出现在 stdout annotations。
            using (var doc = JsonDocument.Parse(stdout))
            {
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("version", out _));
                Assert.True(root.TryGetProperty("ambiguous", out var ambiguous));
                Assert.NotEqual(JsonValueKind.Undefined, ambiguous.ValueKind);
                var annotations = root.GetProperty("annotations");
                Assert.Contains(annotations.EnumerateArray(),
                    a => a.GetProperty("provenance").GetString() == "NativeParameter");
            }

            // 审计加性（semantic-scan params）：三键 1/1/0 + 旧键 annotationCount 仍在。
            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            var scan = audit.ReadAll().Single(r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(scan.Entry.ParamsJson);
            Assert.Equal(1, p.RootElement.GetProperty("nativeRead").GetInt32());
            Assert.Equal(1, p.RootElement.GetProperty("nativeClassified").GetInt32());
            Assert.Equal(0, p.RootElement.GetProperty("nativeReadFailed").GetInt32());
            Assert.Equal(1, p.RootElement.GetProperty("annotationCount").GetInt32());
            Assert.True(audit.VerifyChain(out _));
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>
    /// R8 非 acad 厂商：CADAGENT_RUNTIME=zwcad（默认 RuntimeSelector 读环境变量）+ RuntimeFactory=null
    /// → stderr 警示"--native 需 CADAGENT_RUNTIME=acad"（finding 10：明示可配置通道）、计数全 0、
    /// 无 NativeEntities（不构造任何厂商运行时=不起进程）。
    /// </summary>
    [Fact]
    public void ScanNative_NonAcadVendor_WarnsAndZeros()
    {
        Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", "zwcad");
        CommandRouter.RuntimeSelector = _originalSelector; // 默认环境变量读取器
        CommandRouter.RuntimeFactory = null;
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--native");
            Assert.Equal(0, code);
            Assert.Contains("该运行时不支持原生读参（--native 需 CADAGENT_RUNTIME=acad）", stderr);
            Assert.Equal((0, 0, 0), NativeCounts(stdout));
            Assert.Empty(SemanticOverlay.Load(OverlayPath).NativeEntities);
            Assert.Null(SingleNativeReadFailed()); // 厂商不支持=既定降级路径，不记失败审计
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
            Environment.SetEnvironmentVariable("CADAGENT_RUNTIME", _originalEnv);
        }
    }

    /// <summary>R9：ReadNativeParams 抛 → exit 0 + 审计 native-read-failed(params.error) + 三键 0 + Dispose。</summary>
    [Fact]
    public void ScanNative_ReadThrows_AuditsNativeReadFailed_ExitZero()
    {
        var fake = new ReadNativeThrowsRuntime();
        CommandRouter.RuntimeFactory = () => fake;
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--native");
            Assert.Equal(0, code); // 理解侧诚实降级：扫描照常完成
            Assert.Contains("fake 读参失败（COM 忙）", stderr);
            Assert.Equal((0, 0, 0), NativeCounts(stdout));
            Assert.True(fake.Disposed);
            var rec = SingleNativeReadFailed();
            Assert.NotNull(rec);
            using var p = JsonDocument.Parse(rec!.Entry.ParamsJson);
            Assert.Contains("fake 读参失败（COM 忙）", p.RootElement.GetProperty("error").GetString());
            Assert.Empty(SemanticOverlay.Load(OverlayPath).NativeEntities);
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>R9：构造（RuntimeFactory）抛 → 同样 native-read-failed 审计 + 三键 0 + exit 0。</summary>
    [Fact]
    public void ScanNative_ConstructionThrows_AuditsNativeReadFailed_ExitZero()
    {
        CommandRouter.RuntimeFactory = () => throw new CadRuntimeException("fake 构造失败（激活异常）");
        try
        {
            var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--native");
            Assert.Equal(0, code);
            Assert.Contains("fake 构造失败（激活异常）", stderr);
            Assert.Equal((0, 0, 0), NativeCounts(stdout));
            var rec = SingleNativeReadFailed();
            Assert.NotNull(rec);
            using var p = JsonDocument.Parse(rec!.Entry.ParamsJson);
            Assert.Contains("fake 构造失败（激活异常）", p.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>Minor 5：nativeReadFailed&gt;0 等价类——2 条读参（1 条 ReadOk/1 条 ReadOk=false）
    /// → 三键 2/1/1；NativeEntities 含两句柄；NativeParameter 标注仅给 ReadOk 成功实体。</summary>
    [Fact]
    public void ScanNative_OneReadOkFalse_CountsSplit_AnnotationOnlyForGood()
    {
        CommandRouter.RuntimeFactory = () => new FakeMixedRuntime();
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing, "--native");
            Assert.Equal(0, code);
            Assert.Equal((2, 1, 1), NativeCounts(stdout));

            var overlay = SemanticOverlay.Load(OverlayPath);
            Assert.Equal(2, overlay.NativeEntities.Count);
            Assert.Contains(overlay.NativeEntities, n => n.Handle == "21E00" && n.ReadOk);
            Assert.Contains(overlay.NativeEntities, n => n.Handle == "22A00" && !n.ReadOk);
            var annotation = Assert.Single(overlay.Annotations, a => a.Provenance == Provenance.NativeParameter);
            Assert.Equal("21E00", annotation.TargetHandle);

            // 审计加性三键同口径：2/1/1。
            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            var scan = audit.ReadAll().Single(r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(scan.Entry.ParamsJson);
            Assert.Equal(2, p.RootElement.GetProperty("nativeRead").GetInt32());
            Assert.Equal(1, p.RootElement.GetProperty("nativeClassified").GetInt32());
            Assert.Equal(1, p.RootElement.GetProperty("nativeReadFailed").GetInt32());
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }

    /// <summary>无 --native → 三键恒 0（加性契约）且不触 runtime（工厂置抛：触达即失败）。</summary>
    [Fact]
    public void ScanWithoutNative_ZeroKeys_NeverTouchesRuntime()
    {
        CommandRouter.RuntimeFactory = () => throw new InvalidOperationException("不带 --native 不得创建运行时");
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
            Assert.Equal(0, code);
            Assert.Equal((0, 0, 0), NativeCounts(stdout));
        }
        finally
        {
            CommandRouter.RuntimeFactory = _originalFactory;
        }
    }
}
