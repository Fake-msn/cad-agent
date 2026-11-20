using CadAgent.Data;

namespace CadAgent.Cli.Tests;

public class GatePolicyTests
{
    private static GateReport Report(GateLevel level) =>
        new(level, Array.Empty<UnknownBrief>(), "");

    [Fact]
    public void GateRefusesRun_Green_False() =>
        Assert.False(CommandRouter.GateRefusesRun(Report(GateLevel.Green)));

    [Fact]
    public void GateRefusesRun_Yellow_False() =>
        Assert.False(CommandRouter.GateRefusesRun(Report(GateLevel.Yellow)));

    [Fact]
    public void GateRefusesRun_Red_True() =>
        Assert.True(CommandRouter.GateRefusesRun(Report(GateLevel.Red)));
}