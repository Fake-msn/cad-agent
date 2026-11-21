using System.IO.Compression;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// CUIX 健康金丝雀（2026-11-22 事故）：真机轮后用户 profile 的 ZWCAD.CUIX
/// 必须 zip 完整；损坏即红，指向 outputs/cuix-evidence-20261122 留证流程。
/// </summary>
[Collection("ZwcadLive")]
public class CuixHealthTests
{
    [Fact]
    public void UserProfileCuix_ZipIntact()
    {
        string prof = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZWSOFT", "ZWCAD", "2025", "zh-CN", "Support", "ZWCAD.CUIX");
        if (!File.Exists(prof)) return; // 无中望 profile 的环境跳过
        using var z = ZipFile.OpenRead(prof);
        Assert.True(z.Entries.Count > 0);
        foreach (var e in z.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms); // CRC 校验在读时触发
        }
    }
}