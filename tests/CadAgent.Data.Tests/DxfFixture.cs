using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
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

    public static void AddSampleLine(CadDocument doc)
    {
        var line = new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 100) };
        doc.Entities.Add(line);
    }
}