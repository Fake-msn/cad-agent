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

    // ---- 终审 Important 2：Build + Sheets 同处后台任务（MainWindow 不再 UI 线程重建） ----

    [Fact]
    public async Task LoadAsync_PrebuiltItemsAndSheets_NonEmpty()
    {
        var vm = await MainViewModel.LoadAsync(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.Items);
        Assert.NotEmpty(vm.Sheets);
        Assert.True(vm.BuildCompleted);
    }

    [Fact]
    public void LoadAsync_HeavyBuild_RunsOffDedicatedCallingThread()
    {
        // 调用线程用独立前台线程（非线程池）→ LoadAsync 的 Task.Run 必落线程池，
        // 消除「xUnit 本身在线程池上，ThreadId 可能复用」的时序歧义。
        int callerThread = 0;
        MainViewModel? vm = null;
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                callerThread = Environment.CurrentManagedThreadId;
                vm = MainViewModel.LoadAsync(_path, _projectDir).GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        });
        caller.Start();
        caller.Join();

        Assert.Null(failure);
        Assert.NotNull(vm);
        Assert.Null(vm!.ErrorMessage);
        Assert.True(vm.BuildThreadId > 0);
        Assert.NotEqual(callerThread, vm.BuildThreadId);
        Assert.True(vm.BuildCompleted);
        Assert.NotEmpty(vm.Items);
    }

    [Fact]
    public void SyncConstructor_AlsoBuildsItemsAndSheets()
    {
        // 同步构造保留且语义一致（既有测试/CLI/MainWindow 直接 new 的场景）。
        var vm = new MainViewModel(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(Environment.CurrentManagedThreadId, vm.BuildThreadId);
        Assert.NotEmpty(vm.Items);
        Assert.NotEmpty(vm.Sheets);
    }

    // ---- 终审 Important 1：sidecar 不可写不得阻断打开（只读项目） ----

    [Fact]
    public async Task LoadAsync_UnwritableSidecarTarget_StillLoads()
    {
        // <project>/.cadagent/unparsed.json 被同名目录占位 → Save 抛 IOException（尽力写吞掉）。
        string sidecar = Path.Combine(_projectDir, ".cadagent", "unparsed.json");
        Directory.CreateDirectory(sidecar);
        var vm = await MainViewModel.LoadAsync(_path, _projectDir);
        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.Session.Summarize().Entities);
        Assert.NotEmpty(vm.Items);
    }
}
