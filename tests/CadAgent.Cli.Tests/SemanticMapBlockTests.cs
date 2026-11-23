using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadAgent.Core;
using CSMath;

namespace CadAgent.Cli.Tests;

/// <summary>
/// BF-T4：semantic map-block 人工确认命令（spec §13.3）——复核制（重提取指纹，
/// 精确或 d≤0.15 否则拒绝留痕）→ HumanConfirmed 1.0 落该块全部 walked Insert 实例；
/// overlay-only before==after；不调 GateDetector（Yellow 豁免沿用 SemanticMap 先例）。
/// 夹具 API 按现状定型（镜像 BlockSymbolMatcherTests）：点=CSMath.XYZ；DxfWriter.Write 静态；
/// symbols.yaml 根键 symbols:；BlockRecord(name)/Insert(br) 构造（ACadSharp 3.8.35）。
/// </summary>
public class SemanticMapBlockTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"mapblock-{Guid.NewGuid():N}");
    private readonly string _pkg = Path.Combine(Path.GetTempPath(), $"mapblockpkg-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public SemanticMapBlockTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(Path.Combine(_pkg, "symbols"));
        File.WriteAllText(Path.Combine(_pkg, "skill.yaml"), "name: test-pkg\nversion: \"1.0.0\"\n");
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        // 符号 DXF：线 (0,0)-(10,0) + 圆 (5,3) r2（与块定义同几何→精确命中）
        var sym = new CadDocument();
        sym.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        sym.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "sym.dxf")))
            DxfWriter.Write(fs, sym);
        // 图纸：块定义同几何 + 3 个顶层 Insert 实例
        var doc = new CadDocument();
        var br = new BlockRecord("符号块");
        br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        br.Entities.Add(new Circle { Center = new XYZ(5, 3, 0), Radius = 2 });
        doc.BlockRecords.Add(br);
        for (int i = 0; i < 3; i++) doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 100, 0, 0) });
        _drawing = Path.Combine(_project, "d.dxf");
        using (var fs = File.Create(_drawing)) DxfWriter.Write(fs, doc);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_project, true); } catch (IOException) { }
        try { Directory.Delete(_pkg, true); } catch (IOException) { }
    }

    private int Run(params string[] extra)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "semantic", "map-block", "--drawing", _drawing,
            "--skill", _pkg }.Concat(extra).Append("--project").Append(_project).ToArray(), outW, errW);
        LastStdout = outW.ToString(); LastStderr = errW.ToString();
        return code;
    }
    private string LastStdout = "", LastStderr = "";

    /// <summary>读回 _drawing → 改块定义 → DxfWriter 重写（镜像 DxfFixture 读回改写模式）。</summary>
    private void RewriteBlock(Action<BlockRecord> mutate)
    {
        CadDocument doc;
        using (var fs = File.OpenRead(_drawing)) doc = new DxfReader(fs).Read();
        var br = doc.BlockRecords.First(b => b.Name.Equals("符号块", StringComparison.OrdinalIgnoreCase));
        mutate(br);
        using (var fs = File.Create(_drawing)) DxfWriter.Write(fs, doc);
    }

    [Fact]
    public void MapBlock_ExactMatch_HumanConfirmedAllInstances_AuditChain()
    {
        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(LastStdout);
        Assert.True(doc.RootElement.GetProperty("exact").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("instances").GetInt32());
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Equal(3, overlay.Annotations.Count(a => a.Provenance == Provenance.HumanConfirmed));
        Assert.All(overlay.Annotations, a => { Assert.Equal(1.0, a.Confidence); Assert.Equal(SemanticClass.Device, a.Class); Assert.StartsWith("人工确认块映射:", a.Evidence); });
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block");
        Assert.Equal(rec.Entry.BeforeHash, rec.Entry.AfterHash);
        Assert.True(audit.VerifyChain(out _));
        // 语义闸：HumanConfirmed 1.0 过 minConfidence 0.8
        Assert.All(overlay.Annotations, a => Assert.True(a.Confidence >= 0.8));
    }

    [Fact]
    public void MapBlock_NearMatch_WithinThreshold_Confirms()
    {
        // 改块定义微扰（线端点 10→10.5，d≈0.019≤0.15）→ 确认成功 exact=false distance>0
        RewriteBlock(br =>
        {
            var line = br.Entities.OfType<Line>().First();
            line.EndPoint = new XYZ(10.5, 0, 0);
        });
        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(0, code);
        using var j = JsonDocument.Parse(LastStdout);
        Assert.False(j.RootElement.GetProperty("exact").GetBoolean());
        Assert.InRange(j.RootElement.GetProperty("distance").GetDouble(), 0.001, 0.15);
    }

    [Fact]
    public void MapBlock_FingerprintMismatch_RejectedAudited_Exit1()
    {
        // 块定义改走样（加对角线→线计数 2≠1 结构签名不兼容 d=1）→ exit 1 +
        // semantic-map-block-rejected reason=指纹不符 + overlay 无 HumanConfirmed
        RewriteBlock(br => br.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 5, 0) }));
        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(1, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal("指纹不符", p.RootElement.GetProperty("reason").GetString());
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.DoesNotContain(overlay.Annotations, a => a.Provenance == Provenance.HumanConfirmed);
    }

    [Fact]
    public void MapBlock_UnknownSymbol_Rejected_Exit1()
    {
        int code = Run("--block", "符号块", "--symbol", "不存在");
        Assert.Equal(1, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
    }

    /// <summary>
    /// fix wave（Important#1）：非法 --skill 目录→用法错误先于任何副作用——
    /// 异常经 Run 兜底 exit 1，audit.db 不创建、overlay 不落
    /// （镜像 scan --min-block-instances 先例测试模式）。
    /// </summary>
    [Fact]
    public void MapBlock_InvalidSkillDir_Exit1_BeforeAnySideEffect()
    {
        string bogus = Path.Combine(_project, "no-such-skill");
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "semantic", "map-block", "--drawing", _drawing,
            "--block", "符号块", "--symbol", "测试符号", "--skill", bogus,
            "--project", _project }, outW, errW);
        Assert.Equal(1, code);
        Assert.NotEmpty(errW.ToString());
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "audit.db")));
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
    }

    /// <summary>
    /// fix wave（测试小缺口）：无 --skill→空包列表→符号不存在 reason 留痕 exit 1
    /// （用法合法故走 OpenAudit 后拒绝面，与非法包零副作用路径区分）。
    /// </summary>
    [Fact]
    public void MapBlock_NoSkill_EmptyPackages_SymbolNotFound_RejectedAudited()
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "semantic", "map-block", "--drawing", _drawing,
            "--block", "符号块", "--symbol", "测试符号", "--project", _project }, outW, errW);
        Assert.Equal(1, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal("符号不存在", p.RootElement.GetProperty("reason").GetString());
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void MapBlock_UnknownBlock_Rejected_Exit1()
    {
        int code = Run("--block", "不存在块", "--symbol", "测试符号");
        Assert.Equal(1, code);
        // T5 收紧（controller sanctioned）：拒绝必须留痕 kind+reason，不止 exit code
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal("块不存在", p.RootElement.GetProperty("reason").GetString());
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void MapBlock_InvalidClass_Rejected_Exit1()
    {
        int code = Run("--block", "符号块", "--symbol", "测试符号", "--class", "NotAClass");
        Assert.Equal(1, code);
        // T5 收紧（controller sanctioned）：class 非法拒绝面同样 kind+reason 留痕
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal("class 非法", p.RootElement.GetProperty("reason").GetString());
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void MapBlock_ClassOverride_Applied()
    {
        int code = Run("--block", "符号块", "--symbol", "测试符号", "--class", "Lighting");
        Assert.Equal(0, code);
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.All(overlay.Annotations, a => Assert.Equal(SemanticClass.Lighting, a.Class));
    }

    [Fact]
    public void MapBlock_NoGateInvocation_OverlayOnly_YellowExemptParity()
    {
        // 成功跑后审计无 run-rejected/intake-rejected（map-block 不走闸）；drawing 文件字节前后一致
        byte[] before = File.ReadAllBytes(_drawing);
        Assert.Equal(0, Run("--block", "符号块", "--symbol", "测试符号"));
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind is "run-rejected" or "intake-rejected");
    }

    [Fact]
    public void MapBlock_RescanAfterConfirm_HumanConfirmedPreserved()
    {
        Assert.Equal(0, Run("--block", "符号块", "--symbol", "测试符号"));
        var outW = new StringWriter(); var errW = new StringWriter();
        // T5 收紧（controller sanctioned）：re-scan 自身必须 exit 0（此前丢弃退出码）
        int scanCode = CommandRouter.Run(new[] { "semantic", "scan", "--drawing", _drawing, "--skill", _pkg,
            "--project", _project }, outW, errW);
        Assert.Equal(0, scanCode);
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Equal(3, overlay.Annotations.Count(a => a.Provenance == Provenance.HumanConfirmed));
    }

    /// <summary>
    /// T5 补测（controller sanctioned）：块中块嵌套实例——外层块定义内含符号块 Insert，
    /// 顶层仅 Insert 外层块。map-block 确认符号块→HumanConfirmed 必须落嵌套后缀句柄
    /// "{parent}:{i}"（BlockWalker 同 convention），不止顶层实例。
    /// </summary>
    [Fact]
    public void MapBlock_NestedInstance_ConfirmsSuffixHandle_HumanConfirmed()
    {
        // 读回图纸→加外层块（内含符号块 Insert）+顶层 Insert 外层块→重写（DxfFixture 读回改写模式）
        CadDocument doc;
        using (var fs = File.OpenRead(_drawing)) doc = new DxfReader(fs).Read();
        var inner = doc.BlockRecords.First(b => b.Name.Equals("符号块", StringComparison.OrdinalIgnoreCase));
        var outer = new BlockRecord("外层块");
        outer.Entities.Add(new Insert(inner) { InsertPoint = new XYZ(5, 5, 0) });
        doc.BlockRecords.Add(outer);
        doc.Entities.Add(new Insert(outer) { InsertPoint = new XYZ(500, 0, 0) });
        using (var fs = File.Create(_drawing)) DxfWriter.Write(fs, doc);

        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(0, code);
        using var j = JsonDocument.Parse(LastStdout);
        // 3 顶层 + 1 嵌套（外层块内 Insert）= 4 实例
        Assert.Equal(4, j.RootElement.GetProperty("instances").GetInt32());
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        var confirmed = overlay.Annotations.Where(a => a.Provenance == Provenance.HumanConfirmed).ToList();
        Assert.Equal(4, confirmed.Count);
        // 嵌套实例落后缀句柄（含 ':'），且恰 1 条（外层块内仅 1 个符号块 Insert）
        var nested = Assert.Single(confirmed, a => a.TargetHandle.Contains(':'));
        Assert.Equal(1.0, nested.Confidence);
        Assert.Equal(SemanticClass.Device, nested.Class);
        Assert.StartsWith("人工确认块映射:", nested.Evidence);
        // 后缀句柄前缀=顶层外层 Insert 句柄（读回核定，不赌写盘句柄值）
        using var fs2 = File.OpenRead(_drawing);
        var reread = new DxfReader(fs2).Read();
        var outerInsert = reread.Entities.OfType<Insert>().First(i =>
            i.Block?.Name.Equals("外层块", StringComparison.OrdinalIgnoreCase) == true);
        Assert.StartsWith(outerInsert.Handle.ToString() + ":", nested.TargetHandle);
    }

    // ---- PR#21 合并前 ISTQB 修复波 2（I1 安全关键拒绝面 + M5 阈值边界，纯测试新增）----

    /// <summary>
    /// I1①（块无几何拒绝面）：块定义仅 TEXT/Hatch（零白名单几何）+ 合法符号 →
    /// Extract null → exit 1 + 审计 semantic-map-block-rejected reason="块无几何" + VerifyChain。
    /// Hatch 矩形边界路径夹具镜像 MoveEntityTests（DxfWriter 往返已证）；路径边 Line 为
    /// Hatch 子实体不入块顶层 Entities → Extract 仍零白名单几何。
    /// </summary>
    [Fact]
    public void MapBlock_BlockNoGeometry_RejectedAudited_Exit1()
    {
        // 重建图纸：块定义仅 TEXT + Hatch（覆盖构造夹具的线圆几何）
        var doc = new CadDocument();
        var br = new BlockRecord("符号块");
        br.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Height = 2.5, Value = "注记" });
        var h = new Hatch { IsSolid = true };
        h.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) }),
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(10, 0, 0), EndPoint = new XYZ(10, 10, 0) }),
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(10, 10, 0), EndPoint = new XYZ(0, 10, 0) }),
            new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(0, 0, 0) }),
        }));
        br.Entities.Add(h);
        doc.BlockRecords.Add(br);
        for (int i = 0; i < 3; i++) doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 100, 0, 0) });
        using (var fs = File.Create(_drawing)) DxfWriter.Write(fs, doc);

        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(1, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
        using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
        Assert.Equal("块无几何", p.RootElement.GetProperty("reason").GetString());
        Assert.True(audit.VerifyChain(out _));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block"); // 无成功记录
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json"))); // 拒绝先于 overlay 写入
    }

    /// <summary>
    /// I1②（符号无几何拒绝面）：符号 DXF 损坏（垃圾文本 → DxfReader 抛 → catch 分支）
    /// 与仅 TEXT（合法 DXF → Extract null 分支）双分支 → reason="符号无几何" + VerifyChain。
    /// 两次拒绝各留痕一条，块定义合法（构造夹具线圆）。
    /// </summary>
    [Fact]
    public void MapBlock_SymbolNoGeometry_RejectedAudited_Exit1()
    {
        string symPath = Path.Combine(_pkg, "symbols", "sym.dxf");
        // 分支一：垃圾文本（DxfReader 抛 → catch → 符号无几何）
        File.WriteAllText(symPath, "不是 DXF");
        Assert.Equal(1, Run("--block", "符号块", "--symbol", "测试符号"));
        // 分支二：仅 TEXT 合法 DXF（Extract null → 符号无几何）
        var textOnly = new CadDocument();
        textOnly.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Height = 2.5, Value = "仅注记" });
        using (var fs = File.Create(symPath)) DxfWriter.Write(fs, textOnly);
        Assert.Equal(1, Run("--block", "符号块", "--symbol", "测试符号"));

        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rejects = audit.ReadAll().Where(r => r.Entry.Kind == "semantic-map-block-rejected").ToList();
        Assert.Equal(2, rejects.Count);
        Assert.All(rejects, rec =>
        {
            using var p = JsonDocument.Parse(rec.Entry.ParamsJson);
            Assert.Equal("符号无几何", p.RootElement.GetProperty("reason").GetString());
        });
        Assert.True(audit.VerifyChain(out _));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block");
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
    }

    /// <summary>
    /// M5（阈值边界 CLI 侧）：map-block 复核 d 恰=0.15 接受（&lt;= 口径）→ exit 0 +
    /// exact=false + distance 精确 0.15 + HumanConfirmed 落 3 实例。双圆夹具同
    /// BlockFingerprintTests.Distance_ExactlyAtNearThreshold_ValuePinned（内圆平移 3 → d=0.3/2）。
    /// </summary>
    [Fact]
    public void MapBlock_DistanceExactlyAtThreshold_Accepted()
    {
        // 块：C1(5,5)r5 + C2(6,5)r1；符号：C1(5,5)r5 + C2(3,5)r1（bbox 同 10×10）
        var doc = new CadDocument();
        var br = new BlockRecord("符号块");
        br.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 5 });
        br.Entities.Add(new Circle { Center = new XYZ(6, 5, 0), Radius = 1 });
        doc.BlockRecords.Add(br);
        for (int i = 0; i < 3; i++) doc.Entities.Add(new Insert(br) { InsertPoint = new XYZ(i * 100, 0, 0) });
        using (var fs = File.Create(_drawing)) DxfWriter.Write(fs, doc);
        var sym = new CadDocument();
        sym.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 5 });
        sym.Entities.Add(new Circle { Center = new XYZ(3, 5, 0), Radius = 1 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "sym.dxf"))) DxfWriter.Write(fs, sym);

        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(0, code); // 恰阈值接受
        using var j = JsonDocument.Parse(LastStdout);
        Assert.False(j.RootElement.GetProperty("exact").GetBoolean());
        Assert.Equal(0.15, j.RootElement.GetProperty("distance").GetDouble()); // 精确双精度钉住
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Equal(3, overlay.Annotations.Count(a => a.Provenance == Provenance.HumanConfirmed));
    }
}
