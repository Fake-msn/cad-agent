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
}
