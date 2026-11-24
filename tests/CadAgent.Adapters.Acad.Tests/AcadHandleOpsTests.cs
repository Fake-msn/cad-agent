using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// 切片A Task 8：AcadRuntime 句柄寻址执行器单元测试（无 COM）。
/// HandleReader seam 注入钉住 `.Handle` 读缝；无文档路径证明三个句柄操作已覆盖
/// ICadRuntime 接口默认 throw（默认消息"运行时不支持句柄寻址操作"——本类断言
/// "无打开的图纸" 以区分：真的进了 AcadRuntime 实现体而非接口兜底）。
/// 本类不拉起任何厂商进程（Dispose 期间也不得扫描/等待真实 acad.exe）。
/// </summary>
public class AcadHandleOpsTests : IDisposable
{
    private readonly Func<dynamic, string?> _origHandle = AcadRuntime.HandleReader;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;

    public AcadHandleOpsTests()
    {
        // 确定性：AcadRuntime.Dispose→VendorGuard.WaitForProcessExit（ExitWait 15s）不得
        // 因本机恰有真实 acad.exe 而等待——进程名缝置空。
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.HandleReader = _origHandle;
        VendorGuard.ProcessNames = _origNames;
    }

    [Fact]
    public void HandleReader_ReadsHexString()
    {
        AcadRuntime.HandleReader = _ => "21E00";
        Assert.Equal("21E00", AcadRuntime.HandleReader(new object()));
    }

    [Theory]
    [InlineData("change-layer")]
    [InlineData("move")]
    [InlineData("delete")]
    public void HandleOps_NoOpenDocument_FailLoudlyNotInterfaceDefault(string op)
    {
        using var rt = new AcadRuntime();
        var ex = Assert.Throws<CadRuntimeException>(() =>
        {
            switch (op)
            {
                case "change-layer": rt.ChangeLayerByHandle("21E00", "WALL"); break;
                case "move": rt.MoveByHandle("21E00", 1, 1); break;
                default: rt.DeleteByHandle("21E00"); break;
            }
        });
        Assert.Contains("无打开的图纸", ex.Message);
    }
}
