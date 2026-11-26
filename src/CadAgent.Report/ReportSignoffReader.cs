using System.Text.Json;

namespace CadAgent.Report;

/// <summary>
/// 签认文件（.cadagent/signoffs.json，View 侧 ReviewSignoffStore 写出的格式）的只读投影。
/// CLI 与 Report 项目不依赖 View，故按磁盘契约读取：缺失/结构不符→空列表；
/// 同 TxId+Operator 多条→保留末条（与 Store.Load 去重口径一致）。
/// </summary>
public static class ReportSignoffReader
{
    private sealed class FileShape
    {
        public List<ReportSignoff> Items { get; set; } = new();
    }

    public static IReadOnlyList<ReportSignoff> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<ReportSignoff>();
        FileShape? file;
        try
        {
            file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
        }
        catch
        {
            return Array.Empty<ReportSignoff>(); // 损坏降级：报告如实显示"无签认记录"
        }
        return (file?.Items ?? new List<ReportSignoff>())
            .GroupBy(x => (x.TxId, x.Operator), TxOperatorComparer.Instance)
            .Select(g => g.Last())
            .ToList();
    }

    private sealed class TxOperatorComparer : IEqualityComparer<(string TxId, string Operator)>
    {
        public static readonly TxOperatorComparer Instance = new();

        public bool Equals((string TxId, string Operator) x, (string TxId, string Operator) y) =>
            x.TxId == y.TxId && string.Equals(x.Operator, y.Operator, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string TxId, string Operator) obj) =>
            HashCode.Combine(obj.TxId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Operator));
    }
}
