using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadAgent.Core.Cad;

/// <summary>厂商支持状态——Verified 须真机测试证据（spec §11）。</summary>
public enum VendorStatus { Verified, CompatibleUnverified, Unsupported }

/// <summary>厂商支持矩阵条目。</summary>
public sealed record VendorEntry(
    string Vendor,
    string VersionRange,
    string AdapterVersion,
    VendorStatus Status,
    string? VerifiedAt,
    string TestCoverage);

/// <summary>
/// 厂商支持矩阵（随产品清单 vendor-support.json）；导出 gating 消费。
/// 版本匹配简化：VersionRange 为单版本（前缀匹配）或 "lo-hi" 区间（首段前缀）。
/// </summary>
public sealed class VendorMatrix
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<VendorEntry> Entries { get; }

    private VendorMatrix(IReadOnlyList<VendorEntry> entries) => Entries = entries;

    public static VendorMatrix Load(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return new VendorMatrix(Array.Empty<VendorEntry>());
        var doc = JsonSerializer.Deserialize<MatrixDoc>(File.ReadAllText(jsonPath), JsonOpts);
        return new VendorMatrix(doc?.Entries ?? new List<VendorEntry>());
    }

    public VendorEntry? Find(string vendor, string version) =>
        Entries.FirstOrDefault(e =>
            string.Equals(e.Vendor, vendor, StringComparison.OrdinalIgnoreCase) && VersionMatches(e.VersionRange, version));

    public bool IsVerified(string vendor, string version) =>
        Find(vendor, version)?.Status == VendorStatus.Verified;

    private static bool VersionMatches(string range, string version)
    {
        string lo = range.Split('-')[0];
        return version.StartsWith(lo, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MatrixDoc
    {
        public List<VendorEntry> Entries { get; set; } = new();
    }
}