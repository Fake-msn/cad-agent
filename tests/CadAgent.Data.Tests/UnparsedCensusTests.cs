using System.Security.Cryptography;
using System.Text.Json;
using CadAgent.Data;
using Xunit.Abstractions;

namespace CadAgent.Data.Tests;

public class UnparsedCensusTests
{
    private readonly ITestOutputHelper _output;
    public UnparsedCensusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FromNotifications_ParsesPerClassCounts()
    {
        var msgs = new[]
        {
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
            "Unlisted object with DXF name TCH_OPENING has been read as an UnknownEntity",
            "Entity in SortEntitiesTable 2996 not found 0", // 非 UnknownEntity 通知→忽略
        };
        var c = UnparsedCensus.FromNotifications(msgs);
        Assert.Equal(3, c.TotalCount);
        Assert.Equal(2, c.Classes.Single(x => x.CppClassName == "TCH_WALL").Count);
        Assert.Equal(1, c.Classes.Single(x => x.CppClassName == "TCH_OPENING").Count);
    }

    [Fact]
    public void FromNotifications_Empty_NoClasses()
    {
        Assert.Equal(0, UnparsedCensus.FromNotifications(Array.Empty<string>()).TotalCount);
        Assert.Equal(0, UnparsedCensus.Empty.TotalCount);
    }

    [Fact]
    public void Sidecar_Roundtrip_And_StaleSourceHash_ReturnsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "a.dwg");
        File.WriteAllText(src, "v1");
        var c = UnparsedCensus.FromNotifications(new[] { "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity" });
        UnparsedCensusStore.Save(dir, src, c);
        Assert.Equal(1, UnparsedCensusStore.Load(dir, src).TotalCount);
        File.WriteAllText(src, "v2"); // 源变→陈旧
        Assert.Equal(0, UnparsedCensusStore.Load(dir, src).TotalCount);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Summarize_IncludesUnparsedBriefs_SeparateFromGraphUnknowns()
    {
        // 合成 session（ctor census 参）：空 doc + census TCH_WALL×2 → Summarize().Unparsed 2 条 UnknownBrief(Handle="")
        // 且 Unknowns（图内）保持空；Entities 空
        var doc = new ACadSharp.CadDocument();
        var session = new DrawingSession("mem.dxf", doc, UnparsedCensus.FromNotifications(new[]
        {
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
            "Unlisted object with DXF name TCH_WALL has been read as an UnknownEntity",
        }));
        var s = session.Summarize();
        Assert.Empty(s.Unknowns);
        Assert.Equal(2, s.Unparsed!.Count);
        Assert.All(s.Unparsed!, u => Assert.Equal("", u.Handle));
    }

    [Fact]
    public void Sidecar_ClassesNull_LoadReturnsEmpty_NoThrow()
    {
        // Fix Round 1（R4 闭合）：JSON 合法 + path/hash 匹配但 Classes:null——
        // 原实现 new UnparsedCensus(null!) → TotalCount NRE；守卫后降级 Empty 不抛。
        string dir = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            WriteRawSidecar(dir, src, "null");
            var c = UnparsedCensusStore.Load(dir, src);
            Assert.Empty(c.Classes);
            Assert.Equal(0, c.TotalCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sidecar_ClassesMissing_LoadReturnsEmpty_NoThrow()
    {
        // Classes 属性整体缺失（反序列化为 null）与显式 null 同口径降级 Empty。
        string dir = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            WriteRawSidecar(dir, src, classesJson: null);
            var c = UnparsedCensusStore.Load(dir, src);
            Assert.Empty(c.Classes);
            Assert.Equal(0, c.TotalCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sidecar_NegativeCount_LoadReturnsEmpty_NoThrow()
    {
        // 负 Count 若漏网：TotalCount 为负且 Summarize 的 Enumerable.Repeat 抛 ArgumentOutOfRangeException。
        // 守卫后 Load 降级 Empty，合成 session Summarize 也不抛。
        string dir = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            WriteRawSidecar(dir, src, """[{"CppClassName":"TCH_WALL","Count":-1}]""");
            var c = UnparsedCensusStore.Load(dir, src);
            Assert.Empty(c.Classes);
            Assert.Equal(0, c.TotalCount);
            var session = new DrawingSession("mem.dxf", new ACadSharp.CadDocument(), c);
            Assert.Equal(0, session.Summarize().Unparsed?.Count ?? 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>手写 sidecar（path/hash 与源匹配，Classes 段可控）：合成损坏/异常 census 载荷。</summary>
    private static void WriteRawSidecar(string dir, string src, string? classesJson)
    {
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(src)));
        string classes = classesJson == null ? "" : $",\"Classes\":{classesJson}";
        string json = $"{{\"SourcePath\":{JsonSerializer.Serialize(Path.GetFullPath(src))},\"SourceHash\":\"{hash}\"{classes}}}";
        string p = Path.Combine(dir, ".cadagent", "unparsed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, json);
    }

    [SkippableFact]
    public void RealNativeTArch_Load_CensusNonEmpty()
    {
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native3\2号厂房1021_t9.dwg";
        Skip.If(!File.Exists(path), "真实原生天正标本不在本机，跳过");
        // 直读源（不经 working）：census 只产生于 DWG 首载通知通道（working DXF 无 TCH）。
        // 清除既有 working 副本强制走 LoadDwg，保证测试可重复（二次运行不因 working 分支 census 空而假失败）。
        string working = DrawingSession.WorkingPathFor(path);
        if (File.Exists(working)) File.Delete(working);
        try
        {
            // 经 Load 会建 working 副本于 outputs（可接受，outputs 非用户目录；源 .dwg 只读 never written）
            var session = DrawingSession.Load(path);
            _output.WriteLine($"真实标本 census：TotalCount={session.Unparsed.TotalCount}，类数={session.Unparsed.Classes.Count}");
            foreach (var c in session.Unparsed.Classes)
                _output.WriteLine($"  {c.CppClassName} × {c.Count}");
            Assert.True(session.Unparsed.TotalCount > 1000);
            Assert.Contains(session.Unparsed.Classes, c => c.CppClassName == "TCH_WALL");
        }
        finally
        {
            // 测试产物清理：标本目录保持只读原状（副本仅 .dwg 本体）
            if (File.Exists(working)) File.Delete(working);
        }
    }
}
