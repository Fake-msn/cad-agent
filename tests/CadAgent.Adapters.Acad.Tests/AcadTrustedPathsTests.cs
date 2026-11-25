using System.Diagnostics;
using CadAgent.Adapters.Acad;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Adapters.Acad.Tests;

/// <summary>
/// TRUSTEDPATHS 读取单元测试（fake 缝，无真机/无 COM）。真机实证（Fix round 2，TRX Skip 原因）：
/// ActiveX <b>Application</b> 对象没有 <c>GetSystemVariable</c>——dynamic 绑定抛
/// "'System.__ComObject' does not contain a definition for 'GetSystemVariable'"；
/// 系统变量属 <b>Document</b>（<c>doc.GetVariable(name)</c>）。此处钉住纯助手
/// <see cref="AcadRuntime.ReadTrustedPaths"/> 的 API 口径、缝注入、无活动文档拒绝与端到端取数。
/// Fix round 4（R17）：<see cref="AcadRuntime.GetTrustedPaths"/> 改为**注册表优先**——命中即返回、
/// 零 COM/零实例（COM 实读会 /p 拉起实例，启动期 Quit 被忽略→孤儿 acad.exe）；COM 文档面路径
/// 降级为注册表无值时的兜底，仍由本类（缝注入 null）逐项钉住。
/// </summary>
public class AcadTrustedPathsTests : IDisposable
{
    private readonly Func<dynamic, string, string> _origReader = AcadRuntime.DocumentVariableReader;
    private readonly Func<string, string?> _origTrustedSource = AcadRuntime.TrustedPathsSource;
    private readonly Func<string, Process?> _origLauncher = AcadRuntime.ProcessLauncher;
    private readonly Func<string, object?> _origActive = AcadAttach.ActiveObjectResolver;
    private readonly Func<string[]> _origNames = VendorGuard.ProcessNames;
    private readonly int _origAttempts = AcadRuntime.StartupRetryAttempts;
    private readonly TimeSpan _origDelay = AcadRuntime.StartupRetryDelay;

    public AcadTrustedPathsTests()
    {
        // 守卫缝：本机 acad 进程存在与否不影响单测（真机守卫语义由 VendorGuard 自测覆盖）。
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        AcadRuntime.DocumentVariableReader = _origReader;
        AcadRuntime.TrustedPathsSource = _origTrustedSource;
        AcadRuntime.ProcessLauncher = _origLauncher;
        AcadAttach.ActiveObjectResolver = _origActive;
        VendorGuard.ProcessNames = _origNames;
        AcadRuntime.StartupRetryAttempts = _origAttempts;
        AcadRuntime.StartupRetryDelay = _origDelay;
    }

    /// <summary>
    /// 假文档：只暴露 ActiveX Document 面的系统变量 API <c>GetVariable(string)</c>
    /// （**没有** GetSystemVariable——镜像真机 Application 缺该成员的实证）。
    /// </summary>
    public sealed class FakeVariableDoc
    {
        private readonly string _value;
        public FakeVariableDoc(string value) => _value = value;
        public string? LastVariableName { get; private set; }
        public int Reads { get; private set; }
        public string GetVariable(string name)
        {
            LastVariableName = name;
            Reads++;
            return _value;
        }
    }

    [Fact]
    public void ReadTrustedPaths_DefaultReader_UsesDocumentGetVariable()
    {
        var doc = new FakeVariableDoc(@"C:\trusted;E:\bridge\bin");
        Assert.Equal(@"C:\trusted;E:\bridge\bin", AcadRuntime.ReadTrustedPaths(doc));
        Assert.Equal("TRUSTEDPATHS", doc.LastVariableName);   // 文档面 API + 变量名口径
        Assert.Equal(1, doc.Reads);
    }

    [Fact]
    public void ReadTrustedPaths_NullDoc_ThrowsExactMessage()
    {
        var ex = Assert.Throws<CadRuntimeException>(() => AcadRuntime.ReadTrustedPaths(null));
        Assert.Equal("无活动文档，无法读取 TRUSTEDPATHS", ex.Message);
    }

    [Fact]
    public void ReadTrustedPaths_InjectedReader_IsUsed()
    {
        // 缝注入：助手只经 DocumentVariableReader 取数（不直接碰 doc.GetVariable）。
        var doc = new FakeVariableDoc("ignored");
        AcadRuntime.DocumentVariableReader = (d, n) => "SEAM:" + doc.Reads + ":" + n;
        Assert.Equal("SEAM:0:TRUSTEDPATHS", AcadRuntime.ReadTrustedPaths(doc));
        Assert.Equal(0, doc.Reads);                            // 默认读取机构未被调用
    }

    /// <summary>
    /// R17 缝：注册表命中即返回，**零 COM、零实例**——ProcessLauncher 若被调用即失败
    /// （证明注册表优先路径不再 /p 拉起 acad.exe，孤儿进程根因被切断）。
    /// </summary>
    [Fact]
    public void GetTrustedPaths_RegistrySeamHit_ReturnsWithoutComOrLaunch()
    {
        var launched = new List<string>();
        AcadRuntime.ProcessLauncher = name => { launched.Add(name); return new Process(); };
        AcadRuntime.TrustedPathsSource = profile => "REG:" + profile;

        using var rt = new AcadRuntime();
        Assert.Equal("REG:TElec20V5", rt.GetTrustedPaths());   // profile 口径随缝传入
        Assert.Empty(launched);                                // 未启动任何实例
    }

    /// <summary>R17 缝透传口径：GetTrustedPaths 以天正 profile 名调用读取缝（非硬编码在别处）。</summary>
    [Fact]
    public void GetTrustedPaths_SeamReceivesTArchProfileName()
    {
        var profiles = new List<string>();
        AcadRuntime.TrustedPathsSource = profile => { profiles.Add(profile); return ""; };

        using var rt = new AcadRuntime();
        Assert.Equal("", rt.GetTrustedPaths());                // 空串=已配置但为空：照实返回，不吞成 null
        Assert.Equal(new[] { AcadRuntime.TArchProfileName }, profiles);
        Assert.Equal("TElec20V5", AcadRuntime.TArchProfileName);
    }

    /// <summary>
    /// 端到端（fake app，无 COM）：注册表无值（缝注入 null）→ 回退路径先走 EnsureAppTArch
    /// （/p 天正 profile + Gate1），再取 <c>_doc ?? _app.ActiveDocument</c> 读文档面系统变量。
    /// </summary>
    [Fact]
    public void GetTrustedPaths_RegistryMiss_FallsBackToActiveDocument()
    {
        AcadRuntime.TrustedPathsSource = _ => null;             // 强制回退（不读真机注册表，保持确定性）
        var doc = new FakeVariableDoc(@"C:\trusted");
        var app = new FakeGate1App(doc);
        var launches = new List<string>();
        AcadRuntime.ProcessLauncher = name => { launches.Add(name); return new Process(); };
        AcadAttach.ActiveObjectResolver = _ => app;

        using var rt = new AcadRuntime();
        Assert.Equal(@"C:\trusted", rt.GetTrustedPaths());
        Assert.Equal(new[] { "TElec20V5" }, launches);         // /p 天正 profile 口径保持（复审 F2）
        Assert.Equal("TRUSTEDPATHS", doc.LastVariableName);
    }

    /// <summary>
    /// M6（PR#30 终审）：多产品子键候选选择（纯函数）——PROFILE 命中优先：第一个子键虽含 profile
    /// 但无值、第二个含 profile 且有值 → 选第二个；无任一子键含 profile → 回退首个非 null；
    /// 全无 → null。注册表读取本身不可单测，故把选择策略抽成纯函数钉住。
    /// </summary>
    [Fact]
    public void SelectTrustedPathsCandidate_PrefersProfileHit_ThenFirstNonNullFallback()
    {
        var keys = new[] { "ACAD-D001:804", "ACAD-D001:409", "ACAD-D002:804" };

        // PROFILE 命中在第二个键（第一个含 profile 但值缺失，第三个无 profile 只有值）。
        var probes = new Dictionary<string, (bool HasProfile, string? Value)>
        {
            ["ACAD-D001:804"] = (true, null),
            ["ACAD-D001:409"] = (true, "HIT"),
            ["ACAD-D002:804"] = (false, "FALLBACK"),
        };
        Assert.Equal("HIT", AcadRuntime.SelectTrustedPathsCandidate(keys, k => probes[k]));

        // 无任一键含目标 profile → 首个非 null 值（旧"首个命中"口径兜底）。
        Assert.Equal("FB-2", AcadRuntime.SelectTrustedPathsCandidate(keys,
            k => (false, k == "ACAD-D001:804" ? null : k == "ACAD-D001:409" ? "FB-2" : "FB-3")));

        // 全部无值 → null。
        Assert.Null(AcadRuntime.SelectTrustedPathsCandidate(keys, _ => (true, null)));

        // 空串=「已配置但为空」属有效值（不是缺失）：命中即返回空串。
        Assert.Equal("", AcadRuntime.SelectTrustedPathsCandidate(keys, k => (true, k == "ACAD-D001:409" ? "" : null)));
    }

    /// <summary>假 app：Gate1 所需 Version/Documents.Count、ActiveDocument 与退出 Quit。</summary>
    public sealed class FakeGate1App
    {
        private readonly FakeVariableDoc _doc;
        public FakeGate1App(FakeVariableDoc doc) => _doc = doc;
        public string Version => "19.1s (LMS Tech)";
        public FakeGate1Documents Documents => new();
        public FakeVariableDoc ActiveDocument => _doc;
        public bool Visible { get; set; }
        public void Quit() { }
    }

    public sealed class FakeGate1Documents
    {
        public int Count => 1;   // /p 启动默认已开 Drawing1.dwg（Gate1 就绪条件）
    }
}
