using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// 协议拒绝面（切片B 复审修复波 I6/M10）：请求/结果 TSV 的**每一条**畸形记录都必须
/// FormatException 响亮失败（绝不空结果扮成功），且命令侧 op 对账（M10）拒绝串道请求。
/// 用例按等价类划分：缺必填键 / 未知键 / 数值非法 / 列数不足 / replace 专属键缺失 /
/// 结果侧缺 status / 列数不符 / 0-1 标记非法 / 整数非法 / 浮点非法。
/// </summary>
public class ProtocolParseRejectionsTests
{
    // ---- BridgeRequest.Parse 拒绝面（6 类）----

    [Theory]
    [InlineData("op\tread\nmaxdepth\t4\n", "缺少 dwg")]                       // 缺必填 dwg
    [InlineData("op\tread\ndwg\ta.dwg\nwat\t1\n", "未知请求键")]              // 未知键
    [InlineData("op\tread\ndwg\ta.dwg\nmaxdepth\tx\n", "maxdepth 非法")]      // 整数非法
    [InlineData("op\tread\ndwg\ta.dwg\n只一列\n", "请求行非法")]               // 单列行
    [InlineData("op\texplode-replace\ndwg\ta.dwg\nliveout\tC:\\o.dwg\n", "explode-replace 需要 handle 与 liveout")] // 缺 handle
    [InlineData("op\texplode-replace\ndwg\ta.dwg\nhandle\t21E00\n", "explode-replace 需要 handle 与 liveout")]      // 缺 liveout
    public void Request_Parse_Rejects_Malformed(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => BridgeRequest.Parse(text));
        Assert.Contains(fragment, ex.Message);
    }

    // ---- BridgeResult.Parse 拒绝面（5 类）+ 正例对照 ----

    [Theory]
    [InlineData("error\tboom\n", "缺少 status")]                                                     // 缺 status
    [InlineData("status\tok\nENTITY\t21E00\tTDbWall\tImpCurve\t0dt\t\t1\n", "ENTITY 记录列数非法")]     // 7 列（应 8）
    [InlineData("status\tok\nENTITY\t21E00\tTDbWall\tImpCurve\t0dt\t\t\t2\n", "0/1 标记非法")]          // 非法标记 "2"
    [InlineData("status\tok\nPRIM\t21E00\tx\tAcDbLine\tstart=0,0,0\n", "depth 非法")]                   // 整数非法
    [InlineData("status\tok\nENTITY\t21E00\tTDbWall\tImpCurve\t0dt\ta,b,c\t1,1,1\t1\n", "非法")]        // 浮点非法 "a,b,c"
    public void Result_Parse_Rejects_Malformed(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => BridgeResult.Parse(text));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Result_Parse_StatusOnly_Ok()
        => Assert.Equal("ok", BridgeResult.Parse("status\tok\n").Status);

    // ---- M10：命令侧 op 对账（READ 只收 read / EXPLODE_REPLACE 只收 explode-replace）----

    [Fact]
    public void RequireOp_MatchesCommand_NoThrow()
    {
        BridgeRequest.Parse("op\tread\ndwg\ta.dwg\n").RequireOp("read");
        BridgeRequest.Parse("op\texplode-replace\ndwg\ta.dwg\nhandle\t21E00\nliveout\tC:\\o.dwg\n")
            .RequireOp("explode-replace");
    }

    [Fact]
    public void RequireOp_Mismatch_ThrowsWithFragment()
    {
        // 通道目录复用/旧请求残留时防串道：READ 命令拿到 replace 请求（或反之）必须拒绝。
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BridgeRequest.Parse("op\texplode-replace\ndwg\ta.dwg\nhandle\t21E00\nliveout\tC:\\o.dwg\n")
                .RequireOp("read"));
        Assert.Contains("请求 op 与命令不符", ex.Message);
        Assert.Contains("explode-replace", ex.Message);

        Assert.Throws<InvalidOperationException>(() =>
            BridgeRequest.Parse("op\tread\ndwg\ta.dwg\n").RequireOp("explode-replace"));
    }
}
