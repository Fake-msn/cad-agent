using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-b T5 灯具标注格式 + 图纸文件命名检查器测试。简报测试逐字保留；
/// 简报适配：补 using CadAgent.Data.Checks（简报代码块遗漏，检查器所在命名空间，同 T4 惯例）。
/// 廉价边界（非简报）钉住：候选保守性（无 "/" 不成候选）、缺 /e f 引格式、
/// 白名单缺键 f 不判（e 仍判）、MText 独立分支、.working.dxf 后缀还原原始文件名（控制器裁定）。
/// </summary>
public class LuminaireFileCheckerTests : IDisposable
{
    private readonly string _path;
    public LuminaireFileCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "2-YG2(2×40×FL)/3.0 C", Height = 3.5 });  // 合规（吸顶C）
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "2-YG2(2×40×FL)/— C", Height = 3.5 }); // 合规（—吸顶）
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "2-YG2(2×40×FL)/abc C", Height = 3.5 }); // e非法
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "2-YG2(2×40×FL)/3.0 ZZ", Height = 3.5 }); // f非法
    });
    public void Dispose() => File.Delete(_path);

    private static Rule R(string type, Dictionary<string, object>? p = null) =>
        new("R-T", new RuleSource("GB/T 50786-2012", "4.1.5", "摘录", "source-text"),
            "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
            new CheckSpec(type, p ?? new()), "建议");

    [Fact]
    public void Luminaire_BadHeightAndMount_Flagged()
    {
        var p = new Dictionary<string, object> { ["luminaireMounts"] = new List<object> { "SW","CS","DS","W","C","R","CR","WR","S","CL","HM" } };
        var issues = new LuminaireFormatChecker().Check(R("luminaire-format", p), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("abc"));
        Assert.Contains(issues, i => i.Message.Contains("ZZ"));
    }

    [Fact]
    public void FileNaming_StructureCheck()
    {
        // 临时文件名不含 _ → 违规
        var issues = new FileNamingChecker().Check(
            new Rule("R-448-5.4.3", new RuleSource("JGJ/T 448-2018", "5.4.3", "摘录", "source-text"),
                "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
                new CheckSpec("file-naming", new()), "建议"),
            DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Single(issues);
        // 合规名
        string good = Path.Combine(Path.GetTempPath(), "A_001_平面图.dxf");
        File.Copy(_path, good);
        try
        {
            var ok = new FileNamingChecker().Check(
                new Rule("R-448-5.4.3", new RuleSource("JGJ/T 448-2018", "5.4.3", "摘录", "source-text"),
                    "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
                    new CheckSpec("file-naming", new()), "建议"),
                DrawingSession.Load(good), new SemanticOverlay());
            Assert.Empty(ok);
        }
        finally { File.Delete(good); }
    }

    // ---- 廉价边界（T5 增补，非简报） ----

    private static Dictionary<string, object> Mounts() => new()
    {
        ["luminaireMounts"] = new List<object> { "SW","CS","DS","W","C","R","CR","WR","S","CL","HM" }
    };

    private static Rule R448() =>
        new("R-448-5.4.3", new RuleSource("JGJ/T 448-2018", "5.4.3", "摘录", "source-text"),
            "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
            new CheckSpec("file-naming", new()), "建议");

    [Fact]
    public void Luminaire_MissingTail_Flagged_FormatQuoted_NonCandidateSkipped()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "2-YG2(2×40×FL)/", Height = 3.5 });   // 候选缺 /e f → 格式 issue
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "2-YG2(2×40×FL)", Height = 3.5 });  // 无 "/" → 非候选（保守）
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "普通文字", Height = 3.5 });         // 非候选
        });
        try
        {
            var issues = new LuminaireFormatChecker().Check(R("luminaire-format", Mounts()), DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Contains("a-b(c×d×L)/e f", issue.Message); // 消息引格式
            Assert.Contains("视觉", issue.DetectionBasis);     // 符号附近关联/分数排版视觉项 deferred 如实注明
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Luminaire_MissingWhitelist_MountNotJudged_HeightStillJudged()
    {
        // 白名单缺键→空集合→f 不判（无白名单不可判，保守，同 SlotCode）；e 判定不依赖 params，仍判。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "2-YG2(2×40×FL)/abc C", Height = 3.5 });
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "2-YG2(2×40×FL)/3.0 ZZ", Height = 3.5 });
        });
        try
        {
            var issues = new LuminaireFormatChecker().Check(R("luminaire-format"), DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Contains("abc", issue.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Luminaire_MText_Candidate()
    {
        // MText : Entity（非 TextEntity）独立分支，同样成候选（TextValues.Enumerate 共享枚举）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new MText { InsertPoint = (XYZ)new XY(0, 0), Value = "2-YG2(2×40×FL)/abc C", Height = 3.5 });
        });
        try
        {
            var issues = new LuminaireFormatChecker().Check(R("luminaire-format", Mounts()), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Single(issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileNaming_WorkingSuffix_Restored_OriginalNameJudged()
    {
        // .dwg 源 working sidecar 名含 .working.dxf → 去后缀还原原始文件名再判（第一 slice 控制器裁定）；
        // 描述字段 _描述 可选；Target=还原后原始文件名；专业代码白名单 deferred 注记。
        string good = Path.Combine(Path.GetTempPath(), "E_002_照明平面图.dwg.working.dxf");
        string desc = Path.Combine(Path.GetTempPath(), "E_003_系统图_描述.dxf");
        string bad = Path.Combine(Path.GetTempPath(), "badname.dwg.working.dxf");
        File.Copy(_path, good);
        File.Copy(_path, desc);
        File.Copy(_path, bad);
        try
        {
            Assert.Empty(new FileNamingChecker().Check(R448(), DrawingSession.Load(good), new SemanticOverlay()));
            Assert.Empty(new FileNamingChecker().Check(R448(), DrawingSession.Load(desc), new SemanticOverlay()));
            var issues = new FileNamingChecker().Check(R448(), DrawingSession.Load(bad), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Equal("badname.dwg", Assert.Single(issue.Targets)); // Target=还原后原始文件名（非 sidecar 名）
            Assert.Contains("专业代码_图纸编号_图纸名称", issue.Message);
            Assert.Contains("51301", issue.DetectionBasis); // 专业代码白名单（GB/T 51301）未提供→结构校验 only
        }
        finally { File.Delete(good); File.Delete(desc); File.Delete(bad); }
    }
}
