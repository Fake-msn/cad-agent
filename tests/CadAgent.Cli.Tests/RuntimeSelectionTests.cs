using CadAgent.Core.Cad;

namespace CadAgent.Cli.Tests;

/// <summary>
/// CLI 运行时选择（CADAGENT_RUNTIME）：纯工厂+名称校验单元测试，不触 COM。
/// 等价类：默认/显式 zwcad/acad/大小写空白/未知值硬错误。
/// </summary>
public class RuntimeSelectionTests : IDisposable
{
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;

    public void Dispose() => CommandRouter.RuntimeSelector = _originalSelector;

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
}
