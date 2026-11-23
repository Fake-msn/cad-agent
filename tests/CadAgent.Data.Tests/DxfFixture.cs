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
    /// R11 合成天正 DXF 夹具：DxfWriter 不支持 UnknownEntity 写 → 手写文本注入。
    /// 基础文档含一条原生 Line；ENTITIES 段头后注入 TCH_WALL 命名实体；withClass=true 时
    /// 另在 ENTITIES 所属 SECTION 前注入 CLASSES 段（TCH_WALL 类）→ DxfReader 通知
    /// "Entity not supported read as an UnknownEntity: TCH_WALL"（v3.8.35 实证）；
    /// withClass=false（类表无登记）→ "Entity not supported: TCH_WALL"。
    /// KeepUnknownEntities=false 默认 → TCH 丢弃出对象图（Entities 仅 Line），通知是唯一证据通道。
    /// </summary>
    public static string CreateTempTchDxf(bool withClass = true)
    {
        var doc = new CadDocument();
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 100) });
        string path = Path.Combine(Path.GetTempPath(), $"cadagent-test-{Guid.NewGuid():N}.dxf");
        using (var fs = File.Create(path)) DxfWriter.Write(fs, doc);

        var lines = File.ReadAllLines(path).ToList();
        int entIdx = lines.FindIndex(l => l.Trim() == "ENTITIES");
        // ENTITIES 段头后插入 TCH_WALL 实体（group code/value 对；句柄 2A 仅占位，读回即丢弃）
        lines.InsertRange(entIdx + 1, new[] { "  0", "TCH_WALL", "  5", "2A", "100", "AcDbEntity", "  8", "0" });
        if (withClass)
        {
            // 定位 ENTITIES 所属 SECTION 起点（其前最近的 "SECTION" 行），在 group code 0 前插 CLASSES 段
            int secIdx = entIdx;
            while (secIdx > 0 && lines[secIdx].Trim() != "SECTION") secIdx--;
            lines.InsertRange(secIdx - 1, new[]
            {
                "  0", "SECTION", "  2", "CLASSES",
                "  0", "CLASS", "  1", "TCH_WALL", "  2", "TCH_WALL", "  3", "TArch",
                " 90", "0", " 91", "0", "280", "0", "281", "1",
                "  0", "ENDSEC",
            });
        }
        File.WriteAllLines(path, lines);
        return path;
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
