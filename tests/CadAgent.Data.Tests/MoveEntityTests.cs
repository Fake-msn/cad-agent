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

    // ---- 最终审查 Imp#1：Move OCS 反向（镜像法向精确逆映射） ----
    // Circle/Arc/MText/Insert/LwPolyline/Polyline2D/TextEntity 坐标为 OCS 原值
    // （ACadSharp 不转换）；WCS dx/dy 须经任意轴算法逆投影到 OCS 再施加。
    // 镜像法向 N=(0,0,-1)：Ax=(-1,0,0), Ay=(0,1,0) → odx=-dx, ody=dy。
    // 算法与 View RenderModel.OcsMapper 同款（注释互指，防双处漂移）。

    [Fact]
    public void Move_MirroredNormalCircle_InverseOcsDelta()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3, Normal = new XYZ(0, 0, -1) }));
        try
        {
            var s = DrawingSession.Load(path);
            var circle = s.Doc.Entities.OfType<Circle>().Single();
            Assert.Equal(-1, circle.Normal.Z, 3); // 镜像法向经 DXF 往返保持
            var r = new MoveEntityHandler().Apply(s, P($"{{\"handle\":\"{circle.Handle}\",\"dx\":3,\"dy\":4}}"));
            Assert.True(r.Ok);
            var after = s.Doc.Entities.OfType<Circle>().Single();
            // OCS 原值施加 (-3,+4)：Center=(2,9,0)；WCS 投影 = Ax*2+Ay*9 = (-2,9)
            // ——若 WCS dx 直施 OCS（旧缺陷）得 (8,9)→WCS(-8,9)，实际位移 -dx。
            Assert.Equal(2, after.Center.X, 3);
            Assert.Equal(9, after.Center.Y, 3);
            Assert.Equal(0, after.Center.Z, 3);
            // WCS 投影核验：Ax=(-1,0,0), Ay=(0,1,0) → WCS=(-2,9)，位移 +3/+4 精确。
            double wcsX = -after.Center.X, wcsY = after.Center.Y;
            Assert.Equal(-2, wcsX, 3);
            Assert.Equal(9, wcsY, 3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Move_MirroredNormalLwPolyline_VerticesInverseOcsDelta()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            var lw = new LwPolyline { Elevation = 7, Normal = new XYZ(0, 0, -1) };
            lw.Vertices.Add(new LwPolyline.Vertex(new XY(1, 2)));
            lw.Vertices.Add(new LwPolyline.Vertex(new XY(5, 2)));
            doc.Entities.Add(lw);
        });
        try
        {
            var s = DrawingSession.Load(path);
            var lw = s.Doc.Entities.OfType<LwPolyline>().Single();
            Assert.Equal(-1, lw.Normal.Z, 3);
            var r = new MoveEntityHandler().Apply(s, P($"{{\"handle\":\"{lw.Handle}\",\"dx\":3,\"dy\":4}}"));
            Assert.True(r.Ok);
            var after = s.Doc.Entities.OfType<LwPolyline>().Single();
            // 顶点 XY 同为 OCS：(-3,+4) → (-2,6)/(2,6)；Elevation 不动。
            Assert.Equal(-2, after.Vertices[0].Location.X, 3);
            Assert.Equal(6, after.Vertices[0].Location.Y, 3);
            Assert.Equal(2, after.Vertices[1].Location.X, 3);
            Assert.Equal(6, after.Vertices[1].Location.Y, 3);
            Assert.Equal(7, after.Elevation, 3);
        }
        finally { File.Delete(path); }
    }

    // ---- 合并前 ISTQB I5：倾斜/退化法向代表钉住（近似口径钉住，非精确数学） ----
    // 既有测试仅覆盖镜像 (0,0,-1)+默认 (0,0,1)；此处补倾斜 (1,0,1)/√2 与退化 (0,0,0)
    // 代表，钉住 OcsDelta 当前披露口径（面内投影近似，丢弃法向分量——spec §12 明示，
    // 与 View RenderModel.OcsMapper 倾斜椭圆投影近似同口径，注释互指防双处漂移）。

    [Fact]
    public void Move_TiltedNormalCircle_InPlaneProjectionApproximation()
    {
        // N=(1,0,1)/√2：Ax=(0,1,0), Ay=(-1/√2,0,1/√2) →
        // odx = Ax·d = dy = 4；ody = Ay·d = -dx/√2 = -3/√2 ≈ -2.121320。
        // WCS delta (3,4) 的法向分量 (1.5,0,-1.5) 被丢弃——实际 WCS 位移 (1.5,4)≠(3,4)，
        // 为面内投影近似口径钉住（非精确数学；真修复=Core 共享 OCS 数学，I1 deferred）。
        string path = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Circle { Center = new XYZ(5, 5, 0), Radius = 3, Normal = new XYZ(1, 0, 1) }));
        try
        {
            var s = DrawingSession.Load(path);
            var circle = s.Doc.Entities.OfType<Circle>().Single();
            // 倾斜法向经 DXF 往返保持原值 (1,0,1)（探针核定：不归一——OcsDelta 内部
            // Normalize 归一为 (1/√2,0,1/√2)）；Center 为 OCS 原值。
            Assert.Equal(1, circle.Normal.X, 3);
            Assert.Equal(0, circle.Normal.Y, 3);
            Assert.Equal(1, circle.Normal.Z, 3);
            var r = new MoveEntityHandler().Apply(s, P($"{{\"handle\":\"{circle.Handle}\",\"dx\":3,\"dy\":4}}"));
            Assert.True(r.Ok);
            var after = s.Doc.Entities.OfType<Circle>().Single();
            // OCS 原值施加 (4, -3/√2)：Center=(9, 5-3/√2, 0)；Z（OCS 高程）不动。
            Assert.Equal(9, after.Center.X, 3);
            Assert.Equal(5 - 3 / Math.Sqrt(2), after.Center.Y, 3);
            Assert.Equal(0, after.Center.Z, 3);
            // WCS XY 投影核验：X=-oy/√2, Y=ox → 位移 (1.5,4)：X 少 1.5（法向分量被丢弃，
            // 披露口径——若误为精确逆映射会得位移 (3,4)，本断言即失败）。
            double wcsX = -after.Center.Y / Math.Sqrt(2), wcsY = after.Center.X;
            Assert.Equal(-5 / Math.Sqrt(2) + 1.5, wcsX, 3);
            Assert.Equal(9, wcsY, 3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Move_ZeroNormalCircle_NormalizeFallsBackToIdentityDelta()
    {
        // 退化法向 (0,0,0)：Normalize len<1e-12 → 回落 (0,0,1) → identity 快路径：
        // dx/dy 直施（与 View OcsMapper 回落双侧同款钉住）。探针核定：ACadSharp
        // DxfWriter 对零法向圆整体不落盘（DXF 无 CIRCLE）——故经内存实体钉住
        // Normalize 防御路径（内存构造/编辑管线仍可达），不走 DXF 往返。
        string path = DxfFixture.CreateTempDxf(_ => { });
        try
        {
            var s = DrawingSession.Load(path);
            var circle = new Circle { Center = new XYZ(5, 5, 0), Radius = 3, Normal = new XYZ(0, 0, 0) };
            s.Doc.Entities.Add(circle);
            var r = new MoveEntityHandler().Apply(s, P($"{{\"handle\":\"{circle.Handle}\",\"dx\":3,\"dy\":4}}"));
            Assert.True(r.Ok);
            Assert.Equal(8, circle.Center.X, 3);
            Assert.Equal(9, circle.Center.Y, 3);
            Assert.Equal(0, circle.Center.Z, 3);
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
