namespace CadAgent.Cli.Tests;

/// <summary>
/// 3f：50786 表4.1.2 强电符号矢量化——目录与 DXF 一致性读回（随批增长，T7 定稿 164）。
/// 证据链：MinerU vlm 重转整表裁切→目视转录→矢量重建（近似几何注记在 symbols.yaml）。
/// </summary>
public class ElectricalSymbolsTests
{
    private const int Expected = 27;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Catalog_MatchesDxfFiles_ReadBack()
    {
        string symDir = Path.Combine(RepoRoot(), "examples", "skills", "electrical", "symbols");
        var files = Directory.GetFiles(symDir, "elec-*.dxf").OrderBy(f => f).ToList();
        Assert.Equal(Expected, files.Count);
        string yaml = File.ReadAllText(Path.Combine(symDir, "symbols.yaml"));
        int entries = yaml.Split('\n').Count(l => l.TrimStart().StartsWith("- { name:", StringComparison.Ordinal));
        Assert.Equal(Expected, entries);
        foreach (string f in files)
        {
            using var fs = File.OpenRead(f);
            var doc = new ACadSharp.IO.DxfReader(fs).Read();
            Assert.True(doc.Entities.Count() > 0, Path.GetFileName(f));
            Assert.Contains(Path.GetFileName(f), yaml);
        }
    }

    [Fact]
    public void ElectricalPackage_StillValidates()
    {
        string skillDir = Path.Combine(RepoRoot(), "examples", "skills", "electrical");
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "skill", "validate", skillDir }, outW, errW);
        Assert.Equal(0, code);
        Assert.Empty(errW.ToString());
    }
}
