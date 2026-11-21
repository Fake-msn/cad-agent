using CSMath;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Data.Checks;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3e T2：room-name 检查器（JGJ/T 244-2011 5.2.5：平面图中应注写房间的名称或编号）。
/// 房名模式启发式（RoomDetector 同口径）；编号圆圈6mm格式 VISUAL deferred。
/// </summary>
public class RoomNameCheckerTests : IDisposable
{
    private readonly string _withRoom;
    private readonly string _withoutRoom;

    public RoomNameCheckerTests()
    {
        _withRoom = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(100, 100), Value = "客厅", Height = 5 }));
        _withoutRoom = DxfFixture.CreateTempDxf(doc =>
            doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0) }));
    }

    public void Dispose()
    {
        File.Delete(_withRoom);
        File.Delete(_withoutRoom);
    }

    private static Rule RoomRule() => new(
        "R-244-5.2.5",
        new RuleSource("JGJ/T 244-2011", "5.2.5",
            "平面图中应注写房间的名称或编号，编号应注写在直径为 6mm 细实线绘制的圆圈内", "source-text"),
        "应", RuleForce.Recommended, RuleSeverity.Medium,
        new RuleScope("interior"),
        new CheckSpec("room-name", new Dictionary<string, object>()),
        "注写房间名称或编号（编号圆圈格式见 5.2.5）");

    [Fact]
    public void CheckType_IsRoomName() => Assert.Equal("room-name", new RoomNameChecker().CheckType);

    [Fact]
    public void RoomNamePresent_NoIssues()
    {
        var issues = new RoomNameChecker().Check(RoomRule(), DrawingSession.Load(_withRoom), new SemanticOverlay());
        Assert.Empty(issues);
    }

    [Fact]
    public void NoRoomName_OneRecommendedIssue()
    {
        var issues = new RoomNameChecker().Check(RoomRule(), DrawingSession.Load(_withoutRoom), new SemanticOverlay());
        Assert.Single(issues);
        Assert.Contains("5.2.5", issues[0].Clause);
        Assert.Contains("房间的名称或编号", issues[0].Message);
        Assert.Equal(RuleForce.Recommended, issues[0].Force);
    }
}
