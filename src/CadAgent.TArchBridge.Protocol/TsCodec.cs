using System.Collections.Generic;
using System.Text;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// TSV 单行编解码：列以未转义 <c>\t</c> 分隔，反斜杠转义 <c>\\</c> <c>\t</c> <c>\n</c>。
/// </summary>
public static class TsCodec
{
    /// <summary>转义字段值，使其可安全落入单个 TSV 单元格。</summary>
    public static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");

    /// <summary>还原 <see cref="Escape"/> 的转义；未知转义序列按原字符保留。</summary>
    public static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 't' => '\t', 'n' => '\n', '\\' => '\\', _ => n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>按未转义 <c>\t</c> 切列，并对每列 <see cref="Unescape"/>。</summary>
    public static string[] SplitLine(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { sb.Append(line[i]).Append(line[++i]); }
            else if (line[i] == '\t') { parts.Add(Unescape(sb.ToString())); sb.Clear(); }
            else sb.Append(line[i]);
        }
        parts.Add(Unescape(sb.ToString()));
        return parts.ToArray();
    }
}
