using CadAgent.Data.Tests;

namespace CadAgent.View.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string _path = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_ValidDrawing_TitleAndSession()
    {
        var vm = new ViewModels.MainViewModel(_path, Path.GetTempPath());
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(Path.GetFileName(_path), vm.Title);
        Assert.Single(vm.Session.Summarize().Entities);
    }

    [Fact]
    public void Load_MissingFile_ErrorMessage()
    {
        var vm = new ViewModels.MainViewModel("no-such.dxf", Path.GetTempPath());
        Assert.NotNull(vm.ErrorMessage);
    }
}
