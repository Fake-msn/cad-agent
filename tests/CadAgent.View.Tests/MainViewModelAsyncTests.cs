using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 Task 5：MainViewModel 异步加载（UI 不阻塞）。后台 Load+Build+Sheets；
/// 失败经构造既有 catch 落入 ErrorMessage（不抛）；同步构造保留（既有测试/CLI 零改动）。
/// </summary>
public class MainViewModelAsyncTests : IDisposable
{
    private readonly string _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task LoadAsync_ValidDrawing_NoError_SessionReady()
    {
        var vm = await MainViewModel.LoadAsync(_path, Path.GetTempPath());
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(Path.GetFileName(_path), vm.Title);
        Assert.Single(vm.Session.Summarize().Entities);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ErrorMessage_NoThrow()
    {
        var vm = await MainViewModel.LoadAsync("no-such-file.dxf", Path.GetTempPath());
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task LoadAsync_SkillDirs_PreservedInOrder()
    {
        var vm = await MainViewModel.LoadAsync(_path, Path.GetTempPath(), new[] { "s1", "s2" });
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new[] { "s1", "s2" }, vm.SkillDirs);
    }
}
