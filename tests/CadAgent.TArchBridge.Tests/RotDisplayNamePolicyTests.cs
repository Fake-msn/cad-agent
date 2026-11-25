using CadAgent.TArchBridge.Protocol;
using Xunit;

namespace CadAgent.TArchBridge.Tests;

/// <summary>
/// B6（技术债批次B）：ROT moniker 显示名预过滤 <see cref="RotDisplayNamePolicy.ShouldConsiderMonikerDisplayName"/>。
/// 口径（保守，宁可多查不可漏认）：null（读不出）→ 考虑；含 ".dwg"（OrdinalIgnoreCase）→ 考虑；
/// 以 "!" 开头（OLE 链接 moniker）→ 考虑；其余（ProgID/TypeLib 等）→ 跳过。
/// </summary>
public class RotDisplayNamePolicyTests
{
    [Theory]
    [InlineData(null, true)]                       // 不可读：不得当作排除
    [InlineData(@"C:\dwg\Drawing1.dwg", true)]
    [InlineData(@"C:\dwg\DRAWING1.DWG", true)]     // 大小写不敏感
    [InlineData(@"C:\dwg\a.dwg.bak", true)]        // 含 .dwg 即考虑（保守）
    [InlineData("!{00020906-0000-0000-C000-000000000046}", true)]
    [InlineData("AutoCAD.Application", false)]
    [InlineData(@"C:\Program Files\Autodesk\AutoCAD 2014\acad.exe", false)]
    [InlineData("", false)]
    [InlineData("Drawing1", false)]
    public void Decision_IsConservative(string? displayName, bool expected)
    {
        Assert.Equal(expected, RotDisplayNamePolicy.ShouldConsiderMonikerDisplayName(displayName));
    }
}
