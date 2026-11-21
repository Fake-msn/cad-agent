using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace CadAgent.Data.Tests;

public static class DxfFixture
{
    public static string CreateTempDxf(Action<CadDocument> populate)
    {
        var doc = new CadDocument();
        populate(doc);
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-test-{Guid.NewGuid():N}.dxf");
        using var fs = File.Create(path);
        DxfWriter.Write(fs, doc);
        return path;
    }

    /// <summary>
    /// Real .dwg fixture via ACadSharp DwgWriter (3.8.35). Used by the
    /// working-copy persistence test; if the writer/reader roundtrip proves
    /// unusable the persistence test falls back to the pure load-decision
    /// function (live .dwg corpus deferred to Plan 3).
    /// </summary>
    public static string CreateTempDwg(Action<CadDocument> populate)
    {
        var doc = new CadDocument();
        populate(doc);
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-test-{Guid.NewGuid():N}.dwg");
        using (var fs = File.Create(path))
        using (var writer = new DwgWriter(fs, doc))
        {
            writer.Write();
        }
        return path;
    }

    public static void AddSampleLine(CadDocument doc)
    {
        var line = new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 100) };
        doc.Entities.Add(line);
    }

    /// <summary>
    /// BlockRecord "LUM" with one diagonal line inside — insert-block fixture
    /// (2b-ii-b Task 9). ACadSharp 3.8.35: new BlockRecord(name) + Entities.Add
    /// + Doc.BlockRecords.Add; survives DxfWriter/DxfReader roundtrip.
    /// </summary>
    public static void AddLumBlock(CadDocument doc)
    {
        var block = new BlockRecord("LUM");
        block.Entities.Add(new Line { StartPoint = new XYZ(-1, -1, 0), EndPoint = new XYZ(1, 1, 0) });
        doc.BlockRecords.Add(block);
    }
}