using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 视觉接线 T3：semantic scan --vision → 独立 View 进程 headless 入口 relay。
/// Cli 不 using Avalonia（ViewLaunch 先例）：进程拉起经 VisionScanSpawn seam 可注入
/// （测试捕获 args/返回码；生产 Process dotnet ViewDll + 重定向转发）。
/// 无 --vision → legacy 在进程扫描逐字不变（seam 置抛异常钉住不得误入 relay）。
/// </summary>
public class SemanticScanVisionRelayTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"visionrelay-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public SemanticScanVisionRelayTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.VisionScanSpawn = null;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void SemanticScan_Vision_RelaySpawnSeam_ArgsForwarded()
    {
        string[]? captured = null;
        CommandRouter.VisionScanSpawn = a => { captured = a; return 0; };

        var (code, stdout, _) = Run("semantic", "scan", "--vision", "--drawing", _drawing, "--confirm-egress");

        Assert.Equal(0, code);
        Assert.NotNull(captured);
        // --vision-scan 头 + --drawing/--project/--confirm-egress 转发（顺序钉住）
        Assert.Equal(
            new[] { "--vision-scan", "--drawing", _drawing, "--project", _project, "--confirm-egress" },
            captured);
        Assert.Empty(stdout); // seam 模式不产在进程 stdout（relay 转发由生产进程路径负责）
    }

    [Fact]
    public void SemanticScan_Vision_NameForwarded()
    {
        string[]? captured = null;
        CommandRouter.VisionScanSpawn = a => { captured = a; return 0; };

        var (code, _, _) = Run("semantic", "scan", "--vision", "--drawing", _drawing, "--name", "prof1");

        Assert.Equal(0, code);
        Assert.NotNull(captured);
        Assert.Contains("--name", captured!);
        int i = Array.IndexOf(captured!, "--name");
        Assert.Equal("prof1", captured![i + 1]);
        Assert.DoesNotContain("--confirm-egress", captured!); // 未给定不转发
    }

    [Fact]
    public void SemanticScan_Vision_SeamExitCodeRelayed()
    {
        CommandRouter.VisionScanSpawn = _ => 3;
        var (code, _, _) = Run("semantic", "scan", "--vision", "--drawing", _drawing);
        Assert.Equal(3, code); // View 进程退出码透传
    }

    [Fact]
    public void SemanticScan_NoVision_LegacyInProcess()
    {
        // seam 置抛异常：无 --vision 绝不得走 relay；legacy scan stdout 契约保持
        CommandRouter.VisionScanSpawn = _ => throw new InvalidOperationException("legacy scan 不得经 relay seam");

        var (code, stdout, stderr) = Run("semantic", "scan", "--drawing", _drawing);

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        // legacy stdout 四键（version/annotations/ambiguous/sheets）逐字契约（既有 SemanticCommandTests 钉住全文）
        Assert.Contains("\"version\"", stdout);
        Assert.Contains("\"annotations\"", stdout);
        Assert.Contains("\"ambiguous\"", stdout);
        Assert.Contains("\"sheets\"", stdout);
        Assert.DoesNotContain("\"ok\"", stdout); // 非 vision-scan JSON 形状
    }

    [Fact]
    public void BuildVisionScanArgs_QuotesValuesNotFlags()
    {
        // 与 BuildViewArgs 同口径：dll/值加引号，flag 裸追加（arg-builder 单测，不真启动进程）
        string args = CommandRouter.BuildVisionScanArgs(@"C:\bin\CadAgent.View.dll",
            new[] { "--vision-scan", "--drawing", @"C:\d\f.dxf", "--project", @"C:\proj", "--confirm-egress" });
        Assert.Equal(
            "\"C:\\bin\\CadAgent.View.dll\" --vision-scan --drawing \"C:\\d\\f.dxf\" --project \"C:\\proj\" --confirm-egress",
            args);
    }
}
