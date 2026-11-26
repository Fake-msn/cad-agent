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

    // 通知文本三变体（ACadSharp v3.8.35 源码 + 合成 DXF 实证核实）：
    // DWG（DwgReader）："Unlisted object with DXF name {name} has been read as an UnknownEntity"；
    // DXF 类表登记（DxfSectionReaderBase:260）："Entity not supported read as an UnknownEntity: {name}"；
    // DXF 类表无登记（DxfSectionReaderBase:265）："Entity not supported: {name}"。
    // DXF 两变体共用 "Entity not supported(?: read as an UnknownEntity)?: " 前缀（可选段区分）。
    private static readonly Regex UnknownEntityMsg =
        new(@"Unlisted object with DXF name (\S+) has been read as an UnknownEntity|Entity not supported(?: read as an UnknownEntity)?: (\S+)",
            RegexOptions.Compiled);

    public static UnparsedCensus FromNotifications(IEnumerable<string> messages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            var match = UnknownEntityMsg.Match(m);
            if (!match.Success) continue;
            // alternation 两分支各占一组：DWG→组1，DXF→组2（取成功者）
            string name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            counts.TryGetValue(name, out int c);
            counts[name] = c + 1;
        }
        return new UnparsedCensus(counts.OrderByDescending(kv => kv.Value)
            .Select(kv => new UnparsedClassBrief(kv.Key, kv.Value)).ToList());
    }
}

/// <summary>
/// census sidecar（.cadagent/unparsed.json）：working copy 丢失 TCH 对象后二次加载仍可见。
/// v2（项3 §5.1）：增 SourceSize/SourceMtimeUtc/VerifiedEmpty——空 census 的「已验证」
/// 指纹（源哈希+大小+mtime 全匹配才信任），命中的 working 载入不重读源 DWG（R9 语义扩展）。
/// 旧 JSON 缺新字段 → 默认 false/0 → 不信任（安全回退 R9）。
/// 已知限制（单文件多源互斥）：一个项目仅一份 sidecar——第二源 Save 覆盖第一源，
/// Load 靠 SourcePath+SourceHash 匹配诚实返回 Empty；多源场景 census 仅最近源有效。
/// </summary>
public static class UnparsedCensusStore
{
    private sealed record Sidecar(string SourcePath, string SourceHash, IReadOnlyList<UnparsedClassBrief> Classes,
        long SourceSize = 0, DateTime SourceMtimeUtc = default, bool VerifiedEmpty = false);

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

    /// <summary>
    /// 信任载入（v2）：路径 + 源哈希 + size + mtime 全匹配才算命中；<paramref name="verifiedEmpty"/>
    /// 仅在 VerifiedEmpty=true 且 census 为空且全匹配时为 true（调用方据此跳过源 DWG 重读）。
    /// 损坏/不匹配 → Empty + false（既有 fail-closed 语义：调用方回退 R9 重建）。
    /// 旧 v1 sidecar（无 size/mtime）hash 匹配仍返回其 census（Load 行为不变），但不构成「已验证空」。
    /// </summary>
    public static UnparsedCensus LoadTrusted(string projectDir, string sourcePath, out bool verifiedEmpty)
    {
        verifiedEmpty = false;
        string p = SidecarPath(projectDir);
        if (!File.Exists(p) || !File.Exists(sourcePath)) return UnparsedCensus.Empty;
        try
        {
            var sc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(p));
            if (sc == null || sc.SourcePath != Path.GetFullPath(sourcePath)) return UnparsedCensus.Empty;

            var fi = new FileInfo(sourcePath);
            bool sizeMtimeMatch = sc.SourceSize == fi.Length && sc.SourceMtimeUtc == fi.LastWriteTimeUtc;
            // 空 census + 指纹不符：信任必失败，无需付源哈希成本（调用方回退 R9 重读源）。
            if (!sizeMtimeMatch && (sc.Classes is null || sc.Classes.Count == 0)) return UnparsedCensus.Empty;

            if (sc.SourceHash != HashOf(sourcePath) || sc.Classes == null || sc.Classes.Any(c => c.Count < 0))
                return UnparsedCensus.Empty;
            var census = new UnparsedCensus(sc.Classes);
            verifiedEmpty = sc.VerifiedEmpty && census.TotalCount == 0 && sizeMtimeMatch;
            return census;
        }
        catch { return UnparsedCensus.Empty; } // 损坏 sidecar 降级 Empty（闸口另有图内 Unknowns 兜底）
    }

    public static void Save(string projectDir, string sourcePath, UnparsedCensus census)
    {
        string p = SidecarPath(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var fi = new FileInfo(sourcePath);
        // VerifiedEmpty = 本次 census 来自源（首载或 R9 重建）并为空 → 下次载入可信任 Empty。
        var sc = new Sidecar(Path.GetFullPath(sourcePath), HashOf(sourcePath), census.Classes,
            fi.Length, fi.LastWriteTimeUtc, census.TotalCount == 0);
        // 原子写（T3 闭合 T1 deferred minor，对齐 SaveWorking 约定）：先写 tmp 再 Move——
        // 写中途崩溃不损既有 sidecar（census 证据不因半截 JSON 降级 Empty）。
        // tmp 名唯一：并发写同一项目（测试并行/多进程）不互踩临时文件。
        string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sc));
        File.Move(tmp, p, overwrite: true);
    }
}
