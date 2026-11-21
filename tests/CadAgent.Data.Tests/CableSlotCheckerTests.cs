using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-b T4 缆线格式 + 槽位代码白名单检查器测试。简报测试逐字保留；
/// 简报适配：补 using CadAgent.Data.Checks（简报代码块遗漏，检查器所在命名空间）。
/// 候选启发说明：简报候选式 ^[A-Z]{2,4}- 若强制 "-"，缺 "-" 的 BV3×2.5 永不成候选，
/// 与测试1（钉住 BV3×2.5 被旗标）矛盾——实现取型号前缀（2～4 大写字母）+×，"-" 不强制。
/// </summary>
public class CableSlotCheckerTests : IDisposable
{
    private readonly string _path;
    public CableSlotCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-PC20-WC", Height = 3.5 });   // 合规简写
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-XX20-WC", Height = 3.5 });  // 敷设方式 XX 非法
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV-3×2.5-PC20-YY", Height = 3.5 });  // 部位 YY 非法
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "BV3×2.5", Height = 3.5 });            // 格式违规（缺-）
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "普通文字", Height = 3.5 });            // 非候选
    });
    public void Dispose() => File.Delete(_path);

    private static Rule R(string type, Dictionary<string, object>? p = null) =>
        new("R-T", new RuleSource("GB/T 50786-2012", "4.1.5", "摘录", "source-text"),
            "宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec(type, p ?? new()), "建议");

    private static Dictionary<string, object> Whitelists() => new()
    {
        ["layingMethods"] = new List<object> { "SC","MT","CP","PC","FPC","KPC","CT","CL","MR","PR","M","DB","TC","CE" },
        ["layingPositions"] = new List<object> { "AB","AC","CE","SCE","WS","RS","CC","BC","CLC","WC","FC" },
        ["luminaireMounts"] = new List<object> { "SW","CS","DS","W","C","R","CR","WR","S","CL","HM" }
    };

    [Fact]
    public void CableFormat_ValidSimple_Pass_Invalid_Flagged()
    {
        var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Single(issues); // 仅 BV3×2.5（缺-）；XX/YY 是槽位问题归 SlotCode
        Assert.Contains("BV3×2.5", issues[0].Message);
    }

    [Fact]
    public void SlotCode_BadMethodAndPosition_Flagged_ValidPass()
    {
        var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("XX"));
        Assert.Contains(issues, i => i.Message.Contains("YY"));
    }

    [Fact]
    public void SlotCode_CE_CL_Polysemy_BySlot()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-CE20-CE", Height = 3.5 }); // 方式CE=排管 部位CE=吊顶 均合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-CL20-CL", Height = 3.5 }); // 方式CL=梯架 部位CL? CL不在positions→部位违规
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Single(issues); // 仅第二条部位 CL
            Assert.Contains("CL", issues[0].Message);
        }
        finally { File.Delete(path); }
    }

    // ---- 廉价边界（T4 增补，非简报） ----

    [Fact]
    public void CableFormat_FullForm_Deferred_NotJudged()
    {
        // 完整形特征（空格/+/括号）→ 第一 slice 不判（留后续）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "YJV-4×25+1×16-SC50-FC", Height = 3.5 }); // +
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5 -PC20-WC", Height = 3.5 });    // 空格
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV-3×2.5(PC20)", Height = 3.5 });       // 括号
        });
        try
        {
            var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Empty(issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CableFormat_MessageQuotesFormatAndExample_DetectionBasis()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV3×2.5", Height = 3.5 });
        });
        try
        {
            var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Contains("a b-c(d×e+f×g)i-jh", issue.Message);   // 完整格式
            Assert.Contains("BV-3×2.5-PC20-WC", issue.Message);      // 简写实例
            Assert.Contains("完整形", issue.DetectionBasis);          // 完整形校验留后续（如实注明）
            Assert.Contains("漏检", issue.DetectionBasis);            // 候选启发保守性
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CableFormat_MText_Candidate()
    {
        // MText : Entity（非 TextEntity）独立分支，同样成候选。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new MText { InsertPoint = (XYZ)new XY(0, 0), Value = "BV3×2.5", Height = 3.5 });
        });
        try
        {
            var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Single(issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlotCode_SuffixVariants_NoSuffixSkipped_ThreeSegmentSlots()
    {
        // 2 段（无尾码）不判；3 段末段含数字=仅方式槽、纯字母=仅部位槽；消息引表4.2.1-1/-2。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5", Height = 3.5 });        // 无尾码→不判
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-SC20", Height = 3.5 });  // 仅方式 SC 合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV-3×2.5-FC", Height = 3.5 });    // 仅部位 FC 合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "BV-3×2.5-XX20", Height = 3.5 });  // 仅方式 XX 非法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "BV-3×2.5-YY", Height = 3.5 });    // 仅部位 YY 非法
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("XX") && i.Message.Contains("表4.2.1-1"));
            Assert.Contains(issues, i => i.Message.Contains("YY") && i.Message.Contains("表4.2.1-2"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlotCode_MissingWhitelists_SkipConservatively()
    {
        // 白名单缺键→空集合→对应槽位不判（无白名单不可判，保守，不产生幻影问题）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-XX20-YY", Height = 3.5 });
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Empty(issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StringSet_Conversion_NullSkipped_MissingEmpty_WrongTypeThrows()
    {
        var p = new Dictionary<string, object>
        {
            ["codes"] = new List<object> { "SC", null!, "MT" },
            ["nullKey"] = null!
        };
        var set = CheckParams.StringSet(p, "codes");
        Assert.Equal(2, set.Count);
        Assert.Contains("SC", set);
        Assert.Contains("MT", set);
        Assert.Empty(CheckParams.StringSet(p, "missing"));
        Assert.Empty(CheckParams.StringSet(p, "nullKey"));
        Assert.Throws<InvalidOperationException>(() => CheckParams.StringSet(new Dictionary<string, object> { ["k"] = 42.0 }, "k"));
    }
}
