using CadAgent.Core;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 2b-ii-b Task 9: symbols metadata (symbols/symbols.yaml) additive loading.
/// Schema {symbols: [{name, file, class, description}]} — strict keys (unknown
/// key rejected, YamlDotNet default), name/file non-empty, class a legal
/// SemanticClass enum name (ignoreCase; all-digit strings rejected per T2
/// precedent), duplicate name OrdinalIgnoreCase rejected, description optional
/// (→ ""). Missing dir/file → empty; malformed YAML → InvalidSkillException
/// with inner preserved. File existence NOT enforced — cross-document symbol
/// import is deferred to plan 3.
/// </summary>
public class SymbolMetaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skill-symbols-{Guid.NewGuid():N}");

    public SymbolMetaTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "skill.yaml"), """
            name: symbols-skill
            version: "1.0.0"
            discipline: electrical
            drawingType: any
            description: symbol metadata test
            """);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private void WriteSymbols(string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "symbols"));
        File.WriteAllText(Path.Combine(_dir, "symbols", "symbols.yaml"), yaml);
    }

    private const string ValidYaml = """
        symbols:
          - name: luminaire-ceiling
            file: symbols/luminaire-ceiling.dxf
            class: Auxiliary
            description: ceiling luminaire symbol
          - name: switch-single
            file: symbols/switch-single.dxf
            class: annotation
            description: single-pole switch
        """;

    [Fact]
    public void Load_Symbols_ParsesMetadata_ClassIgnoreCase()
    {
        WriteSymbols(ValidYaml);
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Equal(2, pkg.Symbols.Count);

        Assert.Equal("luminaire-ceiling", pkg.Symbols[0].Name);
        Assert.Equal("symbols/luminaire-ceiling.dxf", pkg.Symbols[0].File);
        Assert.Equal(SemanticClass.Auxiliary, pkg.Symbols[0].Class);
        Assert.Equal("ceiling luminaire symbol", pkg.Symbols[0].Description);

        // class ignoreCase: "annotation" → Annotation.
        Assert.Equal("switch-single", pkg.Symbols[1].Name);
        Assert.Equal(SemanticClass.Annotation, pkg.Symbols[1].Class);
    }

    [Fact]
    public void Load_DescriptionOmitted_EmptyString()
    {
        WriteSymbols("""
            symbols:
              - name: lum
                file: symbols/lum.dxf
                class: Auxiliary
            """);
        var s = Assert.Single(SkillLoader.LoadDirectory(_dir).Symbols);
        Assert.Equal("", s.Description);
    }

    [Fact]
    public void Load_FileExistenceNotEnforced()
    {
        // file points at a nonexistent .dxf — metadata-only validation in this
        // slice; cross-document import (and existence checks) arrive in plan 3.
        WriteSymbols(ValidYaml);
        Assert.False(File.Exists(Path.Combine(_dir, "symbols", "luminaire-ceiling.dxf")));
        Assert.Equal(2, SkillLoader.LoadDirectory(_dir).Symbols.Count);
    }

    [Fact]
    public void Load_NoSymbolsDirOrFile_Empty()
    {
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Symbols);
        Directory.CreateDirectory(Path.Combine(_dir, "symbols"));
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Symbols);
    }

    [Fact]
    public void Load_EmptyDocumentOrEmptyList_Empty()
    {
        WriteSymbols("");
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Symbols);
        WriteSymbols("symbols: []\n");
        Assert.Empty(SkillLoader.LoadDirectory(_dir).Symbols);
    }

    [Fact]
    public void Load_InvalidClass_Throws()
    {
        WriteSymbols("""
            symbols:
              - name: lum
                file: symbols/lum.dxf
                class: NotAClass
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("class", ex.Message);
        Assert.Contains("symbols.yaml", ex.Message);
    }

    [Fact]
    public void Load_NumericClassString_Throws()
    {
        // T2 strict parity: Enum.TryParse would accept "3" as a defined numeric
        // value — class must be an enum NAME (ignoreCase), digits rejected.
        WriteSymbols("""
            symbols:
              - name: lum
                file: symbols/lum.dxf
                class: "3"
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("class", ex.Message);
    }

    [Fact]
    public void Load_EmptyClass_Throws()
    {
        WriteSymbols("""
            symbols:
              - name: lum
                file: symbols/lum.dxf
                class: ""
            """);
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
    }

    [Fact]
    public void Load_EmptyName_Throws()
    {
        WriteSymbols("""
            symbols:
              - name: ""
                file: symbols/lum.dxf
                class: Auxiliary
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Load_EmptyFile_Throws()
    {
        WriteSymbols("""
            symbols:
              - name: lum
                file: ""
                class: Auxiliary
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("file", ex.Message);
    }

    [Fact]
    public void Load_DuplicateName_CaseInsensitive_Throws()
    {
        WriteSymbols("""
            symbols:
              - name: LUM
                file: symbols/lum.dxf
                class: Auxiliary
              - name: lum
                file: symbols/lum2.dxf
                class: Annotation
            """);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("lum", ex.Message);
        Assert.Contains("duplicate", ex.Message);
    }

    [Fact]
    public void Load_MalformedYaml_ThrowsWithInner()
    {
        WriteSymbols("symbols: [unclosed\n");
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("symbols.yaml", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Load_UnknownEntryKey_Throws()
    {
        // strict schema parity with skill.yaml/aliases/steps: unknown keys rejected.
        WriteSymbols("""
            symbols:
              - name: lum
                file: symbols/lum.dxf
                class: Auxiliary
                bogusKey: x
            """);
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
    }

    [Fact]
    public void SymbolMeta_RecordShape()
    {
        var meta = new SymbolMeta("lum", "symbols/lum.dxf", SemanticClass.Auxiliary, "desc");
        Assert.Equal("lum", meta.Name);
        Assert.Equal("symbols/lum.dxf", meta.File);
        Assert.Equal(SemanticClass.Auxiliary, meta.Class);
        Assert.Equal("desc", meta.Description);
        Assert.Equal(meta, new SymbolMeta("lum", "symbols/lum.dxf", SemanticClass.Auxiliary, "desc"));
    }
}
