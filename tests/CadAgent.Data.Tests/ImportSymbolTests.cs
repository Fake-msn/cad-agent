using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3h T1：insert-symbol 跨文档符号导入——skill 包符号名解析→源 DXF 白名单重建克隆→
/// 建块/复用块→Insert。块复用幂等；中文块名往返；未知符号/缺文件/不支持类型响亮失败。
/// </summary>
public class ImportSymbolTests : IDisposable
{
    private readonly string _path;
    private readonly string _tempPkg;
    private readonly ImportSymbolHandler _handler;

    public ImportSymbolTests()
    {
        _path = DxfFixture.CreateTempDxf(_ => { });
        _tempPkg = Path.Combine(Path.GetTempPath(), $"imppkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempPkg, "symbols"));
        File.WriteAllText(Path.Combine(_tempPkg, "skill.yaml"),
            "name: 导入测试包\nversion: \"1.0.0\"\ndiscipline: electrical\ndrawingType: any\ndescription: 测试\n");
        string badDxf = Path.Combine(_tempPkg, "symbols", "bad.dxf");
        var bad = new CadDocument();
        bad.Entities.Add(new MText { Value = "unsupported" });
        using (var fs = File.Create(badDxf)) DxfWriter.Write(fs, bad);
        File.WriteAllText(Path.Combine(_tempPkg, "symbols", "symbols.yaml"),
            "symbols:\n  - { name: 缺文件符号, file: missing.dxf, class: Auxiliary, description: x }\n  - { name: 不支持符号, file: bad.dxf, class: Auxiliary, description: x }\n");
        var packages = new[]
        {
            SkillLoader.LoadDirectory(ElectricalDir()),
            SkillLoader.LoadDirectory(_tempPkg),
        };
        _handler = new ImportSymbolHandler(packages);
    }

    public void Dispose()
    {
        File.Delete(_path);
        Directory.Delete(_tempPkg, true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ElectricalDir() =>
        Path.Combine(RepoRoot(), "examples", "skills", "electrical");

    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Import_Terminal_CreatesBlockAndInsert()
    {
        var s = DrawingSession.Load(_path);
        var r = _handler.Apply(s, P("""{"symbol":"端子","x":10,"y":20}"""));
        Assert.True(r.Ok);
        Assert.True(s.Doc.BlockRecords.TryGetValue("端子", out var block));
        Assert.Single(block.Entities.OfType<Circle>());
        var insert = Assert.Single(s.Doc.Entities.OfType<Insert>());
        Assert.Equal(10, insert.InsertPoint.X);
        Assert.Equal(20, insert.InsertPoint.Y);
        Assert.Equal(insert.Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void Import_Twice_ReusesBlock()
    {
        var s = DrawingSession.Load(_path);
        Assert.True(_handler.Apply(s, P("""{"symbol":"端子","x":0,"y":0}""")).Ok);
        Assert.True(_handler.Apply(s, P("""{"symbol":"端子","x":5,"y":5}""")).Ok);
        Assert.Equal(1, s.Doc.BlockRecords.Count(b => b.Name == "端子"));
        Assert.Equal(2, s.Doc.Entities.OfType<Insert>().Count());
    }

    [Fact]
    public void Import_ScaleRotationLayer()
    {
        var s = DrawingSession.Load(_path);
        var r = _handler.Apply(s, P("""{"symbol":"端子","x":0,"y":0,"scale":2,"rotation":1.5708,"layer":"E-LITE"}"""));
        Assert.True(r.Ok);
        var insert = Assert.Single(s.Doc.Entities.OfType<Insert>());
        Assert.Equal(2, insert.XScale);
        Assert.Equal(1.5708, insert.Rotation, 4);
        Assert.Equal("E-LITE", insert.Layer?.Name);
    }

    [Fact]
    public void Import_ChineseBlockName_Roundtrip()
    {
        var s = DrawingSession.Load(_path);
        Assert.True(_handler.Apply(s, P("""{"symbol":"端子","x":1,"y":2}""")).Ok);
        s.SaveWorking();
        var re = DrawingSession.Load(_path);
        Assert.True(re.Doc.BlockRecords.TryGetValue("端子", out var block));
        Assert.Single(block.Entities.OfType<Circle>());
        Assert.Single(re.Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void Import_UnknownSymbol_Fails()
    {
        var s = DrawingSession.Load(_path);
        var r = _handler.Apply(s, P("""{"symbol":"不存在的符号","x":0,"y":0}"""));
        Assert.False(r.Ok);
        Assert.Contains("符号不存在", r.Error);
    }

    [Fact]
    public void Import_MissingFile_Fails()
    {
        var s = DrawingSession.Load(_path);
        var r = _handler.Apply(s, P("""{"symbol":"缺文件符号","x":0,"y":0}"""));
        Assert.False(r.Ok);
        Assert.Contains("符号文件缺失", r.Error);
    }

    [Fact]
    public void Import_UnsupportedType_FailsLoudly()
    {
        var s = DrawingSession.Load(_path);
        var r = _handler.Apply(s, P("""{"symbol":"不支持符号","x":0,"y":0}"""));
        Assert.False(r.Ok);
        Assert.Contains("不支持的实体类型", r.Error);
        Assert.Empty(s.Doc.Entities.OfType<Insert>());
    }

    [Fact]
    public void Kind_IsInsertSymbol() => Assert.Equal("insert-symbol", _handler.Kind);
}
