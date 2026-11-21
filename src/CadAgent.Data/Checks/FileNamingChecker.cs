using System.Text.RegularExpressions;
using CadAgent.Core;

namespace CadAgent.Data.Checks;

/// <summary>
/// JGJ/T 448-2018 5.4.3 图纸文件命名（宜）：专业代码_图纸编号_图纸名称（描述字段 _描述 可选；
/// 字段内词组 '-' 分隔不强制校验内部）。检查文件名而非图元——每图一次（非逐实体）。
/// .dwg 源 SourcePath 为 working sidecar（原始名 + ".working.dxf"）：含该后缀则去除以
/// 还原原始源文件名再判（第一 slice 控制器裁定；DrawingSession 未暴露 OriginalPath）。
/// 专业代码白名单（GB/T 51301）未提供 → 结构校验 only（DetectionBasis 注记）。
/// </summary>
public sealed class FileNamingChecker : IRuleChecker
{
    public string CheckType => "file-naming";

    private const string WorkingSuffix = ".working.dxf";

    /// <summary>结构式（简报原式）：专业代码（字母）_图纸编号（数字）_图纸名称（非空，描述可选）。</summary>
    private static readonly Regex NameStructure = new(@"^[A-Za-z]+_[0-9]+_.+", RegexOptions.Compiled);

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        string fileName = Path.GetFileName(((DrawingSession)session).SourcePath);
        string original = fileName.EndsWith(WorkingSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^WorkingSuffix.Length]
            : fileName;
        string stem = Path.GetFileNameWithoutExtension(original);
        if (NameStructure.IsMatch(stem)) return Array.Empty<Issue>();

        return new[]
        {
            new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                new[] { original },
                $"图纸文件名 \"{original}\" 不符合命名格式 专业代码_图纸编号_图纸名称（如 A_001_平面图；描述字段 _描述 可选，JGJ/T 448-2018 5.4.3）。",
                rule.Suggestion,
                "文件名结构校验（专业代码_图纸编号_图纸名称）；专业代码白名单（GB/T 51301）未提供，结构校验 only；.working.dxf 后缀去除还原原始源文件名；每图一次")
        };
    }
}
