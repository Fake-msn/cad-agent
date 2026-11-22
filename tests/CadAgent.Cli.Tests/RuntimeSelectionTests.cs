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

    public void Dispose()
    {
        CommandRouter.RuntimeSelector = _originalSelector;
        CommandRouter.RuntimeFactory = _originalFactory;
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
}
