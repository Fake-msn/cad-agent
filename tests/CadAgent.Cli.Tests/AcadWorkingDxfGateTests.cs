using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// I1 fail-closed 闸（PR#24 修复波）：acad + working 派生 DXF 输入 → export/--live
/// 先于运行时创建拒绝（不激活 COM）+ 审计留痕 + guidance，exit 非零；zwcad 同输入放行不回归。
/// fake seam 单测（非真机）：RuntimeFactory 注入"创建即抛"假工厂钉住闸先于运行时创建。
/// 静态 seam（RuntimeSelector/RuntimeFactory）用完即还原（本程序集已禁并行，见 AssemblyInfo）。
/// </summary>
public class AcadWorkingDxfGateTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"acadgate-{Guid.NewGuid():N}");
    private readonly string _drawing;
    private readonly string _plan;
    private readonly Func<string?> _originalSelector = CommandRouter.RuntimeSelector;
    private readonly Func<ICadRuntime?>? _originalFactory = CommandRouter.RuntimeFactory;

    public AcadWorkingDxfGateTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        _plan = Path.Combine(_project, "plan.json");
        File.WriteAllText(_plan,
            "{\"planId\":\"GATE-1\",\"steps\":[{\"kind\":\"add-line\",\"params\":{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}}]}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CommandRouter.RuntimeSelector = _originalSelector;
        CommandRouter.RuntimeFactory = _originalFactory;
        Directory.Delete(_project, true);
        File.Delete(_drawing);
        if (File.Exists(_drawing + ".live.dwg")) File.Delete(_drawing + ".live.dwg");
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    /// <summary>闸必须先于运行时创建：工厂被触达即失败（镜像 UnparsedGateExportTests 先例）。</summary>
    private static ICadRuntime MustNotCreateRuntime() =>
        throw new InvalidOperationException("I1 闸必须先于运行时创建（不激活 COM）");

    [Fact]
    public void Export_Acad_DxfSource_RefusedAudited_NoArtifacts()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = MustNotCreateRuntime;
        var (code, _, stderr) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.Equal(1, code);
        Assert.Contains("0xE0000001", stderr);
        Assert.Contains("zwcad", stderr);
        // 无厂商产物（DXF 降级也算旁路——fail-closed 不交付可能崩溃 AutoCAD 的 DXF）。
        Assert.Empty(Directory.GetFiles(_project, "*.dwg", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_project, "*.dxf", SearchOption.AllDirectories));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-rejected"
            && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "export-degraded");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Export_Acad_DwgSource_WorkingSidecar_Refused()
    {
        // .dwg 源：Load 物化 working.dxf（ACadSharp DxfWriter 产物）→ SourcePath 即 sidecar。
        string srcDir = Path.Combine(Path.GetTempPath(), $"acadgate-dwg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        string dwg = Path.Combine(srcDir, "src.dwg");
        var doc = CadAgent.Data.DrawingSession.Load(_drawing).Doc;
        using (var fs = File.Create(dwg)) ACadSharp.IO.DwgWriter.Write(fs, doc);
        try
        {
            CommandRouter.RuntimeSelector = () => "acad";
            CommandRouter.RuntimeFactory = MustNotCreateRuntime;
            var (code, _, stderr) = Run("export", "--drawing", dwg, "--out", Path.Combine(_project, "out.dwg"));
            Assert.Equal(1, code);
            Assert.Contains("0xE0000001", stderr);
            Assert.True(File.Exists(dwg + ".working.dxf")); // working 已物化，但不得喂给 acad
            using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
            Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-rejected"
                && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        }
        finally { Directory.Delete(srcDir, true); }
    }

    [Fact]
    public void Export_Zwcad_SameDxfInput_PassesNoRegression()
    {
        CommandRouter.RuntimeSelector = () => "zwcad";
        CommandRouter.RuntimeFactory = () => new RoundTripRuntime { Source = _drawing };
        var (code, stdout, _) = Run("export", "--drawing", _drawing, "--out", Path.Combine(_project, "out.dwg"));
        Assert.Equal(0, code);
        Assert.Contains("vendor", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "export-committed");
    }

    [Fact]
    public void Run_Live_Acad_SnapshotRefused_ShadowChainUnaffected()
    {
        CommandRouter.RuntimeSelector = () => "acad";
        CommandRouter.RuntimeFactory = MustNotCreateRuntime;
        var (code, _, stderr) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.Equal(1, code);
        Assert.Contains("0xE0000001", stderr);
        Assert.False(File.Exists(_drawing + ".live.dwg"));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        // 影子 tx 不受影响（tx-commit 先行）；live 拒绝留痕 com-replay-failed + reason。
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "tx-commit");
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-failed"
            && r.Entry.ParamsJson.Contains("acad-working-dxf"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind == "com-replay-committed");
        Assert.True(audit.VerifyChain(out _));
    }

    [Fact]
    public void Run_Live_Zwcad_SameSnapshot_ReplaysNoRegression()
    {
        CommandRouter.RuntimeSelector = () => "zwcad";
        CommandRouter.RuntimeFactory = () => new CountingLiveRuntime();
        var (code, stdout, err) = Run("run", _plan, "--drawing", _drawing, "--live");
        Assert.True(code == 0, $"code={code} out={stdout} err={err}");
        Assert.Contains(".live.dwg", stdout);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "com-replay-committed");
    }

    // ---- 判定口径边界（internal seam 直测，ISTQB 等价类/边界） ----

    [Theory]
    [InlineData(@"C:\x\a.dwg.working.dxf", @"C:\x\a.dwg", true)]   // .dwg 源 sidecar 后缀
    [InlineData(@"C:\x\a.dxf", @"C:\x\a.dxf", true)]               // .dxf 源==WorkingPathFor(源)（原地重写）
    [InlineData(@"C:\x\snap\a.dxf", @"C:\x\a.dxf", true)]          // --live 快照同源（basename 一致）
    [InlineData(@"C:\x\snap\a.dwg.working.dxf", @"C:\x\a.dwg.working.dxf", true)] // .dwg 源快照
    [InlineData(@"C:\x\vendor-native.dwg", @"C:\x\a.dwg", false)]  // 非 working 派生 .dwg 不拒
    public void GatePredicate_Acad_EquivalenceClasses(string input, string source, bool expected)
    {
        CommandRouter.RuntimeSelector = () => "acad";
        Assert.Equal(expected, CommandRouter.AcadWorkingDxfGateRefuses(input, source));
    }

    [Fact]
    public void GatePredicate_Zwcad_AlwaysPasses()
    {
        CommandRouter.RuntimeSelector = () => "zwcad";
        Assert.False(CommandRouter.AcadWorkingDxfGateRefuses(@"C:\x\a.dwg.working.dxf", @"C:\x\a.dwg"));
        Assert.False(CommandRouter.AcadWorkingDxfGateRefuses(@"C:\x\a.dxf", @"C:\x\a.dxf"));
    }

    /// <summary>fake 厂商运行时：SaveAs 经 ACadSharp DwgWriter 写回（读回计数往返相等）。</summary>
    private sealed class RoundTripRuntime : ICadRuntime
    {
        public string? Source;
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string p) { }
        public void SaveAs(string p)
        {
            var d = CadAgent.Data.DrawingSession.Load(Source!).Doc;
            using var fs = File.Create(p);
            ACadSharp.IO.DwgWriter.Write(fs, d);
        }
        public void Close(bool save) { }
        public void Restart() { }
        public void Dispose() { }
        public void AddLine(double x1, double y1, double x2, double y2, string? layer = null) => throw new NotSupportedException();
        public void AddText(double x, double y, string text, double height, string? layer = null) => throw new NotSupportedException();
        public void AddCircle(double x, double y, double radius, string? layer = null) => throw new NotSupportedException();
        public void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null) => throw new NotSupportedException();
        public void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives) => throw new NotSupportedException();
        public int EntityCount() => throw new NotSupportedException();
    }

    /// <summary>fake live 运行时：Open 读快照计数，AddLine 递增（读回闸口 vendor==shadow）。</summary>
    private sealed class CountingLiveRuntime : ICadRuntime
    {
        private int _count;
        public RuntimeInfo GetInfo() => new("Fake", "1", true);
        public void Open(string path)
        {
            using var fs = File.OpenRead(path);
            _count = new ACadSharp.IO.DxfReader(fs).Read().Entities.Count();
        }
        public void SaveAs(string path) { }
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
