using CadAgent.Core;
using CadAgent.Data.Checks;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

/// <summary>
/// 2b-ii-b T4 缆线格式 + 槽位代码白名单检查器测试。简报测试逐字保留；
/// 简报适配：补 using CadAgent.Data.Checks（简报代码块遗漏，检查器所在命名空间）。
/// 候选启发说明：简报候选式 ^[A-Z]{2,4}- 若强制 "-"，缺 "-" 的 BV3×2.5 永不成候选，
/// 与测试1（钉住 BV3×2.5 被旗标）矛盾——实现取型号前缀（2～4 大写字母，后随 "-" 或数字，
/// lookahead ^[A-Z]{2,4}(?=[-\d])）+×；≥5 字母前缀（ABCDE-/WDZAY-）与纯字母+×（MRAB×）不成候选。
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
            // 终审裁定 1b/1c：DetectionBasis 钉住新串——误旗边界披露 + 漏检注记 + 完整形留后续。
            Assert.Equal(
                "文本模式候选（型号前缀+×），简写形正则；非缆线文本可能漏检；" +
                "多段型号前缀（如WDZ-YJY）与尺寸形文本（如MR100×50）可能误旗；完整形（含空格/+/括号）校验留后续",
                issue.DetectionBasis);
            Assert.Contains("误旗", issue.DetectionBasis);            // 误旗边界如实披露
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
        // 2 段（无尾码）不判；3 段末段含数字=仅方式槽（去数字判 methods）；
        // 3 段纯字母尾码按成员资格定槽（终审裁定 2）：FC 仅部位成员→部位槽合法；
        // YY 双不属→不判（宁漏勿滥，原"仅部位 YY 非法"断言按裁定更新）。消息引表4.2.1-1。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5", Height = 3.5 });        // 无尾码→不判
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-SC20", Height = 3.5 });  // 仅方式 SC 合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV-3×2.5-FC", Height = 3.5 });    // 仅部位成员 FC→部位槽合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "BV-3×2.5-XX20", Height = 3.5 });  // 仅方式 XX 非法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "BV-3×2.5-YY", Height = 3.5 });    // 纯字母 YY 双不属→不判（保守）
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues); // 仅 XX20 方式违规；YY 双不属不判
            Assert.Contains("XX", issue.Message);
            Assert.Contains("表4.2.1-1", issue.Message);
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

    // ---- 终审修复波：候选边界收紧 + 误旗披露（裁定 1） ----

    [Fact]
    public void CableFormat_CandidateBoundary_LongPrefixOrPureLetterTimes_NotCandidate()
    {
        // 裁定 1a：CandidatePrefix=^[A-Z]{2,4}(?=[-\d])——≥5 字母前缀（ABCDE-/WDZAY-）
        // 与纯字母+×（MRAB×，前缀后随 × 而非 -/数字）不成候选，不产生 issue。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "ABCDE-3×2.5", Height = 3.5 });  // 5 字母→非候选
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "WDZAY-3×2.5", Height = 3.5 }); // 5 字母→非候选
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "MRAB×50", Height = 3.5 });     // 纯字母+×→非候选
        });
        try
        {
            var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Empty(issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CableFormat_DisclosedFalseFlags_MultiSegmentPrefixAndDimension_FlaggedWithDisclosure()
    {
        // 裁定 1b/1c：WDZ（3 字母随 -）与 MR（2 字母随数字）仍成候选→简写形不匹配→旗标；
        // 此为已披露的误旗类（recommended + DetectionBasis 披露，可接受）；BV3×2.5 仍旗标。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "WDZ-YJY-3×2.5-PC20-WC", Height = 3.5 }); // 多段型号前缀→误旗（披露）
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "MR100×50", Height = 3.5 });              // 尺寸形文本→误旗（披露）
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV3×2.5", Height = 3.5 });               // 缺- 格式违规仍旗标
        });
        try
        {
            var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Equal(3, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("WDZ-YJY-3×2.5-PC20-WC"));
            Assert.Contains(issues, i => i.Message.Contains("MR100×50"));
            Assert.Contains(issues, i => i.Message.Contains("BV3×2.5"));
            Assert.All(issues, i => Assert.Contains("误旗", i.DetectionBasis)); // 误旗边界披露
        }
        finally { File.Delete(path); }
    }

    // ---- 终审修复波：3 段纯字母尾码成员资格定槽（裁定 2） ----

    [Fact]
    public void SlotCode_ThreeSegmentBareLetterTail_MembershipDisambiguation_Conservative()
    {
        // CT ∈methods ∉positions→方式槽（合法，无 issue）；CE 双属→不判；XX 双不属→不判（宁漏勿滥）。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-CT", Height = 3.5 }); // 仅方式成员→方式槽合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-CE", Height = 3.5 }); // 双属→不判
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV-3×2.5-XX", Height = 3.5 }); // 双不属→不判（保守）
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Empty(issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SlotCode_FourSegmentBareLetterPosition_StillJudged_DetectionBasisBoundary()
    {
        // 4 段逻辑不变：末段 YY 仍按部位槽判（违规）；DetectionBasis 披露 3 段纯字母边界。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-PC20-YY", Height = 3.5 });
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(path), new SemanticOverlay());
            var issue = Assert.Single(issues);
            Assert.Contains("YY", issue.Message);
            Assert.Contains("表4.2.1-2", issue.Message);
            Assert.Contains("宁漏勿滥", issue.DetectionBasis); // 3 段纯字母尾码保守边界披露
        }
        finally { File.Delete(path); }
    }
}
