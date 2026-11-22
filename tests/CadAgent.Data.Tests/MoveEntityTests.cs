using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// Task 4：move-entity 八类实体扩展。夹具镜像既有 handler 测试构造法
/// （DxfFixture.CreateTempDxf + DrawingSession.Load 真实 DXF 往返，先例
/// HandlerTests/InsertBlockTests）；首点统一 (1,2,7)——Z=7 高程保持断言
/// 意图同 MoveEntity_PreservesLineZ 先例。不支持类型仍诚实 step-failed。
/// </summary>
public class MoveEntityTests
{
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>按 kind 构造实体（首点 X=1/Y=2/Z=7 高程）；Insert 需 BlockRecord（镜像 AddLumBlock 先例）。</summary>
    private static void AddEntity(string kind, CadDocument doc)
    {
        switch (kind)
        {
            case "Circle":
                doc.Entities.Add(new Circle { Center = new XYZ(1, 2, 7), Radius = 3 });
                break;
            case "Arc":
                doc.Entities.Add(new Arc(new XYZ(1, 2, 7), 4, Math.PI / 6, Math.PI / 3));
                break;
            case "MText":
                doc.Entities.Add(new MText { InsertPoint = new XYZ(1, 2, 7), Value = "标高", Height = 3.5 });
                break;
            case "Insert":
                var block = new BlockRecord("LUM");
                block.Entities.Add(new Line { StartPoint = new XYZ(-1, -1, 0), EndPoint = new XYZ(1, 1, 0) });
                doc.BlockRecords.Add(block);
                doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(1, 2, 7) });
                break;
            case "LwPolyline":
                // LwPolyline 顶点为 XY，高程经 Elevation 承载（往返保持）。
                var lw = new LwPolyline { Elevation = 7 };
                lw.Vertices.Add(new LwPolyline.Vertex(new XY(1, 2)));
                lw.Vertices.Add(new LwPolyline.Vertex(new XY(5, 2)));
                doc.Entities.Add(lw);
                break;
            case "Polyline2D":
                var p2 = new Polyline2D();
                p2.Vertices.Add(new Vertex2D { Location = new XYZ(1, 2, 7) });
                p2.Vertices.Add(new Vertex2D { Location = new XYZ(5, 2, 7) });
                doc.Entities.Add(p2);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind);
        }
    }

    /// <summary>各 kind 首点取法：Circle/Arc→Center；MText/Insert→InsertPoint；
    /// LwPolyline→Vertices[0].Location（XY，Z 经 Elevation 保持）；Polyline2D→Vertices[0].Location。
    /// 探针核定 3.8.35：Arc : Circle——Arc 分支必须先于 Circle（否则不可达 CS8510）。</summary>
    private static (double X, double Y, double Z) FirstPoint(Entity e) => e switch
    {
        Arc a => (a.Center.X, a.Center.Y, a.Center.Z),
        Circle c => (c.Center.X, c.Center.Y, c.Center.Z),
        MText m => (m.InsertPoint.X, m.InsertPoint.Y, m.InsertPoint.Z),
        Insert i => (i.InsertPoint.X, i.InsertPoint.Y, i.InsertPoint.Z),
        LwPolyline lw => (lw.Vertices[0].Location.X, lw.Vertices[0].Location.Y, lw.Elevation),
        Polyline2D p2 => (p2.Vertices[0].Location.X, p2.Vertices[0].Location.Y, p2.Vertices[0].Location.Z),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name)
    };

    [Theory]
    [InlineData("Circle")]
    [InlineData("Arc")]
    [InlineData("MText")]
    [InlineData("Insert")]
    [InlineData("LwPolyline")]
    [InlineData("Polyline2D")]
    public void Move_SupportedEntity_TranslatesPreservingZ(string kind)
    {
        string path = DxfFixture.CreateTempDxf(doc => AddEntity(kind, doc));
        try
        {
            var s = DrawingSession.Load(path);
            var entity = s.Doc.Entities.First();
            var before = FirstPoint(entity);
            Assert.Equal(7, before.Z, 3); // 夹具高程经 DXF 往返保持
            var r = new MoveEntityHandler().Apply(s, P($"{{\"handle\":\"{entity.Handle}\",\"dx\":3,\"dy\":4}}"));
            Assert.True(r.Ok);
            var after = FirstPoint(s.Doc.Entities.First());
            Assert.Equal(before.X + 3, after.X, 3);
            Assert.Equal(before.Y + 4, after.Y, 3);
            Assert.Equal(7, after.Z, 3); // Z 高程保持，不压平
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Move_UnsupportedEntity_StillHonestStepFailed()
    {
        // Hatch 仍不支持 → 诚实失败口径不变（夹具镜像 View.Tests SquarePath 先例）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var h = new Hatch { IsSolid = true };
            h.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
            {
                new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) }),
                new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(10, 0, 0), EndPoint = new XYZ(10, 10, 0) }),
                new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(10, 10, 0), EndPoint = new XYZ(0, 10, 0) }),
                new Hatch.BoundaryPath.Line(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(0, 0, 0) }),
            }));
            doc.Entities.Add(h);
        });
        try
        {
            var s = DrawingSession.Load(path);
            var hatch = s.Doc.Entities.OfType<Hatch>().Single();
            var r = new MoveEntityHandler().Apply(s, P($"{{\"handle\":\"{hatch.Handle}\",\"dx\":3,\"dy\":4}}"));
            Assert.False(r.Ok);
            Assert.Equal("move 暂不支持实体类型 Hatch", r.Error);
        }
        finally { File.Delete(path); }
    }
}
