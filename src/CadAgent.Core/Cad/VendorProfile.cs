namespace CadAgent.Core.Cad;

/// <summary>
/// 厂商运行时差异数据（spec §3/§11）：守卫进程名/ProgID/COM 签名序参数化。
/// Core 厂商无关——本记录仅数据，不含厂商逻辑。
/// </summary>
public sealed record VendorProfile(string Vendor, string ProgId, string ProcessName, bool BlocksAddNameFirst);

public static class VendorProfiles
{
    // ZWCAD Blocks.Add 实测签名序=(origin, Name)（2026-11-20 真机探针核定，与 AutoCAD 文档序相反）
    public static readonly VendorProfile Zwcad = new("ZWCAD", "ZWCAD.Application", "ZWCAD", BlocksAddNameFirst: false);

    // AutoCAD Blocks.Add 实测签名序=(origin, Name)（2026-11-24 真机探针核定，AutoCAD 2014 R19.1）：
    // 文档序 (Name, origin) 调用抛「Could not convert argument 0 for call to Add」
    // （g3 证据：CallSite ComObject,String,Double[] 参数 0 转换失败）——与 ZWCAD 实测一致，
    // 与 Autodesk 文档相反。不赌文档，两厂商均真机核定（docs/verification/autocad-adapter-protocol.md 步骤 4）。
    public static readonly VendorProfile Acad = new("AutoCAD", "AutoCAD.Application", "acad", BlocksAddNameFirst: false);
}
