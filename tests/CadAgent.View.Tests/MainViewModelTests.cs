using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.View.Tests;

/// <summary>
/// 主视图模型加载测试。项目目录按测试隔离（sidecar 落 <c>&lt;projectDir&gt;/.cadagent</c>，
/// 不再共享 %TEMP%/.cadagent——并发测试互踩/残留）。
/// </summary>
public class MainViewModelTests : IDisposable
{
    private readonly string _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), "cadagent-mainvm-" + Guid.NewGuid().ToString("N"));

    public MainViewModelTests() => Directory.CreateDirectory(_projectDir);

    public void Dispose()
    {
        File.Delete(_path);
        Directory.Delete(_projectDir, true);
    }

    [Fact]
    public void Load_ValidDrawing_TitleAndSession()
    {
        var vm = new ViewModels.MainViewModel(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(Path.GetFileName(_path), vm.Title);
        Assert.Single(vm.Session.Summarize().Entities);
    }

    [Fact]
    public void Load_MissingFile_ErrorMessage()
    {
        var vm = new ViewModels.MainViewModel("no-such.dxf", _projectDir);
        Assert.NotNull(vm.ErrorMessage);
    }

    // ---- T10：--skill 目录承载（Program 解析 → MainViewModel → MainWindow 接线） ----

    [Fact]
    public void SkillDirs_DefaultEmpty()
    {
        var vm = new ViewModels.MainViewModel(_path, _projectDir);
        Assert.Empty(vm.SkillDirs);
    }

    [Fact]
    public void SkillDirs_CarriedInOrder()
    {
        var vm = new ViewModels.MainViewModel(_path, _projectDir, new[] { "s1", "s2" });
        Assert.Equal(new[] { "s1", "s2" }, vm.SkillDirs);
    }

    // ---- 防假 Green 切片 T3：sidecar 接线（projectDir 已知，Load 恢复 census） ----

    [Fact]
    public void Load_WithSidecar_CensusRestored()
    {
        UnparsedCensusStore.Save(_projectDir, _path,
            new UnparsedCensus(new[] { new UnparsedClassBrief("TCH_WALL", 2) }));
        var vm = new ViewModels.MainViewModel(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Session.Unparsed.TotalCount); // sidecar census 恢复（显示不完整警示依据）
    }

    [Fact]
    public void Load_NoSidecar_CensusEmpty_WritesVerifiedEmptySidecar()
    {
        // 项3 T5（行为变更披露）：census 空 + 源存在 → 现在总是回写 sidecar（VerifiedEmpty=true，
        // 含源 size/mtime/hash）；此前空 census 不回写。二次打开同图免重读源 DWG（R9 语义扩展）。
        var vm = new ViewModels.MainViewModel(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(0, vm.Session.Unparsed.TotalCount); // 无 census 行为逐字保持
        Assert.True(File.Exists(Path.Combine(_projectDir, ".cadagent", "unparsed.json")));
        Assert.Equal(0, UnparsedCensusStore.LoadTrusted(_projectDir, _path, out bool verifiedEmpty).TotalCount);
        Assert.True(verifiedEmpty); // 已验证空（信任命中）
    }
}
