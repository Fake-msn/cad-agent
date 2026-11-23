using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Tests;

/// <summary>
/// BF-T5：真实标本实证（skip-gated，spec §13.5）——电施强电副本 + electrical skill 包
/// （324 符号）全链 Scan：三级输出口径一致性断言（公式/marker/reason 合法/只读），
/// 不赌 GB 精确命中数（语料为 TArch $equip$ 代理 + 自定义 plain 块；命中率因语料而异——
/// 本标本实证 102 实例精确命中，其他语料可能更低，不赌具体数字）。
/// 标本只读副本（源 D: 零触碰）：临时目录副本字节前后一致；缺标本/缺包→显式跳过不假装。
/// </summary>
public class RealBlockFingerprintTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [SkippableFact]
    public void RealSpecimen_Elec_ThreeTierOutput_HonestDisclosure()
    {
        string root = RepoRoot();
        string specimen = Path.Combine(root, "outputs", "api-probe", "specimen-elec.dwg");
        string pkgDir = Path.Combine(root, "examples", "skills", "electrical");
        Skip.If(!File.Exists(specimen), "电施标本不在本机，跳过");
        Skip.If(!Directory.Exists(pkgDir), "electrical skill 包不在本机，跳过");
        // 副本（源零触碰）
        string tmp = Path.Combine(Path.GetTempPath(), $"realbf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            string copy = Path.Combine(tmp, "elec.dwg");
            File.Copy(specimen, copy);
            byte[] before = File.ReadAllBytes(copy);
            var session = DrawingSession.Load(copy);
            var packages = new[] { SkillLoader.LoadDirectory(pkgDir) };
            var result = SemanticScanner.Scan(session, symbolPackages: packages);
            // xunit 2.5.3 Assert.NotNull 返回 void（无泛型返回值 overload）——先断言再取用
            Assert.NotNull(result.BlockMatch);
            var bm = result.BlockMatch!;
            Assert.Equal(324, bm.SymbolsLoaded + bm.SymbolsSkipped); // 库全量 accounted
            Assert.True(bm.BlocksScanned + bm.BlocksExcluded > 200); // 普查 246 非空块口径
            // 自动种子全 0.9 + marker；建议全闸下 + 公式；歧义 reason 合法 + 样本非空
            var overlay = result.Overlay;
            // 防空过（fix wave）：marker 集非空 + 计数 tie 到 AutoSeeds 实例句柄总数
            var markers = overlay.Annotations.Where(a => a.Evidence.StartsWith("几何指纹精确匹配:")).ToList();
            Assert.NotEmpty(markers);
            Assert.Equal(bm.AutoSeeds.Sum(a => a.Handles.Count), markers.Count);
            Assert.All(markers,
                a => { Assert.Equal(0.9, a.Confidence); Assert.Equal(Provenance.GeometryInference, a.Provenance); });
            Assert.All(bm.Suggestions.SelectMany(s => s.Candidates), c =>
            {
                Assert.InRange(c.Distance, 0, BlockFingerprint.NearThreshold);
                Assert.Equal(BlockFingerprint.Confidence(c.Distance), c.Confidence, 9);
                // T5 真实语料更正（brief 原断言 c.Confidence<0.8 过度）：公式 0.9−2d 为 spec §13.2
                // 钉住口径，d<0.05 → conf>0.8（本标本 30 候选中 17 个 conf≥0.8，d∈[0.0035,0.05]，
                // 含 LG-QQ1→电锁按键 d=0.0035 最小距离候选）。
                // "近似永不自动落种子"是结构保证（近似档只披露不落标注），非全值域公式闸下——
                // 断言口径一致性（公式+值域 [0.6,0.9]），阈值 0.15 不动（R3 不静默改阈）。
                Assert.InRange(c.Confidence, 0.6, 0.9);
            });
            // 三级互斥（结构 never-auto 保证）：建议块不得同时落自动种子
            Assert.DoesNotContain(bm.AutoSeeds, a => bm.Suggestions.Any(s => s.BlockName == a.BlockName));
            Assert.All(bm.Ambiguous, a =>
            {
                Assert.Contains(a.Reason, new[] { "代理图形不可读", "无匹配" });
                Assert.NotEmpty(a.SampleHandles);
                Assert.True(a.Instances >= 3);
            });
            // $equip$00002832×1364 高频块必须披露入歧义（不静默丢弃）+ reason 合法 + 绝不自动种子。
            // 实战更正（spec §12 注记）：该块定义暴露最小可读几何占位（2 Line+1 Circle），fp 非 null，
            // 最近相容 GB 符号 d=0.1883>0.15 → reason=无匹配（非 spec 预期的代理图形不可读）；
            // 真代理案例（fp NULL→代理图形不可读）如 $TelecSys$00000152。断言合法 reason 集不赌具体值。
            var equip = bm.Ambiguous.FirstOrDefault(a => a.BlockName.Equals("$equip$00002832", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(equip);
            Assert.True(equip!.Instances >= 1000); // 普查 ×1364 高频
            Assert.Contains(equip.Reason, new[] { "代理图形不可读", "无匹配" });
            Assert.DoesNotContain(bm.AutoSeeds, a => a.BlockName.Equals("$equip$00002832", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(before, File.ReadAllBytes(copy)); // 只读扫描
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }
}
