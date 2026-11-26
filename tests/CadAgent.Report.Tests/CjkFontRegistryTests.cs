using CadAgent.Report;

namespace CadAgent.Report.Tests;

/// <summary>
/// 串行集合：本组用例改写 CjkFontRegistry 的静态 seam 与族名缓存，必须与其他（并行）
/// 集合互斥，否则静态状态互相污染导致偶发假绿/假红。
/// </summary>
[CollectionDefinition(ReportStaticStateCollection.Name, DisableParallelization = true)]
public sealed class ReportStaticStateCollection
{
    public const string Name = "ReportStaticState";
}

[Collection(ReportStaticStateCollection.Name)]
public sealed class CjkFontRegistryTests
{
    private const string FontPathEnv = "CADAGENT_REPORT_FONT_PATH";

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

    /// <summary>
    /// 加固口径：无 env 断言时不得用「首个已注册族」兜底——否则拉丁字体冒充中文字体，
    /// 报告静默渲染成 tofu，违背类文档/规格的「不产缺字降级」。
    /// </summary>
    [Fact]
    public void PickFamilyOrEnvFallback_WithoutEnv_RejectsForeignFamily()
    {
        Assert.Null(CjkFontRegistry.PickFamilyOrEnvFallback(new[] { "Arial" }, envProvided: false));
        Assert.Null(CjkFontRegistry.PickFamilyOrEnvFallback(Array.Empty<string>(), envProvided: false));
    }

    [Fact]
    public void PickFamilyOrEnvFallback_WithEnv_AcceptsFirstRegisteredFamily()
    {
        Assert.Equal("MyCjkFont",
            CjkFontRegistry.PickFamilyOrEnvFallback(new[] { " ", "MyCjkFont", "Other" }, envProvided: true));
        // 无已注册族时 env 也不凭空造出族名
        Assert.Null(CjkFontRegistry.PickFamilyOrEnvFallback(Array.Empty<string>(), envProvided: true));
    }

    [Fact]
    public void PickFamilyOrEnvFallback_PrefersCjkFamily_EvenWithEnv()
    {
        Assert.Equal("SimSun",
            CjkFontRegistry.PickFamilyOrEnvFallback(new[] { "MyCjkFont", "SimSun" }, envProvided: true));
    }

    [Fact]
    public void EnsureFamily_NoFontsAvailable_ThrowsWithGuidance()
    {
        Func<IEnumerable<string>> originalSystem = CjkFontRegistry.SystemFamilyNames;
        Func<IEnumerable<string>> originalFiles = CjkFontRegistry.FontFileCandidates;
        Func<IEnumerable<string>> originalRegistered = CjkFontRegistry.RegisteredFamilyNames;
        string? originalEnv = Environment.GetEnvironmentVariable(FontPathEnv);
        try
        {
            Environment.SetEnvironmentVariable(FontPathEnv, null);
            CjkFontRegistry.ResetForTests(); // 其他用例可能已缓存族名，先清空才能走到解析路径
            CjkFontRegistry.SystemFamilyNames = () => Array.Empty<string>();
            CjkFontRegistry.FontFileCandidates = () => Array.Empty<string>();
            // QuestPDF 注册是进程级全局状态：其他用例注册过的字体仍可见，必须注入空集合
            CjkFontRegistry.RegisteredFamilyNames = () => Array.Empty<string>();

            var ex = Assert.Throws<ReviewReportException>(() => CjkFontRegistry.EnsureFamily());

            Assert.Contains("中文字体", ex.Message);
            Assert.Contains(FontPathEnv, ex.Message);
        }
        finally
        {
            CjkFontRegistry.SystemFamilyNames = originalSystem;
            CjkFontRegistry.FontFileCandidates = originalFiles;
            CjkFontRegistry.RegisteredFamilyNames = originalRegistered;
            Environment.SetEnvironmentVariable(FontPathEnv, originalEnv);
            CjkFontRegistry.ResetForTests();
        }
    }

    [Fact]
    public void EnsureFamily_OnChineseWindows_ReturnsUsableFamily()
    {
        // 产品为 Windows 中文环境交付（程序集级 SupportedOSPlatform=windows）：
        // 无中文字体即无法出报告，测试机应具备（缺字体时此断言失败即环境不达标）。
        CjkFontRegistry.ResetForTests();
        string family = CjkFontRegistry.EnsureFamily();

        Assert.False(string.IsNullOrWhiteSpace(family));
        Assert.Equal(family, CjkFontRegistry.EnsureFamily()); // 缓存幂等
    }
}
