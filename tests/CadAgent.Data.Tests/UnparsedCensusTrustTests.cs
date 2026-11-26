using System.Security.Cryptography;
using System.Text.Json;
using CadAgent.Data;

namespace CadAgent.Data.Tests;

/// <summary>
/// 项3 Task 5：sidecar v2 空 census 信任（R9 语义扩展）。
/// 信任条件 = 路径 + 源哈希 + size + mtime 全匹配 且 sidecar.VerifiedEmpty=true；
/// 任一项不符 → 不信任（Empty + verifiedEmpty=false），调用方回退既有 R9（重读源 DWG）。
/// 信任命中 → Load(trustEmptySource:true) 不读源（源缺失亦可），二次打开 Green 图免重读。
/// </summary>
public class UnparsedCensusTrustTests
{
    private static string NewProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "census-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static UnparsedCensus NonEmpty() =>
        new(new[] { new UnparsedClassBrief("TCH_WALL", 2) });

    [Fact]
    public void VerifiedEmpty_TrustedLoad_SourceDeleted_TrustSucceeds_UntrustedThrows()
    {
        string dir = NewProjectDir();
        try
        {
            string dwg = Path.Combine(dir, "a.dwg");
            string tmp = DxfFixture.CreateTempDwg(DxfFixture.AddSampleLine);
            File.Move(tmp, dwg);

            var first = DrawingSession.Load(dwg); // 首载物化 working（合成源无 TCH → census Empty）
            Assert.Equal(0, first.Unparsed.TotalCount);
            UnparsedCensusStore.Save(dir, dwg, first.Unparsed); // VerifiedEmpty=true

            var restored = UnparsedCensusStore.LoadTrusted(dir, dwg, out bool verifiedEmpty);
            Assert.True(verifiedEmpty);
            Assert.Equal(0, restored.TotalCount);

            // 源删除 → LoadTrusted 无法核验（hash/size/mtime 不可得）→ 不信任（fail-closed）
            File.Delete(dwg);
            Assert.Equal(0, UnparsedCensusStore.LoadTrusted(dir, dwg, out bool afterDelete).TotalCount);
            Assert.False(afterDelete);

            // 核心信任路径：调用方已信任空 census → 不读源直接 Empty（源缺失也成功）。
            var session = DrawingSession.Load(dwg, UnparsedCensus.Empty, trustEmptySource: true);
            Assert.Equal(0, session.Unparsed.TotalCount);
            Assert.Equal(DrawingSession.WorkingPathFor(dwg), session.SourcePath);
            Assert.Single(session.Summarize().Entities); // working 仍是持久编辑态

            // 未信任同条件 → 既有 R9 fail-closed（不得假 Green 放行）。
            var ex = Assert.Throws<InvalidOperationException>(
                () => DrawingSession.Load(dwg, UnparsedCensus.Empty, trustEmptySource: false));
            Assert.Contains("无法核验完整性", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadTrusted_SourceContentChanged_SameSizeSameMtime_NotTrusted()
    {
        string dir = NewProjectDir();
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            DateTime mtime = File.GetLastWriteTimeUtc(src);
            UnparsedCensusStore.Save(dir, src, UnparsedCensus.Empty);
            Assert.True(UnparsedCensusStore.LoadTrusted(dir, src, out bool trusted).TotalCount == 0 && trusted);

            // 同长度内容替换 + 还原 mtime：只有源哈希可辨（size/mtime 全等）→ 必须不信任。
            File.WriteAllText(src, "v2");
            File.SetLastWriteTimeUtc(src, mtime);
            Assert.Equal(0, UnparsedCensusStore.LoadTrusted(dir, src, out bool afterHash).TotalCount);
            Assert.False(afterHash);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadTrusted_MtimeOnlyChanged_NotTrusted()
    {
        string dir = NewProjectDir();
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "same-bytes");
            UnparsedCensusStore.Save(dir, src, UnparsedCensus.Empty);
            Assert.True(UnparsedCensusStore.LoadTrusted(dir, src, out bool trusted) is { TotalCount: 0 } && trusted);

            File.SetLastWriteTimeUtc(src, File.GetLastWriteTimeUtc(src).AddHours(1));
            UnparsedCensusStore.LoadTrusted(dir, src, out bool afterMtime);
            Assert.False(afterMtime);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadTrusted_OldSidecarWithoutNewFields_NotTrusted()
    {
        // v1 sidecar（仅 SourcePath/SourceHash/Classes）→ 新字段默认 false/0 → 不信任（行为=现状，安全回退 R9）。
        string dir = NewProjectDir();
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(src)));
            string sidecar = Path.Combine(dir, ".cadagent", "unparsed.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.WriteAllText(sidecar,
                $"{{\"SourcePath\":{JsonSerializer.Serialize(Path.GetFullPath(src))},\"SourceHash\":\"{hash}\",\"Classes\":[]}}");

            Assert.Equal(0, UnparsedCensusStore.LoadTrusted(dir, src, out bool trusted).TotalCount);
            Assert.False(trusted);
            // 旧版 Load 语义不变（path+hash 匹配即取 census）——非空旧 sidecar 仍可读出。
            File.WriteAllText(sidecar,
                $"{{\"SourcePath\":{JsonSerializer.Serialize(Path.GetFullPath(src))},\"SourceHash\":\"{hash}\"," +
                "\"Classes\":[{\"CppClassName\":\"TCH_WALL\",\"Count\":2}]}");
            Assert.Equal(2, UnparsedCensusStore.Load(dir, src).TotalCount);
            // 但旧格式不构成"已验证空"信任：非空 census 也不得标记 verifiedEmpty。
            Assert.Equal(2, UnparsedCensusStore.LoadTrusted(dir, src, out bool trustedOld).TotalCount);
            Assert.False(trustedOld);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_WritesVerifiedEmptyAndSourceFingerprint()
    {
        string dir = NewProjectDir();
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            // 固定时间戳（秒级，免受文件系统取整影响）——避免与 Save 时刻竞态。
            DateTime mtime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(src, mtime);
            string sidecar = Path.Combine(dir, ".cadagent", "unparsed.json");

            UnparsedCensusStore.Save(dir, src, UnparsedCensus.Empty);
            using (var doc = JsonDocument.Parse(File.ReadAllText(sidecar)))
            {
                var root = doc.RootElement;
                Assert.True(root.GetProperty("VerifiedEmpty").GetBoolean());
                Assert.Equal(new FileInfo(src).Length, root.GetProperty("SourceSize").GetInt64());
                Assert.Equal(mtime, root.GetProperty("SourceMtimeUtc").GetDateTime().ToUniversalTime());
                // Producer：仅可观测（载入忽略、不参与信任）；伪造它不改变信任判定。
                Assert.StartsWith("CadAgent/", root.GetProperty("Producer").GetString());
            }

            UnparsedCensusStore.Save(dir, src, NonEmpty());
            using (var doc = JsonDocument.Parse(File.ReadAllText(sidecar)))
                Assert.False(doc.RootElement.GetProperty("VerifiedEmpty").GetBoolean());
            Assert.Equal(2, UnparsedCensusStore.LoadTrusted(dir, src, out bool trusted).TotalCount);
            Assert.False(trusted);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_CleansOrphanTempsOlderThanOneDay_KeepsRecent()
    {
        string dir = NewProjectDir();
        try
        {
            string src = Path.Combine(dir, "a.dwg");
            File.WriteAllText(src, "v1");
            string agentDir = Path.Combine(dir, ".cadagent");
            Directory.CreateDirectory(agentDir);
            string stale = Path.Combine(agentDir, "unparsed.json.stale.tmp");
            string recent = Path.Combine(agentDir, "unparsed.json.recent.tmp");
            File.WriteAllText(stale, "{}");
            File.WriteAllText(recent, "{}");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

            UnparsedCensusStore.Save(dir, src, UnparsedCensus.Empty);

            Assert.False(File.Exists(stale)); // 崩溃遗留 >1 天 → 清掉
            Assert.True(File.Exists(recent)); // 近期 tmp（可能在写）不动
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadTrusted_PathMismatch_ReturnsEmpty()
    {
        string dir = NewProjectDir();
        try
        {
            string a = Path.Combine(dir, "a.dwg");
            string b = Path.Combine(dir, "b.dwg");
            File.WriteAllText(a, "same-bytes");
            File.WriteAllText(b, "same-bytes");
            UnparsedCensusStore.Save(dir, a, UnparsedCensus.Empty);

            Assert.Equal(0, UnparsedCensusStore.LoadTrusted(dir, b, out bool trusted).TotalCount);
            Assert.False(trusted);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
