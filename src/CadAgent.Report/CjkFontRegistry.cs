using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace CadAgent.Report;

/// <summary>
/// 中文字体解析：QuestPDF 默认只用显式注册/应用目录字体，系统字体需 UseSystemFonts。
/// 优先选用系统已装中文字体族（微软雅黑/黑体/宋体/等线…）；系统族不可得时尝试从
/// Windows Fonts 目录或 CADAGENT_REPORT_FONT_PATH 注册字体文件。两者皆无 → 显式失败
/// （不生成缺字降级报告）；仅当 CADAGENT_REPORT_FONT_PATH 显式给出时才接受
/// 「首个已注册族」兜底（用户断言该字体，见 <see cref="EnsureFamily"/> remarks）。
/// 族名缓存，进程内只解析一次。
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

    /// <summary>
    /// QuestPDF 已注册族名 seam（测试可注入）。注册是进程级全局状态且无注销 API——
    /// 同进程其他用例注册过的字体仍可见，故须留 seam 才能确定性地测「无字体」路径。
    /// </summary>
    internal static Func<IEnumerable<string>> RegisteredFamilyNames =
        () => FontManager.GetRegisteredFonts().Select(f => f.FamilyName);

    /// <summary>解析并返回可用的中文字体族名（首次调用注册 QuestPDF 许可与系统字体探测）。</summary>
    /// <remarks>
    /// 回落口径（合并前加固）：注册字体文件后若仍无偏好中文字体族，仅在调用方显式提供
    /// CADAGENT_REPORT_FONT_PATH（用户断言该字体可用）时接受首个已注册族；否则抛
    /// <see cref="ReviewReportException"/>——宁可失败也不产缺字（tofu）报告。
    /// </remarks>
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
                foreach (string path in FontFileCandidates())
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        FontManager.RegisterFontFromFile(path);
                    }
                    catch
                    {
                        // 单个候选注册失败不终止：ttc 集合/损坏文件换下一个候选
                    }
                }
                family = PickFamilyOrEnvFallback(RegisteredFamilyNames(), EnvFontPathProvided());
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

    /// <summary>
    /// 已注册族名的最终裁决（纯函数，可单测）：偏好族命中即用；否则仅当调用方显式给出
    /// 字体路径（envProvided=true，用户断言）时接受首个已注册族，避免拉丁字体冒充中文
    /// 字体产出缺字报告。无可用 → null（调用方抛异常）。
    /// </summary>
    internal static string? PickFamilyOrEnvFallback(IEnumerable<string> familyNames, bool envProvided)
    {
        var names = familyNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return PickFamily(names) ?? (envProvided ? names.FirstOrDefault() : null);
    }

    /// <summary>测试用：清空族名缓存（仅清缓存，已注册字体保留；不再触发重复注册）。</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _family = null;
    }

    private const string FontPathEnv = "CADAGENT_REPORT_FONT_PATH";

    private static bool EnvFontPathProvided() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FontPathEnv));

    private static IEnumerable<string> DefaultFontFiles()
    {
        string? env = Environment.GetEnvironmentVariable(FontPathEnv);
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        yield return Path.Combine(fonts, "msyh.ttc");
        yield return Path.Combine(fonts, "simhei.ttf");
        yield return Path.Combine(fonts, "simsun.ttc");
        yield return Path.Combine(fonts, "Deng.ttf");
    }
}
