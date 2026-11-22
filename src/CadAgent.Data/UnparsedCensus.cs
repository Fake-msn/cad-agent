using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadAgent.Data;

public sealed record UnparsedClassBrief(string CppClassName, int Count);

/// <summary>
/// 读取器通知 census：ACadSharp 3.8.35 将未列自定义类（天正 TCH_*）读为 UnknownEntity
/// 但不暴露进文档对象图（实证：通知数千/对象图 0）→ 准入闸口必须消费本 census 防假 Green。
/// </summary>
public sealed record UnparsedCensus(IReadOnlyList<UnparsedClassBrief> Classes)
{
    public static readonly UnparsedCensus Empty = new(Array.Empty<UnparsedClassBrief>());
    public int TotalCount => Classes.Sum(c => c.Count);

    private static readonly Regex UnknownEntityMsg =
        new(@"Unlisted object with DXF name (\S+) has been read as an UnknownEntity", RegexOptions.Compiled);

    public static UnparsedCensus FromNotifications(IEnumerable<string> messages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            var match = UnknownEntityMsg.Match(m);
            if (!match.Success) continue;
            string name = match.Groups[1].Value;
            counts.TryGetValue(name, out int c);
            counts[name] = c + 1;
        }
        return new UnparsedCensus(counts.OrderByDescending(kv => kv.Value)
            .Select(kv => new UnparsedClassBrief(kv.Key, kv.Value)).ToList());
    }
}

/// <summary>census sidecar（.cadagent/unparsed.json）：working copy 丢失 TCH 对象后二次加载仍可见。</summary>
public static class UnparsedCensusStore
{
    private sealed record Sidecar(string SourcePath, string SourceHash, IReadOnlyList<UnparsedClassBrief> Classes);

    private static string SidecarPath(string projectDir) =>
        Path.Combine(projectDir, ".cadagent", "unparsed.json");

    private static string HashOf(string sourcePath)
    {
        using var fs = File.OpenRead(sourcePath);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static UnparsedCensus Load(string projectDir, string sourcePath)
    {
        string p = SidecarPath(projectDir);
        if (!File.Exists(p) || !File.Exists(sourcePath)) return UnparsedCensus.Empty;
        try
        {
            var sc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(p));
            // Fix Round 1（R4 闭合）：JSON 合法 + path/hash 匹配但 Classes null/缺失 → TotalCount NRE；
            // 负 Count → Summarize 的 Enumerable.Repeat 抛。两者均视为损坏载荷降级 Empty。
            if (sc == null || sc.SourcePath != Path.GetFullPath(sourcePath) || sc.SourceHash != HashOf(sourcePath)
                || sc.Classes == null || sc.Classes.Any(c => c.Count < 0))
                return UnparsedCensus.Empty;
            return new UnparsedCensus(sc.Classes);
        }
        catch { return UnparsedCensus.Empty; } // 损坏 sidecar 降级 Empty（闸口另有图内 Unknowns 兜底）
    }

    public static void Save(string projectDir, string sourcePath, UnparsedCensus census)
    {
        string p = SidecarPath(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var sc = new Sidecar(Path.GetFullPath(sourcePath), HashOf(sourcePath), census.Classes);
        File.WriteAllText(p, JsonSerializer.Serialize(sc));
    }
}
