using System.Text;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data;

/// <summary>
/// LLM 读图文本摘要（3b-i；视觉渲染留 3c COM 导出/计划4）。
/// 有界（maxChars 截断）、确定性（ordinal 排序）：源/版本/图层/实体类型计数/
/// 文本清单（前50）/块名/语义叠加层摘要（类×来源计数+人工映射）。
/// </summary>
public static class DrawingSummarizer
{
    public static string Summarize(DrawingSession session, SemanticOverlay? overlay, int maxChars = 8000)
    {
        var summary = session.Summarize();
        var sb = new StringBuilder();
        sb.Append(FormattableString.Invariant($"图纸: {Path.GetFileName(session.SourcePath)} | 版本: {summary.Version}\n"));

        sb.Append("图层: ");
        sb.Append(string.Join(", ", summary.Layers.OrderBy(l => l, StringComparer.Ordinal)));
        sb.Append('\n');

        sb.Append("实体计数: ");
        sb.Append(string.Join(", ", summary.Entities
            .GroupBy(e => e.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}")));
        sb.Append('\n');

        var texts = session.Doc.Entities
            .OfType<TextEntity>()
            .OrderBy(t => t.Handle)
            .Take(50)
            .ToList();
        if (texts.Count > 0)
        {
            sb.Append("文本清单(前50):\n");
            foreach (var t in texts)
                sb.Append(FormattableString.Invariant($"  [{t.Handle}] \"{t.Value}\" 字高{t.Height:0.##} 层{t.Layer?.Name ?? "0"}\n"));
        }

        var blocks = session.Doc.BlockRecords
            .Select(b => b.Name)
            .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("*", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (blocks.Count > 0)
            sb.Append("块: ").Append(string.Join(", ", blocks)).Append('\n');

        if (overlay == null)
        {
            sb.Append("语义: 无语义叠加层\n");
        }
        else
        {
            sb.Append("语义标注: ");
            sb.Append(string.Join(", ", overlay.Annotations
                .GroupBy(a => $"{a.Class}/{a.Provenance}", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}×{g.Count()}")));
            sb.Append('\n');
            if (overlay.Mappings.Count > 0)
            {
                sb.Append("人工图层映射: ");
                sb.Append(string.Join(", ", overlay.Mappings
                    .OrderBy(m => m.LayerName, StringComparer.Ordinal)
                    .Select(m => $"{m.LayerName}→{m.Class}(HumanConfirmed)")));
                sb.Append('\n');
            }
        }

        string result = sb.ToString();
        if (result.Length > maxChars)
            result = result[..maxChars] + "…[截断]";
        return result;
    }
}