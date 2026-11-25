using System;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// B6（技术债批次B）：ROT moniker 显示名预过滤（纯函数，桥/宿主/测试同源）。
/// <para>背景：ROT 里既有本方 AutoCAD 的 exe/文档 moniker，也有大量他方 COM 对象
/// （如 <c>AutoCAD.Application</c> 类 moniker、TypeLib/表达式 moniker）。逐个
/// <c>IRunningObjectTable.GetObject</c> 查表再读 HWND 归属代价高且可能触发服务器查询；
/// 先用显示名做**保守**预过滤：只跳过明显不可能承载 DWG 实例的 moniker。</para>
/// <para>口径（宁可多查不可漏认）：<c>null</c> = 显示名读不出 → **必须**继续考虑
/// （不可判定不得当作排除）；含 <c>.dwg</c>（OrdinalIgnoreCase）= 文档 moniker；
/// 以 <c>!</c> 开头 = OLE 复合文档链接 moniker。其余名字（ProgID、TypeLib 等）跳过。</para>
/// </summary>
public static class RotDisplayNamePolicy
{
    /// <summary>该显示名是否值得进一步查表/归属判定；null（不可读）→ true。</summary>
    public static bool ShouldConsiderMonikerDisplayName(string? displayName)
    {
        if (displayName == null) return true;
        if (displayName.StartsWith("!", StringComparison.Ordinal)) return true;
        return displayName.IndexOf(".dwg", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
