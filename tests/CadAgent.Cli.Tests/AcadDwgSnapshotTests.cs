using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CSMath;

namespace CadAgent.Cli.Tests;

/// <summary>
/// DWG 快照路径（解除 acad fail-closed 停用，用户批准设计）：acad + working 派生 DXF →
/// 先转换 DWG 快照（DxfReader→DwgWriter 钉 AC1027）→ COM Open 喂 DWG；转换成功放行，
/// 转换失败仍 fail-closed 拒绝+审计+guidance（安全侧保持）。zwcad 路径零改动（回归钉住）。
/// fake seam 单测（非真机）：ConvertAcadDwgSnapshot 注入失败钉住 fail-closed；真实转换器
/// （默认 seam）+ fake runtime 钉住快照=.dwg 后缀+AC1027 头+CJK 保真。
/// 静态 seam（RuntimeSelector/RuntimeFactory/ConvertAcadDwgSnapshot/WriteAcadDwgSnapshot）用完即还原（本程序集已禁并行）。
/// CJK 夹具口径：DxfWriter 在 AC1015/AC1018 写中文有损（??????，抽检实证），AC1027 保真——
/// 故 CJK working DXF 夹具钉 AC1027 写出（共享 DxfFixture 不动，本类自带夹具）。
/// </summary>
public class AcadDwgSnapshotTests : IDisposable
{
    private const string Cjk = "中文标注测试";
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"acadsnap-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _plan;
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;
    private readonly Func<ICadRuntime?>? _originalFactory = CommandRouter.RuntimeFactory;
    private readonly Func<string, string> _originalConverter = CommandRouter.ConvertAcadDwgSnapshot;
    private readonly Action<Stream, CadDocument> _originalWriter = CommandRouter.WriteAcadDwgSnapshot;

    public AcadDwgSnapshotTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = CreateCjkWorkingDxf();
        _plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(_plan,
            "{\"planId\":\"SNAP-1\",\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}}]}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeSelector = _originalSelector;
        CommandRouter.RuntimeFactory = _originalFactory;
        CommandRouter.ConvertAcadDwgSnapshot = _originalConverter;
        CommandRouter.WriteAcadDwgSnapshot = _originalWriter;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
        if (File.Exists(_drawing + ".live.dwg")) File.Delete(_drawing + ".live.dwg");
    }

    /// <summary>CJK working DXF 夹具（钉 AC1027，DxfWriter 保 CJK；AC1015/AC1018 有损见类注）。</summary>
    private static string CreateCjkWorkingDxf()
    {
        var doc = new CadDocument();
        doc.Header.Version = ACadVersion.AC1027;
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 10), Height = 5, Value = Cjk });
        string path = Path.Combine(Path.GetTempPath(), $"acadsnap-cjk-{Guid.NewGuid():N}.dxf");
        using var fs = File.Create(path);
        DxfWriter.Write(fs, doc);
        return path;
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    /// <summary>转换失败必须先于运行时创建：工厂被触达即失败（钉住 fail-closed 不激活 COM）。</summary>
    private static ICadRuntime MustNotCreateRuntime() =>
        throw new InvalidOperationException("转换失败拒绝必须先于运行时创建（不激活 COM）");

    // ---- 成功路径：快照=.dwg 后缀 + AC1027 头 + CJK 保真 ----

    [Fact]
    public void Export_Acad_WorkingDxf_ConvertsDwgSnapshot_AC1027_CjkPreserved()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        var rt = new SnapshotRuntime();
        CommandRouter.RuntimeFactory = () => rt;
        var (code, stdout, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.True(code == 0, $"code={code} out={stdout} err={stderr}");
        // Open 喂的是 DWG 快照（.dwg 后缀），非 working DXF。
        Assert.NotNull(rt.OpenedPath);
        Assert.EndsWith(".dwg", rt.OpenedPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".working.dxf", rt.OpenedPath, StringComparison.OrdinalIgnoreCase);
        // DwgReader 读回快照头：钉 AC1027 + CJK 文本保真。
        Assert.Equal(ACadVersion.AC1027, rt.OpenedVersion);
        Assert.Equal(Cjk, rt.OpenedText);
        Assert.Contains("vendor", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-committed");
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "export-rejected");
        Assert.True(audit.VerifyChain(out _));
        // 临时快照用后即清（Open 路径所在目录已删）。
        Assert.False(Directory.Exists(Path.GetDirectoryName(rt.OpenedPath)));
    }

    [Fact]
    public void Run_Live_Acad_WorkingSnapshot_ConvertsDwg_AC1027_CjkPreserved()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        var rt = new SnapshotRuntime();
        CommandRouter.RuntimeFactory = () => rt;
        var (code, stdout, stderr) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.True(code == 0, $"code={code} out={stdout} err={stderr}");
        Assert.Contains(".live.dwg", stdout);
        Assert.NotNull(rt.OpenedPath);
        Assert.EndsWith(".dwg", rt.OpenedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ACadVersion.AC1027, rt.OpenedVersion);
        Assert.Equal(Cjk, rt.OpenedText);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-committed");
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "com-replay-failed");
        Assert.True(audit.VerifyChain(out _));
        Assert.False(Directory.Exists(Path.GetDirectoryName(rt.OpenedPath)));
    }

    // ---- 转换失败：fail-closed 拒绝 + 审计 + guidance + exit1（安全侧保持，先于 COM） ----

    [Fact]
    public void Export_Acad_ConversionFails_RefusedAudited_NoCom()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = MustNotCreateRuntime;
        CommandRouter.ConvertAcadDwgSnapshot = _ => throw new InvalidOperationException("fake DwgWriter 转换失败");
        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.Equal(1, code);
        Assert.Contains("0xE0000001", stderr);
        Assert.Contains("zwcad", stderr);
        Assert.Empty(Directory.GetFiles(_project, "*.dwg", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_project, "*.dxf", SearchOption.AllDirectories));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-rejected"
            && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_Live_Acad_ConversionFails_RefusedAudited_ShadowUnaffected()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = MustNotCreateRuntime;
        CommandRouter.ConvertAcadDwgSnapshot = _ => throw new InvalidOperationException("fake DwgWriter 转换失败");
        var (code, _, stderr) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.Equal(1, code);
        Assert.Contains("0xE0000001", stderr);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-failed"
            && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "com-replay-committed");
        Assert.True(audit.VerifyChain(out _));
    }

    // ---- I1（微修复波）：建目录后写出抛 → 转换器自清临时目录（无 %TEMP% 孤儿 GUID 目录） ----

    [Fact]
    public void Export_Acad_WriterThrowsAfterDirCreated_TempDirSelfCleaned_Refused()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = MustNotCreateRuntime;
        // 写出步 seam 注入失败：真实转换器已 CreateDirectory（且部分写出 .dwg）后抛——
        // 调用方拿不到快照路径（acadSnapDir 仍 null），清理责任在转换器内部。
        CommandRouter.WriteAcadDwgSnapshot = (fs, _) =>
        {
            fs.WriteByte(0x41); // 模拟部分写出（残留部分 .dwg 内容，钉住整目录自清）
            throw new InvalidOperationException("fake DwgWriter 建目录后抛");
        };
        string[] before = Directory.GetDirectories(Path.GetTempPath(), "cadagent-acad-snap-*");
        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.Equal(1, code); // fail-closed 保持（再上抛 → 拒绝+审计+guidance）
        Assert.Contains("0xE0000001", stderr);
        // I1 钉住：失败后无新增孤儿 GUID 临时目录（部分 .dwg 随整目录自清）。
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "cadagent-acad-snap-*").Except(before));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-rejected"
            && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        Assert.True(audit.VerifyChain(out _));
    }

    // ---- I2（微修复波）：working↔快照计数相等闸（转换丢实体 → 拒绝，保真锚不漂移） ----

    [Fact]
    public void Export_Acad_ConverterDropsEntities_CountMismatchRefused_NoCom()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = MustNotCreateRuntime;
        // fake 转换器丢实体：working 源 1 实体 → 返回空文档 DWG 快照（0 ≠ 1）。
        // ExportService sourceCount 以快照为锚——若无前置相等闸，双闸口（源==导出）看不见丢失。
        string snapDir = Path.Combine(Path.GetTempPath(), "cadagent-acad-snap-" + Guid.NewGuid().ToString("N"));
        CommandRouter.ConvertAcadDwgSnapshot = _ =>
        {
            Directory.CreateDirectory(snapDir);
            string p = Path.Combine(snapDir, "dropped.dwg");
            var empty = new CadDocument();
            empty.Header.Version = ACadVersion.AC1027;
            using var fs = File.Create(p);
            DwgWriter.Write(fs, empty);
            return p;
        };
        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.Equal(1, code);
        Assert.Contains("实体数", stderr); // guidance 披露计数不等
        Assert.False(Directory.Exists(snapDir)); // 拒绝路径临时快照亦清
        Assert.Empty(Directory.GetFiles(_project, "*.dwg", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_project, "*.dxf", SearchOption.AllDirectories));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-rejected"
            && r.Entry.ParamsJson.Contains("snapshot-count-mismatch"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "export-committed");
        Assert.True(audit.VerifyChain(out _));
    }

    // ---- 等价类：非 working 派生 / zwcad 不转换 ----

    [Fact]
    public void Export_Acad_WorkingDxf_NoVendor_DegradedRefused()
    {
        // acad working 派生 + 无厂商（降级）：转换成功但降级交付即 ACadSharp DXF（残余崩溃风险），
        // fail-closed 不旁路（镜像旧闸口径——acad 显式选择下降级交付同样拒绝）。
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = () => null;
        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.Equal(1, code);
        Assert.Contains("0xE0000001", stderr);
        Assert.Empty(Directory.GetFiles(_project, "*.dwg", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_project, "*.dxf", SearchOption.AllDirectories));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-rejected"
            && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "export-degraded");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Export_Zwcad_WorkingDxf_ConverterNotCalled_DxfPathUnchanged()
    {
        CommandRouter.RuntimeSelector = () => "zwcad";
        CommandRouter.ConvertAcadDwgSnapshot = _ => throw new InvalidOperationException("zwcad 不得触发 DWG 快照转换");
        var rt = new SnapshotRuntime();
        CommandRouter.RuntimeFactory = () => rt;
        var (code, stdout, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.True(code == 0, $"code={code} out={stdout} err={stderr}");
        // zwcad 直接 Open working DXF（DXF 路径不变），不转换 DWG。
        Assert.NotNull(rt.OpenedPath);
        Assert.EndsWith(".dxf", rt.OpenedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vendor", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-committed");
    }

    [Fact]
    public void GatePredicate_Acad_NativeDwg_False_NoConversion()
    {
        // 原生 .dwg（非 working 派生）→ 闸 false → 不转换，acad 直接 Open（等价类，镜像判定口径）。
        CommandRouter.RuntimeSelector = () => "acad";
        Assert.False(CommandRouter.AcadWorkingDxfGateRefuses(@"C:\x\vendor-native.dwg", @"C:\x\a.dwg"));
        // 对照：working 派生 sidecar → 闸 true（触发转换）。
        Assert.True(CommandRouter.AcadWorkingDxfGateRefuses(@"C:\x\a.dwg.working.dxf", @"C:\x\a.dwg"));
    }

    // ---- CJK 抽检（AC1018 源往返）：DwgWriter AC1018 有损 → AC1027 兜底钉住 ----

    /// <summary>
    /// CJK 往返抽检（设计 §3）：写 CJK 文档 AC1018 → DwgWriter → DwgReader 读回。
    /// 实证（outputs/acad-dwg-snapshot/cjk-probe）：DwgWriter 在 AC1018 写中文有损（??????），
    /// AC1027 保真——故快照钉 AC1027 兜底。本测试钉住该口径：AC1018 往返不保 CJK（有损），
    /// AC1027 往返保 CJK（生产快照保证）。DxfWriter 同口径（AC1015/AC1018 有损，AC1027 保真）。
    /// </summary>
    [Fact]
    public void CjkRoundtrip_DwgWriter_AC1018Lossy_AC1027Preserved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cjk-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // AC1018 保留版本往返 → CJK 有损（不保真）。
            var doc18 = new CadDocument();
            doc18.Header.Version = ACadVersion.AC1018;
            doc18.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 10), Height = 5, Value = Cjk });
            string p18 = Path.Combine(dir, "cjk-ac1018.dwg");
            using (var fs = File.Create(p18)) DwgWriter.Write(fs, doc18);
            using (var r = new DwgReader(p18))
            {
                var back = r.Read();
                Assert.Equal(ACadVersion.AC1018, back.Header.Version);
                Assert.NotEqual(Cjk, back.Entities.OfType<TextEntity>().First().Value); // AC1018 有损
            }

            // AC1027 兜底往返 → CJK 保真（生产快照口径）。
            var doc27 = new CadDocument();
            doc27.Header.Version = ACadVersion.AC1027;
            doc27.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 10), Height = 5, Value = Cjk });
            string p27 = Path.Combine(dir, "cjk-ac1027.dwg");
            using (var fs = File.Create(p27)) DwgWriter.Write(fs, doc27);
            using (var r = new DwgReader(p27))
            {
                var back = r.Read();
                Assert.Equal(ACadVersion.AC1027, back.Header.Version);
                Assert.Equal(Cjk, back.Entities.OfType<TextEntity>().First().Value); // AC1027 保真
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// fake 厂商运行时：Open 经 DwgReader 读快照（捕获路径/版本/CJK 文本/计数）；
    /// SaveAs 经 DwgWriter 写回原文档（读回计数往返相等，过保真闸口）；AddLine 递增（live 重放）。
    /// zwcad 对照场景 Open 的可能是 DXF——DwgReader 读 DXF 会抛，故按扩展名分流读取。
    /// </summary>
    private sealed class SnapshotRuntime : ICadRuntime
    {
        private CadDocument? _doc;
        private int _count;
        public string? OpenedPath { get; private set; }
        public ACadVersion? OpenedVersion { get; private set; }
        public string? OpenedText { get; private set; }
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string path)
        {
            OpenedPath = path;
            using var fs = File.OpenRead(path);
            _doc = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
                ? new DwgReader(fs).Read()
                : new DxfReader(fs).Read();
            OpenedVersion = _doc.Header.Version;
            OpenedText = _doc.Entities.OfType<TextEntity>().FirstOrDefault()?.Value;
            _count = _doc.Entities.Count();
        }
        public void SaveAs(string path)
        {
            using var fs = File.Create(path);
            DwgWriter.Write(fs, _doc!);
        }
        public void Close(bool save) { }
        public void Restart() { }
        public void Dispose() { }
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => _count++;
        public void AddText(double x, double y, string text, double height, string? layer = null) => _count++;
        public void AddCircle(double x, double y, double radius, string? layer = null) => _count++;
        public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => _count++;
        public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) { }
        public int EntityCount() => _count;
    }
}
