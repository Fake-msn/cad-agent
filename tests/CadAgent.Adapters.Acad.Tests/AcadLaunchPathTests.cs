using CadAgent.Adapters.Acad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 切片A T10 真机核定收紧：/p 启动路径解析（无真机/无 COM、不拉起任何进程）。
/// 根因实证（2026-11-25）：本机 Workstation 安装**不写 PATH**，裸 Process.Start("acad.exe")
/// 抛 Win32Exception「系统找不到指定的文件」——真机首跑可复现（证据 outputs/slice-a-t10/）。
/// 解析链：注册表 AcadLocation（HKLM/HKCU R*\ACAD-*）→ %ProgramFiles%\Autodesk\AutoCAD *
/// 目录扫描 → 兜底裸 "acad.exe"（PATH 可解析环境保持旧行为）。
/// </summary>
public class AcadLaunchPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"acadpath-{Guid.NewGuid():N}");

    public AcadLaunchPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录 best-effort 清理 */ }
    }

    private string MakeInstallDir(string name, bool withExe)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (withExe) File.WriteAllText(Path.Combine(dir, "acad.exe"), "");
        return dir;
    }

    [Fact]
    public void Resolve_RegistryLocationWithExe_WinsOverInstallDirScan()
    {
        string registry = MakeInstallDir("reg", withExe: true);
        string scan = MakeInstallDir("scan", withExe: true);
        Assert.Equal(Path.Combine(registry, "acad.exe"),
            AcadRuntime.ResolveAcadExecutable(new[] { registry }, new[] { scan }));
    }

    [Fact]
    public void Resolve_RegistryLocationWithoutExe_FallsBackToInstallDirScan()
    {
        string registry = MakeInstallDir("reg-missing", withExe: false);
        string scan = MakeInstallDir("scan-hit", withExe: true);
        Assert.Equal(Path.Combine(scan, "acad.exe"),
            AcadRuntime.ResolveAcadExecutable(new[] { registry }, new[] { scan }));
    }

    [Fact]
    public void Resolve_NothingFound_ReturnsNull_LauncherFallsBackToBareName()
    {
        Assert.Null(AcadRuntime.ResolveAcadExecutable(
            new[] { MakeInstallDir("n1", withExe: false) }, new[] { MakeInstallDir("n2", withExe: false) }));
    }

    [Fact]
    public void Resolve_BlankCandidates_SkippedWithoutThrow()
    {
        Assert.Null(AcadRuntime.ResolveAcadExecutable(new[] { "", "   " }, new[] { "" }));
    }

    [Fact]
    public void BuildStartInfo_PinnedFileNameArgumentsAndShellExecute()
    {
        var psi = AcadRuntime.BuildStartInfo("TElec20V5", @"C:\Program Files\Autodesk\AutoCAD 2014\acad.exe");
        Assert.Equal(@"C:\Program Files\Autodesk\AutoCAD 2014\acad.exe", psi.FileName);
        Assert.Equal("/p \"TElec20V5\"", psi.Arguments);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void VersionRank_NumericOrderNotOrdinal()
    {
        // 序数排序会把 "R9.1" 排在 "R19.1" 之后——版本键取首个数字串，19 > 9。
        Assert.True(AcadRuntime.VersionRank("R19.1") > AcadRuntime.VersionRank("R9.1"));
        Assert.Equal(2014, AcadRuntime.VersionRank("AutoCAD 2014"));
        Assert.Equal(0, AcadRuntime.VersionRank("AutoCAD"));
    }
}
