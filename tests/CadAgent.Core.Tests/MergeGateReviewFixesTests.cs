using CadAgent.Core;
using CadAgent.Core.Audit;

namespace CadAgent.Core.Tests;

/// <summary>
/// 合并前审查修复回归（2026-11-21）：Warning#2 锚滞后 Stale；Warning#4 skill 符号 file 穿越守卫。
/// </summary>
public class MergeGateReviewFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gate-{Guid.NewGuid():N}");
    public MergeGateReviewFixesTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private static AuditEntry Entry(string kind) =>
        new(DateTimeOffset.UtcNow, "T\\u", "Draw", "s1", "test", kind, "t", "{}", "", "", "", "");

    [Fact]
    public void AnchorLag_DbAheadOfAnchor_StaleNotOk()
    {
        var anchor = new AnchorService(_dir);
        using var store = new SqliteAuditStore(Path.Combine(_dir, "a.db"), anchor);
        store.Append(Entry("k1"));
        var rec2 = store.Append(Entry("k2"));
        Assert.Equal(AnchorStatus.Ok, anchor.Verify(rec2.Seq, rec2.Hash));
        // 模拟 commit 与 anchor.Update 间崩溃窗口：锚停留在 seq1
        anchor.Update(1, store.ReadAll()[0].Hash);
        Assert.Equal(AnchorStatus.Stale, anchor.Verify(rec2.Seq, rec2.Hash));
    }

    [Fact]
    public void SymbolFile_Traversal_Rejected()
    {
        string pkg = Path.Combine(_dir, "pkg");
        Directory.CreateDirectory(Path.Combine(pkg, "symbols"));
        File.WriteAllText(Path.Combine(pkg, "skill.yaml"),
            "name: 穿越测试\nversion: \"1.0.0\"\ndiscipline: electrical\ndrawingType: any\ndescription: x\n");
        File.WriteAllText(Path.Combine(pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 恶, file: ../evil.dxf, class: Auxiliary, description: x }\n");
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(pkg));
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void SymbolFile_Rooted_Rejected()
    {
        string pkg = Path.Combine(_dir, "pkg2");
        Directory.CreateDirectory(Path.Combine(pkg, "symbols"));
        File.WriteAllText(Path.Combine(pkg, "skill.yaml"),
            "name:  rooted测试\nversion: \"1.0.0\"\ndiscipline: electrical\ndrawingType: any\ndescription: x\n");
        File.WriteAllText(Path.Combine(pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 恶, file: /etc/passwd, class: Auxiliary, description: x }\n");
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(pkg));
    }

    [Fact]
    public void SymbolFile_PackageRelativeSubdir_Allowed()
    {
        string pkg = Path.Combine(_dir, "pkg3");
        Directory.CreateDirectory(Path.Combine(pkg, "symbols"));
        File.WriteAllText(Path.Combine(pkg, "skill.yaml"),
            "name: 子目录测试\nversion: \"1.0.0\"\ndiscipline: electrical\ndrawingType: any\ndescription: x\n");
        File.WriteAllText(Path.Combine(pkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 好, file: sub/ok.dxf, class: Auxiliary, description: x }\n");
        var loaded = SkillLoader.LoadDirectory(pkg);
        Assert.Single(loaded.Symbols);
    }
}
