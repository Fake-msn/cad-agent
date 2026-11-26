using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 5：MainViewModel 异步加载（UI 不阻塞）。后台仅 Load；失败经构造既有 catch
/// 落入 ErrorMessage（不抛）；同步构造保留（既有测试/CLI 零改动）。
/// 项目目录按测试隔离（sidecar 落 <c>&lt;projectDir&gt;/.cadagent</c>，不共享 %TEMP%）。
/// </summary>
public class MainViewModelAsyncTests : IDisposable
{
    private readonly string _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), "mainvm-async-" + Guid.NewGuid().ToString("N"));

    public MainViewModelAsyncTests() => Directory.CreateDirectory(_projectDir);

    public void Dispose()
    {
        File.Delete(_path);
        Directory.Delete(_projectDir, true);
    }

    [Fact]
    public async Task LoadAsync_ValidDrawing_NoError_SessionReady()
    {
        var vm = await MainViewModel.LoadAsync(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(Path.GetFileName(_path), vm.Title);
        Assert.Single(vm.Session.Summarize().Entities);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ErrorMessage_NoThrow()
    {
        var vm = await MainViewModel.LoadAsync("no-such-file.dxf", _projectDir);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task LoadAsync_SkillDirs_PreservedInOrder()
    {
        var vm = await MainViewModel.LoadAsync(_path, _projectDir, new[] { "s1", "s2" });
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new[] { "s1", "s2" }, vm.SkillDirs);
    }
}
