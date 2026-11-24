using System.Text.Json;
using ACadSharp;
using ACadSharp.IO;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan 2a end-to-end loop: scan → map → show → rescan → audit verify → run.
/// Fixture: WALL layer with two parallel lines d=200 (layer inference), a
/// custom-layer line (ambiguous until human-mapped), and an axis-grid pair
/// (circle r=400 + number text "1", geometry inference).
/// </summary>
public class SemanticE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"seme2e-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public SemanticE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("WALL"));
            doc.Layers.Add(new Layer("W-自定义"));
            var wall = doc.Layers.First(l => l.Name == "WALL");
            var custom = doc.Layers.First(l => l.Name == "W-自定义");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 200), EndPoint = (XYZ)new XY(2000, 200), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 3000), EndPoint = (XYZ)new XY(500, 3000), Layer = custom });
            doc.Entities.Add(new Circle { Center = (XYZ)new XY(5000, 0), Radius = 400 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(5000, 0), Value = "1", Height = 300 });
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void FullLoop_Scan_Map_Rescan_Run_AuditGreen()
    {
        var (scanCode, scanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        // Controller add-on (T7 review): exit code alone can empty-pass; the
        // overlay artifact must actually exist after the first scan.
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        Assert.Contains("Wall", scanOut);
        Assert.Contains("Axis", scanOut);
        Assert.Contains("W-自定义", scanOut);
        using (var scanDoc = System.Text.Json.JsonDocument.Parse(scanOut))
        {
            Assert.Equal(1, scanDoc.RootElement.GetProperty("version").GetInt32());
            var annotations = scanDoc.RootElement.GetProperty("annotations").EnumerateArray().ToList();
            // WALL 层两条线 → LayerInference Wall ×2（0.8 高于几何推断 0.6，不被降级）
            Assert.Equal(2, annotations.Count(a => a.GetProperty("class").GetString() == "Wall"));
            // 轴号圆 + 数字文本 → GeometryInference Axis ×2
            Assert.Equal(2, annotations.Count(a => a.GetProperty("class").GetString() == "Axis"));
            var scanAmbiguous = scanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
                .Select(a => a.GetProperty("layerName").GetString()).ToList();
            Assert.Contains("W-自定义", scanAmbiguous);
        }

        var (mapCode, _, _) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(0, mapCode);

        var (_, showOut, _) = Run("semantic", "show");
        Assert.Contains("HumanConfirmed", showOut);

        var (rescanCode, rescanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, rescanCode);
        using var rescanDoc = System.Text.Json.JsonDocument.Parse(rescanOut);
        Assert.Equal(3, rescanDoc.RootElement.GetProperty("version").GetInt32()); // scan=1, map=2, rescan=3（Save 每次+1）
        var ambiguous = rescanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
            .Select(a => a.GetProperty("layerName").GetString()).ToList();
        Assert.DoesNotContain("W-自定义", ambiguous);
        // 映射保留：rescan 后自定义层实体仍是 HumanConfirmed Wall（1.0）
        var rescanAnnotations = rescanDoc.RootElement.GetProperty("annotations").EnumerateArray().ToList();
        Assert.Contains(rescanAnnotations, a =>
            a.GetProperty("class").GetString() == "Wall" &&
            a.GetProperty("provenance").GetString() == "HumanConfirmed" &&
            a.GetProperty("confidence").GetDouble() == 1.0);

        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);

        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, runCode);
        Assert.Contains("\"ok\":true", runOut.Replace(" ", ""));
    }

    // ---- BF-T3：scan stdout 块级加性披露 + 审计 params 加性 + --min-block-instances ----

    /// <summary>临时 skill 包（镜像 BlockSymbolMatcherTests._pkg）——放 _project 下，Dispose 递归删除覆盖。</summary>
    private string CreateSymbolPackage()
    {
        string pkg = Path.Combine(_project, "blockskill");
        Directory.CreateDirectory(Path.Combine(pkg, "symbols"));
        File.WriteAllText(Path.Combine(pkg, "skill.yaml"), "name: test-pkg\nversion: \"1.0.0\"\n");
        File.WriteAllText(Path.Combine(pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        var sym = new CadDocument();
        sym.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        sym.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(pkg, "symbols", "sym.dxf")))
            DxfWriter.Write(fs, sym);
        return pkg;
    }

    private List<CadAgent.Core.AuditRecord> AuditRecords()
    {
        using var audit = new CadAgent.Core.SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        return audit.ReadAll().ToList();
    }

    [Fact]
    public void Scan_StdoutBlockKeys_AlwaysPresent_Additive()
    {
        // 无 --skill：stdout 四加性键恒在（零/空）；旧键 version/annotations/ambiguous/sheets 逐字仍在。
        var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("blockAutoSeeded").GetInt32());
        Assert.Empty(root.GetProperty("blockSuggestions").EnumerateArray());
        Assert.Empty(root.GetProperty("blockAmbiguous").EnumerateArray());
        Assert.Equal(0, root.GetProperty("symbolsSkipped").GetInt32());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(root.TryGetProperty("annotations", out var ann) && ann.ValueKind == JsonValueKind.Array);
        Assert.True(root.TryGetProperty("ambiguous", out var amb) && amb.ValueKind == JsonValueKind.Array);
        Assert.True(root.TryGetProperty("sheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array);
        // 审计 params 加性四键同样恒在（无 --skill → 零）。
        var rec = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal(0, p.RootElement.GetProperty("blockAutoSeeded").GetInt32());
        Assert.Equal(0, p.RootElement.GetProperty("blockSuggestionCount").GetInt32());
        Assert.Equal(0, p.RootElement.GetProperty("blockAmbiguousCount").GetInt32());
        Assert.Equal(0, p.RootElement.GetProperty("symbolsSkipped").GetInt32());
    }

    [Fact]
    public void Scan_WithSkill_BlockSuggestionDisclosed_AndAuditParamsAdditive()
    {
        // 近似块（线端点 12 vs 符号 10 → d≈0.065）×4 实例 → 只建议不自动；confidence<0.8。
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            var br = new BlockRecord("近似块");
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(12, 0, 0) });
            br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
            doc.BlockRecords.Add(br);
            for (int i = 0; i < 4; i++)
                doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 1000, 0, 0) });
        });
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal(0, doc.RootElement.GetProperty("blockAutoSeeded").GetInt32());
            var s = Assert.Single(doc.RootElement.GetProperty("blockSuggestions").EnumerateArray());
            Assert.Equal("近似块", s.GetProperty("block").GetString());
            Assert.Equal(4, s.GetProperty("instances").GetInt32());
            var c = Assert.Single(s.GetProperty("candidates").EnumerateArray());
            Assert.Equal("测试符号", c.GetProperty("symbol").GetString());
            Assert.InRange(c.GetProperty("distance").GetDouble(), 0.001, 0.15);
            Assert.True(c.GetProperty("confidence").GetDouble() < 0.8);
            var rec = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
            Assert.True(p.RootElement.GetProperty("blockSuggestionCount").GetInt32() >= 1);
            Assert.Equal(0, p.RootElement.GetProperty("blockAutoSeeded").GetInt32());
            Assert.Equal(0, p.RootElement.GetProperty("blockAmbiguousCount").GetInt32());
            Assert.Equal(0, p.RootElement.GetProperty("symbolsSkipped").GetInt32());
        }
        finally { File.Delete(blockDxf); }
    }

    [Fact]
    public void Scan_MinBlockInstances_InvalidValue_Exit1BeforeSideEffects()
    {
        // 非法值 → exit 1 + stderr，先于副作用（不建 audit.db、不落 overlay）——镜像 --max-tiles 先例。
        var (code, _, stderr) = Run("semantic", "scan", "--drawing", _drawing, "--min-block-instances", "abc");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Contains("--min-block-instances", stderr);
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        // 非正整数同样非法（正整数口径）。
        var (code0, _, stderr0) = Run("semantic", "scan", "--drawing", _drawing, "--min-block-instances", "0");
        Assert.Equal(1, code0);
        Assert.NotEmpty(stderr0);
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
    }

    [Fact]
    public void Scan_Rescan_ClearsStaleBlockSeeds_KeepsHumanConfirmed()
    {
        // scan（精确种子 ×4）→ map DEV-A（2 实例升 HumanConfirmed）→ 改块定义 SaveWorking
        // → rescan：陈旧块种子清（marker 范围）+ HumanConfirmed 保留 + version 递增。
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("DEV-A"));
            doc.Layers.Add(new Layer("DEV-B"));
            var a = doc.Layers.First(l => l.Name == "DEV-A");
            var b = doc.Layers.First(l => l.Name == "DEV-B");
            var br = new BlockRecord("符号块");
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
            br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
            doc.BlockRecords.Add(br);
            for (int i = 0; i < 2; i++)
                doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 1000, 0, 0), Layer = a });
            for (int i = 2; i < 4; i++)
                doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 1000, 0, 0), Layer = b });
        });
        try
        {
            var (c1, out1, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, c1);
            using (var d1 = JsonDocument.Parse(out1))
                Assert.Equal(4, d1.RootElement.GetProperty("blockAutoSeeded").GetInt32());
            // fix wave（测试小缺口）：精确夹具下审计 params blockAutoSeeded>0（=4 实例）——
            // stdout/审计双通道计数一致，防审计侧空过。
            var rec1 = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
            using (var p1 = JsonDocument.Parse(rec1.Entry.ParamsJson))
                Assert.Equal(4, p1.RootElement.GetProperty("blockAutoSeeded").GetInt32());
            Assert.Equal(0, Run("semantic", "map", "--layer", "DEV-A", "--class", "Lighting", "--drawing", blockDxf).code);
            // 块定义改走样（加对角线 → 指纹漂移）；.dxf 的 SaveWorking 即写回源路径。
            var session = DrawingSession.Load(blockDxf);
            var block = session.Doc.BlockRecords.First(b => b.Name == "符号块");
            block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(50, 50, 0) });
            session.SaveWorking();
            var (c3, out3, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, c3);
            using var d3 = JsonDocument.Parse(out3);
            Assert.Equal(3, d3.RootElement.GetProperty("version").GetInt32()); // scan=1, map=2, rescan=3
            Assert.Equal(0, d3.RootElement.GetProperty("blockAutoSeeded").GetInt32());
            var annotations = d3.RootElement.GetProperty("annotations").EnumerateArray().ToList();
            Assert.DoesNotContain(annotations, x => x.GetProperty("evidence").GetString()!.StartsWith("几何指纹精确匹配:"));
            Assert.Equal(2, annotations.Count(x => x.GetProperty("provenance").GetString() == "HumanConfirmed"
                && x.GetProperty("class").GetString() == "Lighting"));
        }
        finally { File.Delete(blockDxf); }
    }

    [Fact]
    public void Scan_BlockAmbiguous_GuidanceForProxyReason_NullOtherwise()
    {
        // 零白名单几何块（仅 TEXT，与天正代理同构）→ "代理图形不可读" + guidance 后缀；
        // 无匹配块 → "无匹配" + guidance null（加性，JSON 含 null）。
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            var proxy = new BlockRecord("$equip$0001");
            proxy.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Value = "代理", Height = 2.5 });
            doc.BlockRecords.Add(proxy);
            var custom = new BlockRecord("自定义块");
            custom.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            custom.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            custom.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
            doc.BlockRecords.Add(custom);
            for (int i = 0; i < 4; i++)
            {
                doc.Entities.Add(new Insert(proxy) { InsertPoint = new XYZ(i * 1000, 0, 0) });
                doc.Entities.Add(new Insert(custom) { InsertPoint = new XYZ(i * 1000, 500, 0) });
            }
        });
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout);
            var amb = doc.RootElement.GetProperty("blockAmbiguous").EnumerateArray().ToList();
            Assert.Equal(2, amb.Count);
            var proxyItem = amb.First(x => x.GetProperty("block").GetString() == "$equip$0001");
            Assert.Equal("代理图形不可读", proxyItem.GetProperty("reason").GetString());
            Assert.Equal("建议天正 T3 转换后重扫", proxyItem.GetProperty("guidance").GetString());
            Assert.Equal(4, proxyItem.GetProperty("instances").GetInt32());
            Assert.NotEmpty(proxyItem.GetProperty("sampleHandles").EnumerateArray());
            var customItem = amb.First(x => x.GetProperty("block").GetString() == "自定义块");
            Assert.Equal("无匹配", customItem.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, customItem.GetProperty("guidance").ValueKind);
        }
        finally { File.Delete(blockDxf); }
    }

    /// <summary>
    /// M2（--min-block-instances 合法非默认值传播）：CLI(2)→Scanner→Matcher 全链钉住——
    /// 2 实例无匹配块默认 min=3 不歧义；--min-block-instances 2 → 入歧义（instances=2）+
    /// 审计 blockAmbiguousCount 双通道一致。纯测试新增（产品传播链已存在，无需微调）。
    /// </summary>
    [Fact]
    public void Scan_MinBlockInstancesTwo_PropagatesToMatcher()
    {
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            var br = new BlockRecord("边界块");
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
            doc.BlockRecords.Add(br);
            for (int i = 0; i < 2; i++)
                doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 1000, 0, 0) });
        });
        try
        {
            // 默认 min=3：2 实例无匹配块不歧义
            var (c1, out1, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, c1);
            using (var d1 = JsonDocument.Parse(out1))
                Assert.Empty(d1.RootElement.GetProperty("blockAmbiguous").EnumerateArray());
            // --min-block-instances 2：传播至 Matcher → 2 实例入歧义
            var (c2, out2, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg, "--min-block-instances", "2");
            Assert.Equal(0, c2);
            using var d2 = JsonDocument.Parse(out2);
            var a = Assert.Single(d2.RootElement.GetProperty("blockAmbiguous").EnumerateArray());
            Assert.Equal("边界块", a.GetProperty("block").GetString());
            Assert.Equal(2, a.GetProperty("instances").GetInt32());
            Assert.Equal("无匹配", a.GetProperty("reason").GetString());
            // 审计双通道：第二次 scan params blockAmbiguousCount=1
            var recs = AuditRecords().Where(r => r.Entry.Kind == "semantic-scan").ToList();
            Assert.Equal(2, recs.Count);
            using var p2 = JsonDocument.Parse(recs[1].Entry.ParamsJson);
            Assert.Equal(1, p2.RootElement.GetProperty("blockAmbiguousCount").GetInt32());
        }
        finally { File.Delete(blockDxf); }
    }

    // ---- R9②③ 诊断披露补全（spec §13.2 歧义诊断字段 / §13.4 NaN 剔除披露）----

    /// <summary>
    /// ③ nonFiniteDropped 恒在（无 --skill → 0）：stdout 加性键 + 审计 semantic-scan
    /// params 加性键双通道钉住（镜像 blockAutoSeeded 恒在先例）。
    /// </summary>
    [Fact]
    public void Scan_StdoutAndAudit_NonFiniteDroppedAlwaysPresent_ZeroWithoutSkill()
    {
        var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("nonFiniteDropped").GetInt32());
        var rec = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal(0, p.RootElement.GetProperty("nonFiniteDropped").GetInt32());
    }

    /// <summary>
    /// ② blockAmbiguous 项加性 histogram/bbox：无匹配块直方图全量类型计数（Type Ordinal
    /// 排序）+ bbox 数值钉住；代理块（仅 TEXT）histogram 仍披露、bbox=插入点退化口径
    /// （PointsOf 复用：TextEntity 插入点参与）。**ValueTuple 序列化回归钉住**：
    /// histogram[0] 必须是非空对象且 type 非空——ValueTuple 字段非属性会退化为 {}。
    /// 旧字段 block/instances/reason/sampleHandles/guidance 逐字 shape pin 保持。
    /// </summary>
    [Fact]
    public void Scan_BlockAmbiguous_HistogramBboxDisclosed_OldKeysShapePinned()
    {
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            var proxy = new BlockRecord("$equip$0001");
            proxy.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Value = "代理", Height = 2.5 });
            doc.BlockRecords.Add(proxy);
            var custom = new BlockRecord("自定义块");
            custom.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            custom.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            custom.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
            doc.BlockRecords.Add(custom);
            for (int i = 0; i < 4; i++)
            {
                doc.Entities.Add(new Insert(proxy) { InsertPoint = new XYZ(i * 1000, 0, 0) });
                doc.Entities.Add(new Insert(custom) { InsertPoint = new XYZ(i * 1000, 500, 0) });
            }
        });
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout);
            var amb = doc.RootElement.GetProperty("blockAmbiguous").EnumerateArray().ToList();
            Assert.Equal(2, amb.Count);
            var customItem = amb.First(x => x.GetProperty("block").GetString() == "自定义块");
            // 旧键 shape pin（逐字不变）
            Assert.Equal("无匹配", customItem.GetProperty("reason").GetString());
            Assert.Equal(4, customItem.GetProperty("instances").GetInt32());
            Assert.NotEmpty(customItem.GetProperty("sampleHandles").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, customItem.GetProperty("guidance").ValueKind);
            // histogram：3 Line（类型计数，Ordinal 排序）；首项必须是非空对象（防 ValueTuple {} 退化）
            var hist = customItem.GetProperty("histogram").EnumerateArray().ToList();
            var h0 = Assert.Single(hist);
            Assert.Equal(JsonValueKind.Object, h0.ValueKind);
            Assert.Equal("Line", h0.GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(h0.GetProperty("type").GetString()));
            Assert.Equal(3, h0.GetProperty("count").GetInt32());
            // bbox：三角形 (0,0)-(1,5)-(3,1) → min(0,0) max(3,5)
            var bbox = customItem.GetProperty("bbox");
            Assert.Equal(JsonValueKind.Object, bbox.ValueKind);
            Assert.Equal(0, bbox.GetProperty("minX").GetDouble());
            Assert.Equal(0, bbox.GetProperty("minY").GetDouble());
            Assert.Equal(3, bbox.GetProperty("maxX").GetDouble());
            Assert.Equal(5, bbox.GetProperty("maxY").GetDouble());
            // 代理块：histogram 全量（TextEntity 非白名单仍披露）；bbox=插入点退化（PointsOf 口径）
            var proxyItem = amb.First(x => x.GetProperty("block").GetString() == "$equip$0001");
            Assert.Equal("建议天正 T3 转换后重扫", proxyItem.GetProperty("guidance").GetString());
            var ph = Assert.Single(proxyItem.GetProperty("histogram").EnumerateArray());
            Assert.Equal("TextEntity", ph.GetProperty("type").GetString());
            Assert.Equal(1, ph.GetProperty("count").GetInt32());
            var pb = proxyItem.GetProperty("bbox");
            Assert.Equal(0, pb.GetProperty("minX").GetDouble());
            Assert.Equal(0, pb.GetProperty("maxX").GetDouble());
            // 本夹具全有限 → nonFiniteDropped=0（恒在键）
            Assert.Equal(0, doc.RootElement.GetProperty("nonFiniteDropped").GetInt32());
        }
        finally { File.Delete(blockDxf); }
    }

    /// <summary>
    /// ③ NaN 实体剔除披露全链：块定义含 1 NaN 线（DxfWriter/DxfReader 往返实证保 NaN）
    /// → stdout nonFiniteDropped=1 + 审计 params nonFiniteDropped=1 双通道一致；
    /// histogram 全量口径（NaN 线仍计数 Line:4）与指纹剔除口径（NaN 不入哈希/bbox）区分钉住。
    /// </summary>
    [Fact]
    public void Scan_NaNBlockEntity_NonFiniteDroppedStdoutAndAudit()
    {
        string pkg = CreateSymbolPackage();
        string blockDxf = DxfFixture.CreateTempDxf(doc =>
        {
            var br = new BlockRecord("NaN块");
            br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 5, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(1, 5, 0), EndPoint = new XYZ(3, 1, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(3, 1, 0), EndPoint = new XYZ(0, 0, 0) });
            br.Entities.Add(new Line { StartPoint = new XYZ(double.NaN, 0, 0), EndPoint = new XYZ(9, 9, 0) });
            doc.BlockRecords.Add(br);
            for (int i = 0; i < 4; i++)
                doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 1000, 0, 0) });
        });
        try
        {
            var (code, stdout, _) = Run("semantic", "scan", "--drawing", blockDxf, "--skill", pkg);
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal(1, doc.RootElement.GetProperty("nonFiniteDropped").GetInt32());
            var a = Assert.Single(doc.RootElement.GetProperty("blockAmbiguous").EnumerateArray());
            Assert.Equal("NaN块", a.GetProperty("block").GetString());
            Assert.Equal("无匹配", a.GetProperty("reason").GetString());
            // histogram 全量：NaN 线仍计数（Line:4）；bbox 仅有限点（三角形 0,0→3,5）
            var h = Assert.Single(a.GetProperty("histogram").EnumerateArray());
            Assert.Equal("Line", h.GetProperty("type").GetString());
            Assert.Equal(4, h.GetProperty("count").GetInt32());
            Assert.Equal(3, a.GetProperty("bbox").GetProperty("maxX").GetDouble());
            Assert.Equal(5, a.GetProperty("bbox").GetProperty("maxY").GetDouble());
            var rec = Assert.Single(AuditRecords(), r => r.Entry.Kind == "semantic-scan");
            using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
            Assert.Equal(1, p.RootElement.GetProperty("nonFiniteDropped").GetInt32());
        }
        finally { File.Delete(blockDxf); }
    }
}
