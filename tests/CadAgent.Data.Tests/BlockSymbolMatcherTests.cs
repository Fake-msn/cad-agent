using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// BF-T2：块级纠偏三级匹配（spec §13.3）——符号索引（skill 包 DXF）+排除启发+
/// 精确自动种子 GeometryInference 0.9 / 近似建议（不自动）/ 无命中高频歧义 + marker 范围 re-scan 清理。
/// 夹具 API 按现状定型：点=CSMath.XYZ；DxfWriter.Write 静态；symbols.yaml 根键 symbols:；
/// BlockRecord(name)/Insert(br) 构造（ACadSharp 3.8.35，DxfFixture/InsertBlockTests 同口径）。
/// </summary>
public class BlockSymbolMatcherTests : IDisposable
{
    private readonly string _pkg = Path.Combine(Path.GetTempPath(), $"bfpg-{Guid.NewGuid():N}");

    public BlockSymbolMatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(_pkg, "symbols"));
        File.WriteAllText(Path.Combine(_pkg, "skill.yaml"), "name: test-pkg\nversion: \"1.0.0\"\n");
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        var sym = new CadDocument();
        sym.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        sym.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "sym.dxf")))
            DxfWriter.Write(fs, sym);
    }

    public void Dispose() { try { Directory.Delete(_pkg, true); } catch (IOException) { } }

    private static CadDocument DocWithBlock(string name, Action<BlockRecord> populate, int inserts = 4)
    {
        var doc = new CadDocument();
        var br = new BlockRecord(name);
        populate(br);
        doc.BlockRecords.Add(br);
        for (int i = 0; i < inserts; i++)
            doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 100, 0, 0) });
        return doc;
    }

    private static void SymbolGeometry(BlockRecord br)
    {
        br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
    }

    private BlockMatchResult Run(CadDocument doc, SemanticOverlay? overlay = null, int min = 3)
    {
        var packages = new[] { SkillLoader.LoadDirectory(_pkg) };
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        return BlockSymbolMatcher.MatchAndSeed(doc, walked, overlay ?? new SemanticOverlay(), packages, min);
    }

    [Fact]
    public void ExactClone_AutoSeedsAllInstances_GeometryInference09()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        var r = Run(doc, overlay);
        Assert.Single(r.AutoSeeds);
        Assert.Equal("测试符号", r.AutoSeeds[0].SymbolName);
        Assert.Equal(4, r.AutoSeeds[0].Handles.Count);
        Assert.Equal(4, overlay.Annotations.Count(a => a.Provenance == Provenance.GeometryInference));
        Assert.All(overlay.Annotations.Where(a => a.Provenance == Provenance.GeometryInference),
            a => { Assert.Equal(0.9, a.Confidence); Assert.StartsWith("几何指纹精确匹配:", a.Evidence); Assert.Equal(SemanticClass.Device, a.Class); });
        Assert.Empty(r.Suggestions);
    }

    [Fact]
    public void PerturbedBlock_SuggestionOnly_NoAutoSeed_ConfidenceFormula()
    {
        // 扰动端点 12.0（brief 原值 10.5 经 T1 公式仅 d≈0.019→confidence≈0.86，
        // 与 brief 自身 <0.8 断言矛盾；加大扰动使 d≈0.065∈(0.05,0.15]，断言逐字保留）。
        var doc = DocWithBlock("近似块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(12, 0, 0) });
            br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        });
        var overlay = new SemanticOverlay();
        var r = Run(doc, overlay);
        Assert.Empty(r.AutoSeeds);
        Assert.Empty(overlay.Annotations);
        var s = Assert.Single(r.Suggestions);
        var c = Assert.Single(s.Candidates);
        Assert.Equal("测试符号", c.SymbolName);
        Assert.InRange(c.Distance, 0.001, BlockFingerprint.NearThreshold);
        Assert.Equal(BlockFingerprint.Confidence(c.Distance), c.Confidence, 9);
        Assert.True(c.Confidence < 0.8);
    }

    [Fact]
    public void UnmatchedHighFreq_Ambiguous_NoMatch()
    {
        var doc = DocWithBlock("自定义块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
        });
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("自定义块", a.BlockName);
        Assert.Equal("无匹配", a.Reason);
        Assert.Equal(4, a.Instances);
        Assert.NotEmpty(a.SampleHandles);
    }

    [Fact]
    public void ProxyLikeBlock_NoWhitelistGeometry_AmbiguousProxyReason()
    {
        // R4：Hatch 代代理夹具——零白名单几何与天正代理同构（Extract→null→"代理图形不可读"）。
        var doc = DocWithBlock("$equip$0001", br => br.Entities.Add(new Hatch()));
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("代理图形不可读", a.Reason);
    }

    [Fact]
    public void LowInstances_Unmatched_NotAmbiguous()
    {
        var doc = DocWithBlock("偶发块", br => br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(9, 9, 0) }), inserts: 2);
        var r = Run(doc);
        Assert.Empty(r.Ambiguous);
    }

    [Fact]
    public void Exclusions_Frame_SystemPrefix_ModelSpace()
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new AttributeDefinition { Tag = "图名" });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1189, 841, 0) });
        Assert.True(BlockSymbolMatcher.IsExcluded(frame));
        var smallAttdef = new BlockRecord("小属性块");
        smallAttdef.Entities.Add(new AttributeDefinition { Tag = "A" });
        smallAttdef.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 10, 0) });
        Assert.False(BlockSymbolMatcher.IsExcluded(smallAttdef)); // 有 ATTDEF 但尺寸<500 不排除
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("$LinePat$00000004")));
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("$TCHSYS$DOOR3D")));
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("*Model_Space")));
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("空块"))); // 空定义排除
        var anon = new BlockRecord("*U160");
        SymbolGeometry(anon);
        Assert.False(BlockSymbolMatcher.IsExcluded(anon)); // 匿名块不排除
    }

    [Fact]
    public void Rescan_ClearsStaleBlockSeeds_KeepsHumanConfirmedAndWallHeuristics()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        Run(doc, overlay);
        overlay.UpsertAnnotation(new SemanticAnnotation("999", SemanticClass.Wall, Provenance.GeometryInference, 0.7, "平行线对"));
        overlay.UpsertAnnotation(new SemanticAnnotation("888", SemanticClass.Device, Provenance.HumanConfirmed, 1.0, "人工确认"));
        // 块定义被改走样→re-scan 应清陈旧块种子
        var br = doc.BlockRecords.First(b => b.Name == "符号块");
        br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(50, 50, 0) });
        var r = Run(doc, overlay);
        Assert.Empty(r.AutoSeeds);
        Assert.DoesNotContain(overlay.Annotations, a => a.Evidence.StartsWith("几何指纹精确匹配:"));
        Assert.Contains(overlay.Annotations, a => a.TargetHandle == "999" && a.Provenance == Provenance.GeometryInference); // 墙启发式保留
        Assert.Contains(overlay.Annotations, a => a.TargetHandle == "888" && a.Provenance == Provenance.HumanConfirmed);
    }

    [Fact]
    public void HumanConfirmedInstance_SkippedByAutoSeed()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        var first = walked.First(w => w.Entity is Insert);
        overlay.UpsertAnnotation(new SemanticAnnotation(first.HandlePath, SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工"));
        var packages = new[] { SkillLoader.LoadDirectory(_pkg) };
        BlockSymbolMatcher.MatchAndSeed(doc, walked, overlay, packages);
        Assert.DoesNotContain(overlay.Annotations, a => a.TargetHandle == first.HandlePath && a.Provenance == Provenance.GeometryInference);
        Assert.Contains(overlay.Annotations, a => a.TargetHandle == first.HandlePath && a.Provenance == Provenance.HumanConfirmed && a.Class == SemanticClass.Lighting);
    }

    [Fact]
    public void BrokenSymbolDxf_SkippedWithCount_OthersStillMatch()
    {
        File.WriteAllText(Path.Combine(_pkg, "symbols", "bad.dxf"), "不是 DXF");
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n  - { name: 坏符号, file: bad.dxf, class: Device, description: 坏 }\n");
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var r = Run(doc);
        Assert.Equal(1, r.SymbolsLoaded);
        Assert.Equal(1, r.SymbolsSkipped);
        Assert.Single(r.AutoSeeds);
    }

    [Fact]
    public void TruncatedSymbolDxf_DxfException_SkippedWithCount_OthersStillMatch()
    {
        // R2 实证钉住：合法 DXF 截断一半 → DxfReader 抛型异构且漂移（实证 DxfException 与
        // NullReferenceException 两种，HEADER 时间戳致截断落点不定）——catch 面须宽捕，
        // 否则整体失败（违反 spec"不整体失败"）。证据 outputs/block-fingerprint/t2-r2-*.log。
        // 收窄 catch 漏任一型即回红。
        byte[] full = File.ReadAllBytes(Path.Combine(_pkg, "symbols", "sym.dxf"));
        File.WriteAllBytes(Path.Combine(_pkg, "symbols", "trunc.dxf"), full.Take(full.Length / 2).ToArray());
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n  - { name: 截断符号, file: trunc.dxf, class: Device, description: 截断 }\n");
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var r = Run(doc);
        Assert.Equal(1, r.SymbolsLoaded);
        Assert.Equal(1, r.SymbolsSkipped);
        Assert.Single(r.AutoSeeds);
    }

    [Fact]
    public void EmptyPackages_EmptyResult_NoThrow()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        var r = BlockSymbolMatcher.MatchAndSeed(doc, walked, new SemanticOverlay(), Array.Empty<SkillPackage>());
        Assert.Empty(r.AutoSeeds); Assert.Empty(r.Suggestions); Assert.Empty(r.Ambiguous);
        Assert.Equal(0, r.SymbolsLoaded);
    }

    // ---- BF-T3 顺带补 T2 遗留测试缺口（审查建议，sanctioned）----

    [Fact]
    public void ExactClone_EvidenceCarriesSymbolNameSuffix()
    {
        // evidence 全文钉住：前缀 + 空格 + 符号名（T4 map-block 与人工审查都消费该后缀）。
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        Run(doc, overlay);
        var seeded = overlay.Annotations.Where(a => a.Provenance == Provenance.GeometryInference).ToList();
        Assert.Equal(4, seeded.Count);
        Assert.All(seeded, a => Assert.Equal("几何指纹精确匹配: 测试符号", a.Evidence));
    }

    [Fact]
    public void ExactClone_SeedsCarrySheetId_WhenFramePresent()
    {
        // 幅面夹具（镜像 SheetTilerTests.TilerSetup）：1000×700 图框 → 幅面非 null；
        // 符号块实例落框内 → 种子 SheetId 与幅面一致（幅面作用域闸口消费）。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        var br = new BlockRecord("符号块");
        SymbolGeometry(br);
        doc.BlockRecords.Add(br);
        for (int i = 0; i < 4; i++)
            doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(100 + i * 100, 100, 0) });
        var sheets = SheetRegistryBuilder.Build(doc);
        var sheet = Assert.Single(sheets);
        Assert.NotNull(sheet.SheetId);
        var walked = BlockWalker.Walk(doc, sheets).ToList();
        var overlay = new SemanticOverlay();
        var r = BlockSymbolMatcher.MatchAndSeed(doc, walked, overlay, new[] { SkillLoader.LoadDirectory(_pkg) });
        Assert.Single(r.AutoSeeds);
        var seeded = overlay.Annotations.Where(a => a.Provenance == Provenance.GeometryInference
            && a.Evidence.StartsWith("几何指纹精确匹配:")).ToList();
        Assert.Equal(4, seeded.Count);
        Assert.All(seeded, a => Assert.Equal(sheet.SheetId, a.SheetId));
    }

    [Fact]
    public void MultiCandidate_SuggestionsSortedByDistanceAscending()
    {
        // 第二符号线端点 14（far.dxf）：块端点 12 → d(远)≈0.047 < d(测)≈0.065，
        // yaml 序测试符号在前——升序披露必须远符号居首（钉住排序键=d 而非包序/yaml 序）。
        var far = new CadDocument();
        far.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(14, 0, 0) });
        far.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "far.dxf")))
            DxfWriter.Write(fs, far);
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n  - { name: 远符号, file: far.dxf, class: Device, description: 远 }\n");
        var doc = DocWithBlock("近似块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(12, 0, 0) });
            br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        });
        var r = Run(doc);
        Assert.Empty(r.AutoSeeds);
        var s = Assert.Single(r.Suggestions);
        Assert.Equal(2, s.Candidates.Count);
        Assert.Equal("远符号", s.Candidates[0].SymbolName);
        Assert.Equal("测试符号", s.Candidates[1].SymbolName);
        Assert.True(s.Candidates[0].Distance < s.Candidates[1].Distance);
        Assert.True(s.Candidates[0].Confidence > s.Candidates[1].Confidence);
    }

    [Fact]
    public void MinInstancesBoundary_TwoNotAmbiguous_ThreeAmbiguous()
    {
        // minInstances 默认 3 的边界钉住：无匹配块 2 实例不歧义 / 3 实例歧义。
        static void Triangle(BlockRecord br)
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
        }
        var two = Run(DocWithBlock("边界块", Triangle, inserts: 2));
        Assert.Empty(two.Ambiguous);
        var three = Run(DocWithBlock("边界块", Triangle, inserts: 3));
        var a = Assert.Single(three.Ambiguous);
        Assert.Equal("无匹配", a.Reason);
        Assert.Equal(3, a.Instances);
    }

    // ---- PR#21 合并前 ISTQB 修复波 2（择要 Minor，纯测试新增）----

    /// <summary>
    /// M3（精确取首确定性）：两同哈希符号——yaml 序与包序双维度钉住"取首"，
    /// class/evidence 随首符号（镜像 insert-symbol 首包命中先例）。
    /// </summary>
    [Fact]
    public void ExactMultiSymbolSameHash_FirstWinsDeterministic()
    {
        // 克隆符号：与 sym.dxf 同几何（同哈希），class Lighting 区分来源
        var clone = new CadDocument();
        clone.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        clone.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "clone.dxf")))
            DxfWriter.Write(fs, clone);
        var doc = DocWithBlock("符号块", SymbolGeometry);

        // 维度一：yaml 序——测试符号在前 → 取测试符号（Device）
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n  - { name: 克隆符号, file: clone.dxf, class: Lighting, description: 克隆 }\n");
        var ov1 = new SemanticOverlay();
        var r1 = Run(doc, ov1);
        Assert.Equal("测试符号", Assert.Single(r1.AutoSeeds).SymbolName);
        Assert.All(ov1.Annotations.Where(x => x.Provenance == Provenance.GeometryInference),
            x => { Assert.Equal(SemanticClass.Device, x.Class); Assert.Equal("几何指纹精确匹配: 测试符号", x.Evidence); });
        // yaml 序反转 → 取克隆符号（Lighting）
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 克隆符号, file: clone.dxf, class: Lighting, description: 克隆 }\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        var ov2 = new SemanticOverlay();
        var r2 = Run(doc, ov2);
        Assert.Equal("克隆符号", Assert.Single(r2.AutoSeeds).SymbolName);
        Assert.All(ov2.Annotations.Where(x => x.Provenance == Provenance.GeometryInference),
            x => Assert.Equal(SemanticClass.Lighting, x.Class));

        // 维度二：包序——两包各一符号，packages 列表序决定取首
        string pkg2 = Path.Combine(Path.GetTempPath(), $"bfpg2-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(pkg2, "symbols"));
            File.WriteAllText(Path.Combine(pkg2, "skill.yaml"), "name: test-pkg-2\nversion: \"1.0.0\"\n");
            File.WriteAllText(Path.Combine(pkg2, "symbols", "symbols.yaml"),
                "symbols:\n  - { name: 另包符号, file: sym.dxf, class: Wall, description: 另包 }\n");
            using (var fs = File.Create(Path.Combine(pkg2, "symbols", "sym.dxf")))
                DxfWriter.Write(fs, clone);
            // _pkg 恢复单符号
            File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
                "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
            var p1 = SkillLoader.LoadDirectory(_pkg);
            var p2 = SkillLoader.LoadDirectory(pkg2);
            var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
            var ov3 = new SemanticOverlay();
            var r3 = BlockSymbolMatcher.MatchAndSeed(doc, walked, ov3, new[] { p1, p2 });
            Assert.Equal("测试符号", Assert.Single(r3.AutoSeeds).SymbolName);
            var ov4 = new SemanticOverlay();
            var r4 = BlockSymbolMatcher.MatchAndSeed(doc, walked, ov4, new[] { p2, p1 });
            Assert.Equal("另包符号", Assert.Single(r4.AutoSeeds).SymbolName);
            Assert.All(ov4.Annotations.Where(x => x.Provenance == Provenance.GeometryInference),
                x => Assert.Equal(SemanticClass.Wall, x.Class));
        }
        finally { try { Directory.Delete(pkg2, true); } catch (IOException) { } }
    }

    /// <summary>
    /// M4（R6 早退守卫）：预置块级种子 + 全符号损坏 → symbols.Count==0 早退，
    /// 既有种子不被清（RemoveAll 仅在索引非空后执行）；扫描计数全 0。
    /// </summary>
    [Fact]
    public void AllSymbolsBroken_PresetSeedsPreserved_R6EarlyExit()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        // 预置既有块级种子（marker evidence）
        overlay.UpsertAnnotation(new SemanticAnnotation("123", SemanticClass.Device,
            Provenance.GeometryInference, 0.9, "几何指纹精确匹配: 测试符号"));
        // 全符号损坏（垃圾文本 → DxfReader 抛 → 跳过计数）
        File.WriteAllText(Path.Combine(_pkg, "symbols", "sym.dxf"), "不是 DXF");
        var r = Run(doc, overlay);
        Assert.Equal(0, r.SymbolsLoaded);
        Assert.Equal(1, r.SymbolsSkipped);
        Assert.Empty(r.AutoSeeds);
        Assert.Empty(r.Suggestions);
        Assert.Empty(r.Ambiguous);
        Assert.Equal(0, r.BlocksScanned);
        Assert.Equal(0, r.BlocksExcluded);
        // 既有种子原样保留（早退不清 overlay）
        Assert.Contains(overlay.Annotations, x => x.TargetHandle == "123"
            && x.Provenance == Provenance.GeometryInference && x.Evidence.StartsWith("几何指纹精确匹配:"));
    }

    /// <summary>M5（边界择要）：IsExcluded ATTDEF bbox 最长边恰 500 排除（&gt;= 口径）；499 不排除。</summary>
    [Fact]
    public void Excluded_AttdefBboxExactly500_Excluded_JustUnderNot()
    {
        var exact = new BlockRecord("图框恰500");
        exact.Entities.Add(new AttributeDefinition { Tag = "图名" });
        exact.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(500, 0, 0) });
        Assert.True(BlockSymbolMatcher.IsExcluded(exact)); // 最长边=500 恰排除
        var under = new BlockRecord("图框499");
        under.Entities.Add(new AttributeDefinition { Tag = "图名" });
        under.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(499, 0, 0) });
        Assert.False(BlockSymbolMatcher.IsExcluded(under)); // 499<500 不排除
    }

    /// <summary>M5（边界择要）：*Paper 前缀排除（OrdinalIgnoreCase）——带几何夹具隔离前缀规则（空定义规则不干扰）。</summary>
    [Fact]
    public void Excluded_PaperPrefix_CaseInsensitive()
    {
        var paper = new BlockRecord("*Paper_Space");
        SymbolGeometry(paper);
        Assert.True(BlockSymbolMatcher.IsExcluded(paper));
        var paperLower = new BlockRecord("*paper_space0");
        SymbolGeometry(paperLower);
        Assert.True(BlockSymbolMatcher.IsExcluded(paperLower));
    }

    /// <summary>M5（边界择要）：sampleHandles Take(5) 上限——6 实例夹具 → 样本恰 5 且为 walked 前 5 句柄。</summary>
    [Fact]
    public void Ambiguous_SampleHandlesCappedAtFive_SixInstances()
    {
        static void Triangle(BlockRecord br)
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
        }
        var doc = DocWithBlock("自定义块", Triangle, inserts: 6);
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal(6, a.Instances); // 实例计数全量
        Assert.Equal(5, a.SampleHandles.Count); // 样本上限 5
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        var expected = walked.Where(w => w.Entity is Insert).Take(5).Select(w => w.HandlePath).ToList();
        Assert.Equal(expected, a.SampleHandles); // Take(5) 序钉住
    }

    // ---- R9②③ 诊断披露补全（spec §13.2 歧义诊断字段 / §13.4 NaN 剔除披露）----

    /// <summary>
    /// ② 歧义项 Histogram 逐字钉住：块定义**全部实体**类型计数（含非白名单 Hatch），
    /// 按 Type Ordinal 排序；Bbox 数值钉住（PointsOf 口径：线端点+圆外接两角）。
    /// </summary>
    [Fact]
    public void Ambiguous_HistogramSortedFullTypeCounts_BboxValuesPinned()
    {
        var doc = DocWithBlock("自定义块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
            br.Entities.Add(new Circle { Center = new XYZ(1, 1, 0), Radius = 0.5 });
            br.Entities.Add(new Hatch()); // 非白名单——诊断全量口径仍计数
        });
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("无匹配", a.Reason);
        // Ordinal 排序：Circle < Hatch < Line；record 值相等逐字钉住
        Assert.Equal(new[] { new BlockHistEntry("Circle", 1), new BlockHistEntry("Hatch", 1), new BlockHistEntry("Line", 3) },
            a.Histogram);
        // bbox：线点 (0,0)/(1,5)/(3,1) + 圆外接 (0.5,0.5)/(1.5,1.5) → (0,0)-(3,5)
        Assert.Equal(new BlockBbox(0, 0, 3, 5), a.Bbox);
    }

    /// <summary>② 代理块（仅 Hatch，零 PointsOf 点）：Bbox null + Histogram 仍全量披露。</summary>
    [Fact]
    public void Ambiguous_ProxyHatchOnly_BboxNull_HistogramFull()
    {
        var doc = DocWithBlock("$equip$0001", br => br.Entities.Add(new Hatch()));
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("代理图形不可读", a.Reason);
        Assert.Null(a.Bbox); // 零几何 → null
        Assert.Equal(new[] { new BlockHistEntry("Hatch", 1) }, a.Histogram);
    }

    /// <summary>
    /// ② 代理块 Hatch+AttDef：Histogram 含全部非白名单类型（AttributeDefinition/Hatch）；
    /// Bbox 按 PointsOf 口径——AttDef : TextEntity（探针实证）插入点参与 → 退化点 bbox。
    /// </summary>
    [Fact]
    public void Ambiguous_ProxyHatchAttDef_HistogramFull_BboxFromInsertPoint()
    {
        var doc = DocWithBlock("$equip$0002", br =>
        {
            br.Entities.Add(new Hatch());
            br.Entities.Add(new AttributeDefinition { Tag = "A", InsertPoint = new XYZ(2, 3, 0) });
        });
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("代理图形不可读", a.Reason);
        Assert.Equal(new[] { new BlockHistEntry("AttributeDefinition", 1), new BlockHistEntry("Hatch", 1) },
            a.Histogram);
        Assert.Equal(new BlockBbox(2, 3, 2, 3), a.Bbox); // 仅 AttDef 插入点 (2,3)
    }

    /// <summary>
    /// ③ NonFiniteDropped 累计：符号含 NaN 线（nan.dxf 剔除 1）+ 块含 NaN 线（剔除 1）
    /// → 求和 2（matcher 内所有 Extract 调用 dropped 累计口径）。
    /// </summary>
    [Fact]
    public void NonFiniteDropped_BlockAndSymbolExtracts_Summed()
    {
        var nanSym = new CadDocument();
        nanSym.Entities.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        nanSym.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "nan.dxf")))
            DxfWriter.Write(fs, nanSym);
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n  - { name: NaN符号, file: nan.dxf, class: Device, description: NaN }\n");
        var doc = DocWithBlock("自定义块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(9, 9, 0) });
        });
        var r = Run(doc);
        Assert.Equal(2, r.SymbolsLoaded);
        Assert.Equal("无匹配", Assert.Single(r.Ambiguous).Reason);
        Assert.Equal(2, r.NonFiniteDropped); // 符号 1 + 块 1
    }

    /// <summary>③ NaN-only 代理块（fp null 路径）：剔除数仍累计披露（out 重载通道，不丢计数）。</summary>
    [Fact]
    public void NonFiniteDropped_NaNOnlyProxyBlock_StillCounted()
    {
        var doc = DocWithBlock("$equip$0003", br =>
        {
            br.Entities.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(1, 1, 0) });
            br.Entities.Add(new Hatch());
        });
        var r = Run(doc);
        Assert.Equal("代理图形不可读", Assert.Single(r.Ambiguous).Reason);
        Assert.Equal(1, r.NonFiniteDropped);
    }

    /// <summary>③ 空包早退：NonFiniteDropped=0（无 Extract 调用）。</summary>
    [Fact]
    public void EmptyPackages_NonFiniteDroppedZero()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        var r = BlockSymbolMatcher.MatchAndSeed(doc, walked, new SemanticOverlay(), Array.Empty<SkillPackage>());
        Assert.Equal(0, r.NonFiniteDropped);
    }

    /// <summary>③ 全符号损坏早退（symbols.Count==0）：NonFiniteDropped=0（早退路径 0 口径）。</summary>
    [Fact]
    public void AllSymbolsBroken_EarlyExit_NonFiniteDroppedZero()
    {
        File.WriteAllText(Path.Combine(_pkg, "symbols", "sym.dxf"), "不是 DXF");
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var r = Run(doc);
        Assert.Equal(0, r.SymbolsLoaded);
        Assert.Equal(0, r.NonFiniteDropped);
    }

    /// <summary>
    /// ③ NaN-only 符号包早退判别（钉住批准设计"早退 0"口径，防日后"顺手修复"为累计值）：
    /// 符号 DXF 仅一条 NaN 坐标 Line——过白名单→Extract null（symDropped 已累计 1）→
    /// symbols.Count==0 早退→NonFiniteDropped==0 而非累计 1。与全符号损坏早退测试的判别差异：
    /// 该路径确实发生了 Extract 剔除累计（损坏路径 catch 在 Extract 之前，累计恒 0 无判别力）。
    /// 夹具镜像 BrokenSymbolDxf 覆盖模式；NaN 经 DxfWriter/DxfReader 往返保 NaN
    /// （probe 实证，outputs/block-diagnostics/probe.txt）。
    /// </summary>
    [Fact]
    public void NaNOnlySymbolPackage_EarlyExit_NonFiniteDroppedZero_NotAccumulated()
    {
        // 覆盖 sym.dxf 为 NaN-only（yaml 唯一符号）——Line 过白名单，Extract 全剔除→null→跳过
        var nanOnly = new CadDocument();
        nanOnly.Entities.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "sym.dxf")))
            DxfWriter.Write(fs, nanOnly);
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var r = Run(doc);
        Assert.Equal(0, r.SymbolsLoaded);
        Assert.Equal(1, r.SymbolsSkipped); // fp null 跳过路径（非读取异常 catch 路径）
        Assert.Equal(0, r.NonFiniteDropped); // 早退归 0——即使 symDropped 已累计 1，不得披露累计值
        Assert.Empty(r.AutoSeeds);
        Assert.Empty(r.Suggestions);
        Assert.Empty(r.Ambiguous);
    }
}
