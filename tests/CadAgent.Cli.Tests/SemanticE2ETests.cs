using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Plan 2a end-to-end loop: scan → map → show → rescan → audit verify → run.
/// Fixture: WALL layer with two parallel lines d=200 (layer inference), a
/// custom-layer line (ambiguous until human-mapped), and an axis-grid pair
/// (circle r=400 + number text "1", geometry inference).
/// </summary>
public class SemanticE2ETests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"seme2e-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public SemanticE2ETests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new Layer("WALL"));
            doc.Layers.Add(new Layer("W-自定义"));
            var wall = doc.Layers.First(l => l.Name == "WALL");
            var custom = doc.Layers.First(l => l.Name == "W-自定义");
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(2000, 0), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 200), EndPoint = (XYZ)new XY(2000, 200), Layer = wall });
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 3000), EndPoint = (XYZ)new XY(500, 3000), Layer = custom });
            doc.Entities.Add(new Circle { Center = (XYZ)new XY(5000, 0), Radius = 400 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(5000, 0), Value = "1", Height = 300 });
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void FullLoop_Scan_Map_Rescan_Run_AuditGreen()
    {
        var (scanCode, scanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, scanCode);
        // Controller add-on (T7 review): exit code alone can empty-pass; the
        // overlay artifact must actually exist after the first scan.
        Assert.True(File.Exists(Path.Combine(_project, ".cadagent", "semantic.json")));
        Assert.Contains("Wall", scanOut);
        Assert.Contains("Axis", scanOut);
        Assert.Contains("W-自定义", scanOut);
        using (var scanDoc = System.Text.Json.JsonDocument.Parse(scanOut))
        {
            Assert.Equal(1, scanDoc.RootElement.GetProperty("version").GetInt32());
            var annotations = scanDoc.RootElement.GetProperty("annotations").EnumerateArray().ToList();
            // WALL 层两条线 → LayerInference Wall ×2（0.8 高于几何推断 0.6，不被降级）
            Assert.Equal(2, annotations.Count(a => a.GetProperty("class").GetString() == "Wall"));
            // 轴号圆 + 数字文本 → GeometryInference Axis ×2
            Assert.Equal(2, annotations.Count(a => a.GetProperty("class").GetString() == "Axis"));
            var scanAmbiguous = scanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
                .Select(a => a.GetProperty("layerName").GetString()).ToList();
            Assert.Contains("W-自定义", scanAmbiguous);
        }

        var (mapCode, _, _) = Run("semantic", "map", "--layer", "W-自定义", "--class", "Wall", "--drawing", _drawing);
        Assert.Equal(0, mapCode);

        var (_, showOut, _) = Run("semantic", "show");
        Assert.Contains("HumanConfirmed", showOut);

        var (rescanCode, rescanOut, _) = Run("semantic", "scan", "--drawing", _drawing);
        Assert.Equal(0, rescanCode);
        using var rescanDoc = System.Text.Json.JsonDocument.Parse(rescanOut);
        Assert.Equal(3, rescanDoc.RootElement.GetProperty("version").GetInt32()); // scan=1, map=2, rescan=3（Save 每次+1）
        var ambiguous = rescanDoc.RootElement.GetProperty("ambiguous").EnumerateArray()
            .Select(a => a.GetProperty("layerName").GetString()).ToList();
        Assert.DoesNotContain("W-自定义", ambiguous);
        // 映射保留：rescan 后自定义层实体仍是 HumanConfirmed Wall（1.0）
        var rescanAnnotations = rescanDoc.RootElement.GetProperty("annotations").EnumerateArray().ToList();
        Assert.Contains(rescanAnnotations, a =>
            a.GetProperty("class").GetString() == "Wall" &&
            a.GetProperty("provenance").GetString() == "HumanConfirmed" &&
            a.GetProperty("confidence").GetDouble() == 1.0);

        var (verifyCode, _, _) = Run("audit", "verify");
        Assert.Equal(0, verifyCode);

        string planPath = Path.Combine(_project, "plan.json");
        File.WriteAllText(planPath, """
            {"planId":"p1","steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":5,"y2":5}}]}
            """);
        var (runCode, runOut, _) = Run("run", planPath, "--drawing", _drawing);
        Assert.Equal(0, runCode);
        Assert.Contains("\"ok\":true", runOut.Replace(" ", ""));
    }
}
