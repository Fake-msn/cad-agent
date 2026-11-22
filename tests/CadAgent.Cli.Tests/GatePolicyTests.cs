using CadAgent.Data;

namespace CadAgent.Cli.Tests;

public class GatePolicyTests
{
    private static GateReport Report(GateLevel level) =>
        new(level, Array.Empty<UnknownBrief>(), "");

    [Fact]
    public void GateRefusesRun_Green_False() =>
        Assert.False(CommandRouter.GateRefusesRun(Report(GateLevel.Green)));

    // T3 sanctioned 更新（原 Yellow_False 放行期望）：Yellow=有原生+未解析 → 只读审查，
    // 修改操作全图级拦截（未解析对象无句柄/几何，禁触区无法定位）——与 Red 同样拒绝。
    [Fact]
    public void GateRefusesRun_Yellow_True() =>
        Assert.True(CommandRouter.GateRefusesRun(Report(GateLevel.Yellow)));

    [Fact]
    public void GateRefusesRun_Red_True() =>
        Assert.True(CommandRouter.GateRefusesRun(Report(GateLevel.Red)));
}
