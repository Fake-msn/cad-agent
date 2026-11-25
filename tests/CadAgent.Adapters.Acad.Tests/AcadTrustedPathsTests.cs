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
/// </summary>
public class AcadTrustedPathsTests : IDisposable
{
    private readonly Func<dynamic, string, string> _origReader = AcadRuntime.DocumentVariableReader;
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
    /// 端到端（fake app，无 COM）：GetTrustedPaths 先走 EnsureAppTArch（/p 天正 profile + Gate1），
    /// 再取 <c>_doc ?? _app.ActiveDocument</c> 读文档面系统变量。
    /// </summary>
    [Fact]
    public void GetTrustedPaths_ReadsFromActiveDocument()
    {
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
