using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// M3（PR#30 合并前 ISTQB 终审）：liveout 前置守卫的**纯路径逻辑**上移到协议层后可直测。
/// 原实现固守 net45 桥工程（<c>Commands.GuardLiveOut</c>），net8 测试工程无法引用 → 守卫不可测。
/// 本组四例钉住等价类的判定与中文文案（桥侧只做"错误→InvalidOperationException"形态转换）：
/// 等于当前文档、等于请求 dwg、父目录不存在、空串；另加正例（合法路径放行）。
/// </summary>
public class LiveOutGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("liveout-guard").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>空 liveout → 逐字拒绝（最先判定，不触任何路径解析）。</summary>
    [Fact]
    public void Empty_Refused()
        => Assert.Equal("liveout 为空", LiveOutGuard.Check("", @"C:\a\doc.dwg", @"C:\a\src.dwg"));

    /// <summary>liveout == 当前文档路径（大小写不同亦算同一路径）→ 拒绝（不得覆盖源图）。</summary>
    [Fact]
    public void EqualsCurrentDoc_Refused_CaseInsensitive()
    {
        string doc = Path.Combine(_dir, "16#1116.dwg");
        string liveOut = Path.Combine(_dir, "16#1116.DWG");   // 大小写变体

        string? error = LiveOutGuard.Check(liveOut, doc, Path.Combine(_dir, "other.dwg"));

        Assert.NotNull(error);
        Assert.Contains("不得等于当前文档路径", error);
    }

    /// <summary>liveout == 请求 dwg 路径 → 拒绝（请求侧源图名同样受保护）。</summary>
    [Fact]
    public void EqualsRequestDwg_Refused()
    {
        string src = Path.Combine(_dir, "source.dwg");

        string? error = LiveOutGuard.Check(src, Path.Combine(_dir, "current.dwg"), src);

        Assert.NotNull(error);
        Assert.Contains("不得等于源图路径", error);
    }

    /// <summary>父目录不存在 → 拒绝（SaveAs 必失败的路径在开事务前拦下）。</summary>
    [Fact]
    public void MissingParentDir_Refused()
    {
        string liveOut = Path.Combine(_dir, "no-such-subdir", "out.live.dwg");

        string? error = LiveOutGuard.Check(liveOut, Path.Combine(_dir, "current.dwg"), Path.Combine(_dir, "src.dwg"));

        Assert.NotNull(error);
        Assert.Contains("liveout 父目录不存在", error);
    }

    /// <summary>正例：父目录存在且不等于两侧源图 → 放行（null）。</summary>
    [Fact]
    public void ValidLiveOut_Passes()
    {
        string liveOut = Path.Combine(_dir, "16#1116.dwg.live.dwg");

        Assert.Null(LiveOutGuard.Check(liveOut, Path.Combine(_dir, "16#1116.dwg"), Path.Combine(_dir, "copy.dwg")));
    }
}
