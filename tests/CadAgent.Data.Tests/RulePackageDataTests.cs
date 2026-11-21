using CadAgent.Core;
using CadAgent.Data.Checks;
using CadAgent.Data.Rules;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-b T6 电气/模型规则包数据测试（examples/skills/electrical + model）。
/// 钉住：SkillLoader.LoadDirectory 两包可加载（规则数 4/1）；清单字段；excerpt 逐字
/// （片段同时须逐字出现在 standards-src extraction 源文件中——数据↔源双向守卫）；
/// 三白名单 14/11/11 逐项有序精确（D9）；modal 情态冲突在案；与 builtin Id 无冲突
/// （SkillSet.Compose 不抛）。examples 包非 builtin——RulePackVersion 治理哈希只钉
/// builtin（BuiltinGovernanceTests），本测试不钉 examples 哈希。
/// 放在 Data.Tests：BuiltinRules 位于 Data（Core 不能引用 Data）。
/// </summary>
public class RulePackageDataTests
{
    // ---- extraction 逐字片段（外层引号剥除，沿用 builtin 13.0.1 先例；无 LaTeX 归一化需求） ----

    /// <summary>H3（5.1.3）逐字。</summary>
    private const string H3 =
        "图样中本专业的汉字标注字高不宜小于 3.5mm，主导专业工艺、功能用房的汉字标注字高不宜小于 3.0mm，字母或数字标注字高不应小于 2.5mm";

    /// <summary>D8（3.4.3）逐字。</summary>
    private const string D8 =
        "照明灯具安装方式、线缆敷设方式及敷设部位，应按本标准表 4.2.1-1～表 4.2.1-3 的文字符号标注";

    /// <summary>D7（4.1.5）操作句逐字。</summary>
    private const string D7Op =
        "绘制图样时，宜采用表 4.1.5 的电气设备标注方式表示";

    /// <summary>D7 序号8 格式注记逐字（机器可检解释列内嵌引文，含 markdown 反引号原样）。</summary>
    private const string D7Item8 =
        "序号8 线缆 `a b-c (d×e+f×g)i-jh`（\"d—相导体根数e—相导体截面(mm²)f—N、PE导体根数g—N、PE导体截面(mm²)i—敷设方式和管径(mm)…j—敷设部位…h—安装高度(m)\"）";

    /// <summary>D7 序号5 格式注记逐字。</summary>
    private const string D7Item5 =
        "序号5 灯具 `a-b (c×d×L)/e f`（\"a—数量b—型号c—每盏灯具的光源数量d—光源安装容量e—安装高度(m)'—'表示吸顶安装L—光源种类…f—安装方式,参见表4.2.1-3\"）";

    /// <summary>R1（JGJ/T 448 5.4.3）主干逐字（简报范围：至"隔开"止；款内列举 deferred）。</summary>
    private const string R1 =
        "工程图纸命名宜由专业代码、图纸编号、图纸名称、描述等字段依次组成，以下划线\"_\"隔开，字段内部的词组以连字符\"-\"隔开";

    private static readonly string[] LayingMethods =
        { "SC", "MT", "CP", "PC", "FPC", "KPC", "CT", "CL", "MR", "PR", "M", "DB", "TC", "CE" };

    private static readonly string[] LayingPositions =
        { "AB", "AC", "CE", "SCE", "WS", "RS", "CC", "BC", "CLC", "WC", "FC" };

    private static readonly string[] LuminaireMounts =
        { "SW", "CS", "DS", "W", "C", "R", "CR", "WR", "S", "CL", "HM" };

    /// <summary>仓库根：从测试程序集目录上溯找 CadAgent.sln（examples 为入库文件，非嵌入资源）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ElectricalDir() => Path.Combine(RepoRoot(), "examples", "skills", "electrical");
    private static string ModelDir() => Path.Combine(RepoRoot(), "examples", "skills", "model");

    private static Rule ById(SkillPackage pkg, string id) => Assert.Single(pkg.Rules, r => r.Id == id);

    private static string[] StringList(Rule rule, string key)
    {
        Assert.True(rule.Check.Params.TryGetValue(key, out var raw));
        return Assert.IsAssignableFrom<IEnumerable<object>>(raw).Select(v => Assert.IsType<string>(v)).ToArray();
    }

    [Fact]
    public void ElectricalPackage_Loads_FourRules_ManifestExact_AliasesEmpty()
    {
        var pkg = SkillLoader.LoadDirectory(ElectricalDir());
        Assert.Equal("建筑电气制图（GB/T 50786-2012）", pkg.Manifest.Name);
        Assert.Equal("1.0.0", pkg.Manifest.Version);
        Assert.Equal("electrical", pkg.Manifest.Discipline);
        Assert.Contains("外部核验待原件确认", pkg.Manifest.Description);
        Assert.Contains("VISUAL deferred", pkg.Manifest.Description);
        Assert.Equal(4, pkg.Rules.Count);
        Assert.Empty(pkg.Aliases); // 第一 slice 不编造别名（无 extraction 依据）
        Assert.Equal(
            new[] { "R-50786-3.4.3", "R-50786-4.1.5-5", "R-50786-4.1.5-8", "R-50786-5.1.3" },
            pkg.Rules.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.All(pkg.Rules, r =>
        {
            Assert.Equal("GB/T 50786-2012", r.Source.Standard);
            Assert.Equal("source-text", r.Source.Verification);
            Assert.Equal(RuleForce.Recommended, r.Force);
            Assert.Equal("electrical", r.Scope.Discipline);
        });
    }

    [Fact]
    public void ElectricalPackage_Excerpts_Verbatim_FromExtraction()
    {
        var pkg = SkillLoader.LoadDirectory(ElectricalDir());
        Assert.Equal(H3, ById(pkg, "R-50786-5.1.3").Source.Excerpt);
        Assert.Equal(D8, ById(pkg, "R-50786-3.4.3").Source.Excerpt);
        Assert.Equal(D7Op + "；" + D7Item8, ById(pkg, "R-50786-4.1.5-8").Source.Excerpt);
        Assert.Equal(D7Op + "；" + D7Item5, ById(pkg, "R-50786-4.1.5-5").Source.Excerpt);

        // 双向守卫：每个逐字片段必须原样出现在 extraction 源行中（防数据漂移/编造）。
        string extraction = File.ReadAllText(Path.Combine(RepoRoot(), "standards-src", "extraction-electrical.md"));
        foreach (string fragment in new[] { H3, D8, D7Op, D7Item8, D7Item5 })
            Assert.Contains(fragment, extraction);
    }

    [Fact]
    public void ElectricalPackage_CheckTypesAndGrades_MatchCheckers()
    {
        var pkg = SkillLoader.LoadDirectory(ElectricalDir());
        var height = ById(pkg, "R-50786-5.1.3");
        Assert.Equal("text-height-min", height.Check.Type);
        Assert.Equal(RuleSeverity.High, height.Severity);
        Assert.Equal("不应／不宜", height.Modal);
        Assert.Equal("5.1.3", height.Source.Clause);
        Assert.Equal(2.5, CheckParamsProbe.ToDouble(height.Check.Params["minLatin"]));
        Assert.Equal(3.5, CheckParamsProbe.ToDouble(height.Check.Params["minHan"]));

        var cable = ById(pkg, "R-50786-4.1.5-8");
        Assert.Equal("cable-format", cable.Check.Type);
        Assert.Equal(RuleSeverity.High, cable.Severity);
        Assert.Equal("宜", cable.Modal);
        Assert.Empty(cable.Check.Params);

        var slot = ById(pkg, "R-50786-3.4.3");
        Assert.Equal("slot-code-whitelist", slot.Check.Type);
        Assert.Equal(RuleSeverity.High, slot.Severity);
        Assert.Equal("3.4.3", slot.Source.Clause);
        // 情态冲突在案（D8 应 vs D9 宜），按较严应执行——modal 双录逐字钉住。
        Assert.Equal("应（3.4.3）／宜（4.2.1）——情态冲突在案，按较严应执行", slot.Modal);

        var luminaire = ById(pkg, "R-50786-4.1.5-5");
        Assert.Equal("luminaire-format", luminaire.Check.Type);
        Assert.Equal(RuleSeverity.Medium, luminaire.Severity);
        Assert.Equal("宜", luminaire.Modal);

        // 数据↔检查器类型对齐：包内每个 check.type 都有已交付检查器（T3-T5）。
        var checkerTypes = new IRuleChecker[]
        {
            new TextHeightMinChecker(), new CableFormatChecker(), new SlotCodeChecker(),
            new LuminaireFormatChecker(), new FileNamingChecker()
        }.Select(c => c.CheckType).ToHashSet(StringComparer.Ordinal);
        Assert.All(pkg.Rules, r => Assert.Contains(r.Check.Type, checkerTypes));
    }

    [Fact]
    public void ElectricalPackage_Whitelists_Exact_14_11_11_D9Order()
    {
        var pkg = SkillLoader.LoadDirectory(ElectricalDir());
        var slot = ById(pkg, "R-50786-3.4.3");
        Assert.Equal(LayingMethods, StringList(slot, "layingMethods"));   // 表4.2.1-1，14 项
        Assert.Equal(LayingPositions, StringList(slot, "layingPositions")); // 表4.2.1-2，11 项
        Assert.Equal(LuminaireMounts, StringList(slot, "luminaireMounts")); // 表4.2.1-3，11 项
        // luminaire-format 复用同一 11 项安装方式白名单。
        Assert.Equal(LuminaireMounts, StringList(ById(pkg, "R-50786-4.1.5-5"), "luminaireMounts"));
    }

    [Fact]
    public void ModelPackage_Loads_OneRule_FileNaming_ExcerptVerbatim()
    {
        var pkg = SkillLoader.LoadDirectory(ModelDir());
        Assert.Equal("信息模型制图（JGJ/T 448-2018）", pkg.Manifest.Name);
        Assert.Equal("1.0.0", pkg.Manifest.Version);
        Assert.Equal("any", pkg.Manifest.Discipline);
        var rule = Assert.Single(pkg.Rules);
        Assert.Equal("R-448-5.4.3", rule.Id);
        Assert.Equal("JGJ/T 448-2018", rule.Source.Standard);
        Assert.Equal("5.4.3", rule.Source.Clause);
        Assert.Equal("source-text", rule.Source.Verification);
        Assert.Equal(R1, rule.Source.Excerpt);
        Assert.Equal("宜", rule.Modal);
        Assert.Equal(RuleForce.Recommended, rule.Force);
        Assert.Equal(RuleSeverity.Medium, rule.Severity);
        Assert.Equal("file-naming", rule.Check.Type);
        Assert.Empty(rule.Check.Params);
        Assert.Contains("deferred", rule.Suggestion); // 专业代码白名单（GB/T 51301）deferred 注记
        Assert.Empty(pkg.Aliases);

        string extraction = File.ReadAllText(Path.Combine(RepoRoot(), "standards-src", "extraction-model.md"));
        Assert.Contains(R1, extraction);
    }

    [Fact]
    public void BothPackages_ComposeWithBuiltin_NoIdCollision()
    {
        var builtin = BuiltinRules.Load();
        var electrical = SkillLoader.LoadDirectory(ElectricalDir());
        var model = SkillLoader.LoadDirectory(ModelDir());
        var set = SkillSet.Compose(builtin, new[] { electrical, model });
        Assert.Equal(builtin.Count + 4 + 1, set.AllRules.Count);
        Assert.StartsWith("gb50001-2017.r1|", set.RulePackVersion);
        Assert.Contains("建筑电气制图（GB/T 50786-2012）@1.0.0", set.RulePackVersion);
        Assert.Contains("信息模型制图（JGJ/T 448-2018）@1.0.0", set.RulePackVersion);
        Assert.True(set.ContainsRule("R-50786-3.4.3"));
        Assert.True(set.ContainsRule("R-448-5.4.3"));
    }

    /// <summary>参数标量转换探针（CheckParams internal，测试经 Convert 同口径 InvariantCulture）。</summary>
    private static class CheckParamsProbe
    {
        public static double ToDouble(object value) => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
