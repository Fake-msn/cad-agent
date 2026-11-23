namespace CadAgent.Core.Cad;

/// <summary>
/// 厂商用户 profile CUIX 文件定位（健康金丝雀 + 事故留证）。
/// 目录 glob 覆盖版本/语言变体；未安装厂商返回空列表（不抛）。
/// </summary>
public static class CuixLocator
{
    internal static Func<string> AppData =
        () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static IReadOnlyList<string> FindCuix(VendorProfile profile)
    {
        var root = AppData();
        string vendorRoot;
        string dirPattern;
        string fileName;
        if (profile.Vendor == "ZWCAD")
        {
            vendorRoot = Path.Combine(root, "ZWSOFT", "ZWCAD");
            dirPattern = "*";
            fileName = "ZWCAD.CUIX";
        }
        else if (profile.Vendor == "AutoCAD")
        {
            vendorRoot = Path.Combine(root, "Autodesk");
            dirPattern = "AutoCAD*";
            fileName = "acad.cuix";
        }
        else
        {
            return Array.Empty<string>();
        }

        if (!Directory.Exists(vendorRoot)) return Array.Empty<string>();
        var found = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(vendorRoot, dirPattern))
            found.AddRange(Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories));
        return found;
    }
}
