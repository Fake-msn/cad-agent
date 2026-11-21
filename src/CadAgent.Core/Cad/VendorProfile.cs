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

    // AutoCAD Blocks.Add 按 Autodesk 文档序=(Name, origin)；开发机无 AutoCAD，待工作环境真机探针核定
    // （docs/verification/autocad-adapter-protocol.md）。若实测相反，翻此标志即可，无需改适配器代码。
    public static readonly VendorProfile Acad = new("AutoCAD", "AutoCAD.Application", "acad", BlocksAddNameFirst: true);
}