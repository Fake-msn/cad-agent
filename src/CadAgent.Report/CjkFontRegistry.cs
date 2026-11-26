using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace CadAgent.Report;

/// <summary>
/// 中文字体解析：QuestPDF 默认只用显式注册/应用目录字体，系统字体需 UseSystemFonts。
/// 优先选用系统已装中文字体族（微软雅黑/黑体/宋体/等线…）；系统族不可得时尝试从
/// Windows Fonts 目录或 CADAGENT_REPORT_FONT_PATH 注册字体文件。两者皆无 → 显式失败
/// （不生成缺字降级报告）。族名缓存，进程内只解析一次。
/// </summary>
public static class CjkFontRegistry
{
    /// <summary>偏好族名序（中英名并存——Windows 字体族可能仅以本地化名注册）。</summary>
    private static readonly string[] PreferredFamilies =
    {
        "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "宋体",
        "DengXian", "等线", "Microsoft JhengHei", "微软正黑体"
    };

    private static readonly object Gate = new();
    private static string? _family;

    /// <summary>系统字体族名 seam（测试可注入）。</summary>
    internal static Func<IEnumerable<string>> SystemFamilyNames =
        () => FontManager.GetSystemFonts().Select(f => f.FamilyName);

    /// <summary>字体文件候选 seam（测试可注入）。</summary>
    internal static Func<IEnumerable<string>> FontFileCandidates = DefaultFontFiles;

    /// <summary>解析并返回可用的中文字体族名（首次调用注册 QuestPDF 许可与系统字体探测）。</summary>
    public static string EnsureFamily()
    {
        lock (Gate)
        {
            if (_family is not null) return _family;

            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = true;

            string? family = PickFamily(SystemFamilyNames());
            if (family is null)
            {
                bool registeredAny = false;
                foreach (string path in FontFileCandidates())
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        FontManager.RegisterFontFromFile(path);
                        registeredAny = true;
                    }
                    catch
                    {
                        // 单个候选注册失败不终止：ttc 集合/损坏文件换下一个候选
                    }
                }
                var names = FontManager.GetRegisteredFonts().Select(f => f.FamilyName).ToList();
                family = PickFamily(names);
                if (family is null && registeredAny)
                    family = names.FirstOrDefault();
            }

            _family = family ?? throw new ReviewReportException(
                "未找到可用中文字体，无法生成中文报告。请安装微软雅黑/黑体/宋体，"
                + "或设置 CADAGENT_REPORT_FONT_PATH 指向 .ttf/.ttc 字体文件。");
            return _family;
        }
    }

    /// <summary>按偏好序从候选族名中选出可用中文字体族；无匹配→null。</summary>
    internal static string? PickFamily(IEnumerable<string> familyNames)
    {
        var set = new HashSet<string>(
            familyNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        foreach (string preferred in PreferredFamilies)
            if (set.Contains(preferred)) return preferred;
        return null;
    }

    private static IEnumerable<string> DefaultFontFiles()
    {
        string? env = Environment.GetEnvironmentVariable("CADAGENT_REPORT_FONT_PATH");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        yield return Path.Combine(fonts, "msyh.ttc");
        yield return Path.Combine(fonts, "simhei.ttf");
        yield return Path.Combine(fonts, "simsun.ttc");
        yield return Path.Combine(fonts, "Deng.ttf");
    }
}
