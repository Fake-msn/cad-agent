using System.Runtime.InteropServices;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// CLI 运行时选择（CADAGENT_RUNTIME）：纯工厂+名称校验单元测试，不触 COM；
/// 另含硬错误边界集成测试（真实 export 路径，不触 COM——未知名称在激活前即硬失败）。
/// 等价类：默认/显式 zwcad/acad/大小写空白/未知值硬错误。
/// 静态 seam（RuntimeSelector/RuntimeFactory）用完即还原（本程序集已禁并行，见 AssemblyInfo）。
/// </summary>
public class RuntimeSelectionTests : IDisposable
{
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;
    private readonly Func<ICadRuntime?>? _originalFactory = CommandRouter.RuntimeFactory;
    private readonly Func<string, ICadRuntime> _originalCreateInstance = CommandRouter.CreateRuntimeInstance;

    public void Dispose()
    {
        CommandRouter.RuntimeSelector = _originalSelector;
        CommandRouter.RuntimeFactory = _originalFactory;
        CommandRouter.CreateRuntimeInstance = _originalCreateInstance;
    }

    [Fact]
    public void ResolveRuntimeName_Default_Zwcad()
    {
        CommandRouter.RuntimeSelector = () => null;
        Assert.Equal("zwcad", CommandRouter.ResolveRuntimeName());
    }

    [Theory]
    [InlineData("acad", "acad")]
    [InlineData("ACAD", "acad")]
    [InlineData(" Acad ", "acad")]
    [InlineData("zwcad", "zwcad")]
    [InlineData("ZWCAD", "zwcad")]
    public void ResolveRuntimeName_Normalizes(string input, string expected)
    {
        CommandRouter.RuntimeSelector = () => input;
        Assert.Equal(expected, CommandRouter.ResolveRuntimeName());
    }

    [Fact]
    public void ResolveRuntimeName_Unknown_ThrowsWithUsage()
    {
        CommandRouter.RuntimeSelector = () => "solidworks";
        var ex = Assert.Throws<CadRuntimeException>(() => CommandRouter.ResolveRuntimeName());
        Assert.Contains("CADAGENT_RUNTIME", ex.Message);
        Assert.Contains("zwcad", ex.Message);
        Assert.Contains("acad", ex.Message);
    }

    [Fact]
    public void CreateRuntimeInstance_Zwcad_ReturnsZwcadRuntime()
    {
        using var rt = CommandRouter.CreateRuntimeInstance("zwcad");
        Assert.IsType<CadAgent.Adapters.Zwcad.ZwcadRuntime>(rt);
    }

    [Fact]
    public void CreateRuntimeInstance_Acad_ReturnsAcadRuntime()
    {
        using var rt = CommandRouter.CreateRuntimeInstance("acad");
        Assert.IsType<CadAgent.Adapters.Acad.AcadRuntime>(rt);
    }

    /// <summary>
    /// 硬错误边界集成测试（spec §3 无旁路硬边界）：走 CommandRouter 真实 export 路径，
    /// 未知 CADAGENT_RUNTIME 须 exit 1 + stderr 含 CADAGENT_RUNTIME，不降级、无产物、
    /// 审计无 export-degraded。锁住"ResolveRuntimeName 在 CreateRuntimeIfAvailable 的
    /// try 之外"的不变量——将来挪进 try（被当初始化失败降级）此测试会红。
    /// </summary>
    [Fact]
    public void Export_UnknownRuntime_HardError_NoDegradeNoArtifacts()
    {
        CommandRouter.RuntimeSelector = () => "bogus";
        CommandRouter.RuntimeFactory = null; // 走生产路径（CreateRuntimeIfAvailable），非注入工厂
        string project = Path.Combine(Path.GetTempPath(), $"runtime-harderr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(project);
        string drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        try
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            int code = CommandRouter.Run(
                new[] { "export", "--drawing", drawing, "--out", Path.Combine(project, "out.dwg"), "--project", project },
                outW, errW);
            Assert.Equal(1, code);
            Assert.Contains("CADAGENT_RUNTIME", errW.ToString());
            // 输出目录（含子目录）不得有任何 CAD 产物——DXF 降级也算旁路。
            Assert.Empty(Directory.GetFiles(project, "*.dxf", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(project, "*.dwg", SearchOption.AllDirectories));
            // intake 审计先行已建库；硬错误不得留下 export-degraded 条目。
            using var audit = new SqliteAuditStore(Path.Combine(project, ".cadagent", "audit.db"));
            Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "export-degraded");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(project, true);
            File.Delete(drawing);
        }
        // seam 还原由 Dispose 兜底（_originalSelector/_originalFactory）。
    }

    /// <summary>
    /// 假 runtime（PR#18 修复波 Important#2）：构造（激活）成功但 GetInfo 抛 COMException
    /// （模拟 Visible/Version 读取 COM 失败）；跟踪 Dispose 调用。其余成员不应被触达。
    /// </summary>
    private sealed class GetInfoThrowsRuntime : ICadRuntime
    {
        public bool Disposed { get; private set; }
        public RuntimeInfo GetInfo() => throw new COMException("Visible/Version 读取失败 (0x8000FFFF)");
        public void Dispose() => Disposed = true;
        public void Open(string drawingPath) => throw new NotSupportedException();
        public void SaveAs(string outputPath) => throw new NotSupportedException();
        public void Close(bool save) => throw new NotSupportedException();
        public void Restart() => throw new NotSupportedException();
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => throw new NotSupportedException();
        public void AddText(double x, double y, string text, double height, string? layer = null) => throw new NotSupportedException();
        public void AddCircle(double x, double y, double radius, string? layer = null) => throw new NotSupportedException();
        public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => throw new NotSupportedException();
        public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) => throw new NotSupportedException();
        public int EntityCount() => throw new NotSupportedException();
    }

    /// <summary>
    /// PR#18 修复波 Important#2 回归钉住：CreateRuntimeIfAvailable 激活成功后 GetInfo 抛，
    /// catch 必须 Dispose 已构造的 rt（否则孤儿 ZWCAD 进程=CUIX 竞态源，违反守卫自身不变量），
    /// 且写警示 + 返回 null 走 DXF 降级（审计 export-degraded 佐证）。
    /// </summary>
    [Fact]
    public void Export_GetInfoThrows_DisposesRuntimeAndDegrades()
    {
        var fake = new GetInfoThrowsRuntime();
        CommandRouter.RuntimeSelector = () => "zwcad";
        CommandRouter.RuntimeFactory = null; // 走生产路径（CreateRuntimeIfAvailable）
        CommandRouter.CreateRuntimeInstance = _ => fake;
        string project = Path.Combine(Path.GetTempPath(), $"runtime-getinfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(project);
        string drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        try
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            int code = CommandRouter.Run(
                new[] { "export", "--drawing", drawing, "--out", Path.Combine(project, "out.dwg"), "--project", project },
                outW, errW);
            Assert.True(fake.Disposed, "GetInfo 抛后 rt 必须被 Dispose（孤儿进程=CUIX 竞态源）");
            Assert.Contains("厂商运行时初始化失败", errW.ToString());
            Assert.Equal(0, code); // 降级 DXF 交付成功
            // 审计 export-degraded 佐证 CreateRuntimeIfAvailable 返回 null（降级路径）。
            using var audit = new SqliteAuditStore(Path.Combine(project, ".cadagent", "audit.db"));
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-degraded");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(project, true);
            File.Delete(drawing);
        }
    }
}
