using CadAgent.Report;

namespace CadAgent.Report.Tests;

public sealed class CjkFontRegistryTests
{
    [Fact]
    public void PickFamily_PrefersConfiguredPriorityOrder_OverOtherNames()
    {
        string? family = CjkFontRegistry.PickFamily(new[] { "Arial", "SimSun", "Microsoft YaHei" });

        Assert.Equal("Microsoft YaHei", family);
    }

    [Fact]
    public void PickFamily_MatchesCaseInsensitively()
    {
        Assert.Equal("SimHei", CjkFontRegistry.PickFamily(new[] { "simhei" }));
    }

    [Fact]
    public void PickFamily_NoCjkCandidate_ReturnsNull()
    {
        Assert.Null(CjkFontRegistry.PickFamily(new[] { "Arial", "Consolas" }));
        Assert.Null(CjkFontRegistry.PickFamily(Array.Empty<string>()));
    }

    [Fact]
    public void EnsureFamily_OnChineseWindows_ReturnsUsableFamily()
    {
        // 产品为 Windows 中文环境交付（程序集级 SupportedOSPlatform=windows）：
        // 无中文字体即无法出报告，测试机应具备（缺字体时此断言失败即环境不达标）。
        string family = CjkFontRegistry.EnsureFamily();

        Assert.False(string.IsNullOrWhiteSpace(family));
        Assert.Equal(family, CjkFontRegistry.EnsureFamily()); // 缓存幂等
    }
}
