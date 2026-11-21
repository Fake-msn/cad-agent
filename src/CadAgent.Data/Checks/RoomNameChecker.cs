using CadAgent.Core;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Checks;

/// <summary>
/// JGJ/T 244-2011 5.2.5 房间名称/编号：平面图中应注写房间的名称或编号。
/// 检测＝RoomDetector 房名模式启发式（同口径，非自造信号）；未检出→单条 recommended issue。
/// 编号圆圈 6mm/字高比较/房间名称表＝几何+版面检查（VISUAL deferred，DetectionBasis 注记）。
/// 包选择即场景选择：interior-244 包随室内项目加载，非室内项目不加载本包（无噪声）。
/// </summary>
public sealed class RoomNameChecker : IRuleChecker
{
    public string CheckType => "room-name";

    public IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay)
    {
        var s = (DrawingSession)session;
        if (RoomDetector.Detect(s, overlay).Count > 0) return Array.Empty<Issue>();
        return new[]
        {
            new Issue(
                rule.Id, rule.Source.Clause, rule.Source.Standard, rule.Source.Excerpt,
                rule.Force, rule.Severity,
                Array.Empty<string>(),
                "未检出房间的名称或编号标注（5.2.5：平面图中应注写房间的名称或编号）。",
                rule.Suggestion,
                "房名模式列表启发式（RoomDetector 同口径）；编号圆圈6mm格式/字高比较/房间名称表 VISUAL deferred")
        };
    }
}
