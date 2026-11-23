using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Semantic;
using CadAgent.Data.Sheets;
using CadAgent.Data.Vision;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// T6：视觉→overlay 管道（fake claims 不触网）。实验实证规律：可读分辨率→准确；
/// 不可读→先验幻觉带置信标签（日期 2023.06 vs 实 2018.10、YJV22 vs 实碳素波纹管）。
/// 诚实原则：视觉声称必须过矢量交叉验证才种子（LlmInference 0.9）；
/// 矛盾/不可读/无法归类→VisionAmbiguous 歧义清单人工纠偏，不静默。
/// </summary>
public class VisionCrossCheckerTests
{
    // 合成夹具（镜像 BlockAttributeSeederTests 模式）：图框块 院标准图框_A0（1000×700）
    // 属性 图号=Z-E01/日期=2018.10；块 E 含圆 r=50；Insert E 属性 A=深照型灯 @ (100,100)
    // 层 EQUIP-照明（约定库 Unknown）；图框层文字 一层电力平面图。
    private static void PopulateVisionDoc(CadDocument doc)
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(1000, 700, 0), EndPoint = new XYZ(0, 700, 0) });
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 700, 0), EndPoint = new XYZ(0, 0, 0) });
        doc.BlockRecords.Add(frame);
        var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        frameIns.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01" });
        frameIns.Attributes.Add(new AttributeEntity { Tag = "日期", Value = "2018.10" });
        doc.Entities.Add(frameIns);
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(500, 650, 0), Value = "一层电力平面图", Height = 30, Layer = new Layer("图框") });

        var e = new BlockRecord("E");
        e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
        doc.BlockRecords.Add(e);
        var layer = new Layer("EQUIP-照明");
        doc.Layers.Add(layer);
        var ins = new Insert(e) { InsertPoint = new XYZ(100, 100, 0), Layer = layer };
        ins.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
        doc.Entities.Add(ins);
    }

    private static CadDocument VisionDoc()
    {
        var doc = new CadDocument();
        PopulateVisionDoc(doc);
        return doc;
    }

    private static (IReadOnlyList<SheetRecord> Sheets, IReadOnlyList<WalkedEntity> Walked) Walk(CadDocument doc)
    {
        var sheets = SheetRegistryBuilder.Build(doc);
        return (sheets, BlockWalker.Walk(doc, sheets).ToList());
    }

    private static string EquipHandle(CadDocument doc) =>
        doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E").Handle.ToString();

    [Fact]
    public void CrossCheck_ClaimMatchesVectorText_SeedsHighConfidence()
    {
        // GT 含 "深照型灯"（块属性 A）；claim conf 0.8 → seeded LlmInference 0.9，
        // handle=GT 命中实体句柄，Evidence 含"视觉声称经矢量交叉验证吻合"，SheetId=幅面。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("深照型灯", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(EquipHandle(doc), a.TargetHandle);
        Assert.Equal(SemanticClass.Lighting, a.Class);
        Assert.Equal(Provenance.LlmInference, a.Provenance);
        Assert.Equal(0.9, a.Confidence);
        Assert.Contains("视觉声称经矢量交叉验证吻合", a.Evidence);
        Assert.Contains("块属性 A=深照型灯", a.Evidence);
        Assert.Equal(sheets[0].SheetId, a.SheetId);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void CrossCheck_ClaimContradictsVector_LowConfidenceAmbiguous()
    {
        // 幻觉拦截（实验实证场景）：GT 日期 2018.10；claim "2023.06" conf 0.9 →
        // 不种子 + VisionAmbiguous（与矢量事实矛盾: gt=2018.10）。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("2023.06", "图签", 0.9) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Equal("2023.06", amb.Claim);
        Assert.Contains("与矢量事实矛盾", amb.Reason);
        Assert.Contains("2018.10", amb.Reason);
    }

    [Fact]
    public void CrossCheck_UnreadableRegion_AmbiguousNotSeeded()
    {
        // claim confidence 0.2（不可读区域先验幻觉）→ 歧义清单，不种子——即使文本恰与 GT 同值。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("深照型灯", "tile-9", 0.2) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Contains("不可读/无矢量佐证", amb.Reason);
    }

    [Fact]
    public void CrossCheck_MatchedButUnclassifiable_AmbiguousNotSeeded()
    {
        // GT 吻合但类推断都不中（日期值非构件语义、层无约定）→ Ambiguous("无法归类")，不种子。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("2018.10", "图签", 0.9) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Contains("无法归类", amb.Reason);
    }

    [Fact]
    public void CrossCheck_NoVectorSupport_AmbiguousNotSeeded()
    {
        // 实验实证场景：claim "碳素波纹管"（或 YJV22 幻觉）conf 0.9 但全幅面无 GT 佐证 →
        // 不可读/无矢量佐证，不种子（诚实：不猜）。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("碳素波纹管", "说明区", 0.9) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Contains("不可读/无矢量佐证", amb.Reason);
    }

    [Fact]
    public void CrossCheck_DrawingNumberContradiction_Intercepted()
    {
        // 同标签类（图号）GT 存在但值不同 → 矛盾拦截（region 提示图号栏）。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("D-05", "图号栏", 0.9) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Contains("与矢量事实矛盾", amb.Reason);
        Assert.Contains("Z-E01", amb.Reason);
    }

    [Fact]
    public void CrossCheck_LayerConventionClass_Seeds()
    {
        // 类推断第二路：claim 命中 GT 文字，命中实体层约定成类（WALL→Wall）。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        var wall = new Layer("WALL");
        doc.Layers.Add(wall);
        var text = new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "墙", Height = 3.5, Layer = wall };
        doc.Entities.Add(text);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("墙", "tile-1", 0.7) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(text.Handle.ToString(), a.TargetHandle);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Equal(Provenance.LlmInference, a.Provenance);
        Assert.Equal(0.9, a.Confidence);
    }

    [Fact]
    public void CrossCheck_ClaimTextAsConventionLayerName_Seeds()
    {
        // 类推断路径兜底：GT 文字在约定库外图层（0），但 claim 文本本身是约定层名（门→Door）。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        var text = new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "门", Height = 3.5 };
        doc.Entities.Add(text);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("门", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(SemanticClass.Door, a.Class);
        Assert.Equal(text.Handle.ToString(), a.TargetHandle);
    }

    [Fact]
    public void CrossCheck_NormalizedWhitespaceAndFullWidth_Matches()
    {
        // 归一（whitespace 折叠/全半角）后匹配：claim 带全角空格尾缀仍命中 GT。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("深照型灯\u3000", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(SemanticClass.Lighting, a.Class);
    }

    [Theory]
    [InlineData("１：１００", "1:100")]
    [InlineData("ＡＢＣ１２３", "ABC123")]
    [InlineData("A\u3000 B  C", "A B C")]
    [InlineData("  x  ", "x")]
    public void Normalize_FullWidthAndWhitespace(string input, string expected) =>
        Assert.Equal(expected, VisionCrossChecker.Normalize(input));

    [Fact]
    public void CrossCheck_ContainmentMatch_BothDirections()
    {
        // 包含匹配：claim⊂GT（深照型灯 ⊂ 深照型灯(吸顶安装)）与 GT⊂claim（配电箱 ⊂ 配电箱 AL1）。
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        var t1 = new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "深照型灯(吸顶安装)", Height = 3.5 };
        var t2 = new TextEntity { InsertPoint = new XYZ(200, 100, 0), Value = "配电箱", Height = 3.5 };
        doc.Entities.Add(t1);
        doc.Entities.Add(t2);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[]
        {
            new VisionClaim("深照型灯", "tile-1", 0.8),
            new VisionClaim("配电箱 AL1", "tile-2", 0.8),
        });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Equal(2, result.Seeded.Count);
        Assert.Contains(result.Seeded, a => a.TargetHandle == t1.Handle.ToString() && a.Class == SemanticClass.Lighting);
        Assert.Contains(result.Seeded, a => a.TargetHandle == t2.Handle.ToString() && a.Class == SemanticClass.Equipment);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void CrossCheck_MTextValue_IsGroundTruth()
    {
        // GT 通道含 MText（MText : Entity，非 TextEntity，独立分支）。
        var doc = VisionDoc();
        var mt = new MText { InsertPoint = new XYZ(300, 300, 0), Value = "荧光灯", Height = 3.5 };
        doc.Entities.Add(mt);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("荧光灯", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(mt.Handle.ToString(), a.TargetHandle);
        Assert.Equal(SemanticClass.Lighting, a.Class);
    }

    [Fact]
    public void CrossCheck_HumanConfirmedHandle_NotSeeded()
    {
        // 人工确认优先（与 seeder/LayerInference 同口径）：已 HumanConfirmed 句柄不再视觉种子。
        var doc = VisionDoc();
        var (sheets, walked) = Walk(doc);
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation(
            EquipHandle(doc), SemanticClass.Column, Provenance.HumanConfirmed, 1.0, "人工改判柱"));
        var vision = new VisionReadResult(new[] { new VisionClaim("深照型灯", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, overlay, walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void CrossCheck_SheetScoped_NoCrossSheetSupport()
    {
        // 幅面作用域（T4 主题）：sheet2 的 GT 不得佐证 sheet1 的声称（跨幅跑偏拒绝）。
        var doc = VisionDoc();
        var frame = doc.BlockRecords.First(b => b.Name == "院标准图框_A0");
        var frame2 = new Insert(frame) { InsertPoint = new XYZ(2000, 0, 0) };
        frame2.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E02" });
        doc.Entities.Add(frame2);
        var e = doc.BlockRecords.First(b => b.Name == "E");
        var ins2 = new Insert(e) { InsertPoint = new XYZ(2100, 100, 0) };
        ins2.Attributes.Add(new AttributeEntity { Tag = "A", Value = "荧光灯" });
        doc.Entities.Add(ins2);
        var (sheets, walked) = Walk(doc);
        Assert.Equal(2, sheets.Count);
        var vision = new VisionReadResult(new[] { new VisionClaim("荧光灯", "tile-1", 0.8) });

        var r1 = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);
        Assert.Empty(r1.Seeded);
        Assert.Contains(r1.Ambiguous, v => v.Reason.Contains("不可读/无矢量佐证"));

        var r2 = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[1].SheetId);
        var a = Assert.Single(r2.Seeded);
        Assert.Equal(ins2.Handle.ToString(), a.TargetHandle);
        Assert.Equal(sheets[1].SheetId, a.SheetId);
    }

    [Fact]
    public void Scan_WithVision_SeedsCrossChecked_AndReportsAmbiguous()
    {
        // Scanner 接入：Seeded Upsert 入 overlay（与 NativeObject 0.95 共存），
        // Ambiguous 并入 ScanResult.VisionAmbiguous；legacy 图层歧义清单不受影响。
        string path = DxfFixture.CreateTempDxf(PopulateVisionDoc);
        try
        {
            var session = DrawingSession.Load(path);
            var vision = new VisionReadResult(new[]
            {
                new VisionClaim("深照型灯", "tile-1", 0.8),
                new VisionClaim("2023.06", "图签", 0.9),
                new VisionClaim("碳素波纹管", "说明区", 0.9),
            });

            var result = SemanticScanner.Scan(session, vision: vision);

            var ins = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
            string h = ins.Handle.ToString();
            var llm = Assert.Single(result.Overlay.Annotations.Where(a => a.TargetHandle == h && a.Provenance == Provenance.LlmInference));
            Assert.Equal(SemanticClass.Lighting, llm.Class);
            Assert.Equal(0.9, llm.Confidence);
            Assert.Contains("视觉声称经矢量交叉验证吻合", llm.Evidence);
            Assert.Equal(session.Sheets[0].SheetId, llm.SheetId);
            // 确定性原生数据共存（五级来源：NativeObject 0.95 优先，闸口取高置信）
            Assert.Contains(result.Overlay.Annotations, a => a.TargetHandle == h && a.Provenance == Provenance.NativeObject && a.Class == SemanticClass.Lighting);
            Assert.Contains(result.VisionAmbiguous, v => v.Claim == "2023.06" && v.Reason.Contains("与矢量事实矛盾") && v.Reason.Contains("2018.10"));
            Assert.Contains(result.VisionAmbiguous, v => v.Claim == "碳素波纹管" && v.Reason.Contains("不可读/无矢量佐证"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scan_LegacyNullVision_EmptyVisionAmbiguous_BehaviorUnchanged()
    {
        // 行为保持：vision=null legacy scan 无 LlmInference 标注，VisionAmbiguous 默认空（非 null）。
        string path = DxfFixture.CreateTempDxf(PopulateVisionDoc);
        try
        {
            var session = DrawingSession.Load(path);
            var result = SemanticScanner.Scan(session);
            Assert.Empty(result.VisionAmbiguous);
            Assert.DoesNotContain(result.Overlay.Annotations, a => a.Provenance == Provenance.LlmInference);
            Assert.Contains(result.Overlay.Annotations, a => a.Provenance == Provenance.NativeObject);

            var two = SemanticScanner.Scan(session, new SemanticOverlay());
            Assert.Empty(two.VisionAmbiguous);
        }
        finally { File.Delete(path); }
    }

    // ---- Fix Round 1：包含短值守卫 + 命中优先级 + 多幅面无佐证抑制 ----

    /// <summary>最小图框夹具（单线幅面 1000×700，镜像既有测试口径）。</summary>
    private static CadDocument FrameOnlyDoc()
    {
        var doc = new CadDocument();
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        doc.Entities.Add(new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) });
        return doc;
    }

    [Fact]
    public void CrossCheck_SingleCharAxisGt_ContainmentGuard_NotSeeded()
    {
        // 短值守卫（GT 侧）：实图轴号 GT "1" 单字符 ubiquitous，claim "配电箱AL1" 包含 "1"
        // 不得佐证——否则 Equipment 0.9 种子到轴号句柄披"已验证"外衣（诚实约束击穿）。
        var doc = FrameOnlyDoc();
        var axisLayer = new Layer("AXIS_TEXT");
        doc.Layers.Add(axisLayer);
        var axis = new TextEntity { InsertPoint = new XYZ(50, 350, 0), Value = "1", Height = 3.5, Layer = axisLayer };
        doc.Entities.Add(axis);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("配电箱AL1", "tile-1", 0.9) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Equal("配电箱AL1", amb.Claim);
        Assert.Contains("不可读/无矢量佐证", amb.Reason);
    }

    [Fact]
    public void CrossCheck_SingleCharClaim_ContainmentGuard_NotSeeded()
    {
        // 短值守卫（claim 侧）：单字符 claim "灯" 不得以包含匹配佐证图例长句"深照型灯(吸顶安装)"。
        var doc = FrameOnlyDoc();
        var legend = new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "深照型灯(吸顶安装)", Height = 3.5 };
        doc.Entities.Add(legend);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("灯", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        Assert.Empty(result.Seeded);
        var amb = Assert.Single(result.Ambiguous);
        Assert.Contains("不可读/无矢量佐证", amb.Reason);
    }

    [Fact]
    public void CrossCheck_SingleCharExactMatch_StillSeeds()
    {
        // 精确通道不受短值守卫影响：claim "1" == GT "1"（轴号层约定成类 Axis）→ 仍可种子。
        var doc = FrameOnlyDoc();
        var axisLayer = new Layer("AXIS_TEXT");
        doc.Layers.Add(axisLayer);
        var axis = new TextEntity { InsertPoint = new XYZ(50, 350, 0), Value = "1", Height = 3.5, Layer = axisLayer };
        doc.Entities.Add(axis);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("1", "tile-1", 0.9) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(axis.Handle.ToString(), a.TargetHandle);
        Assert.Equal(SemanticClass.Axis, a.Class);
        Assert.Equal(Provenance.LlmInference, a.Provenance);
    }

    [Fact]
    public void CrossCheck_HitPriority_CoreAttributeBeatsFrameAndText()
    {
        // 命中优先级：核心 Tag 属性(A/N/$TEXT$) > 图框属性 > 普通文字——
        // 同 claim 多命中取优先级高者，非文档序首个（普通文字故意排最前）。
        var doc = new CadDocument();
        var legend = new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "深照型灯", Height = 3.5 };
        doc.Entities.Add(legend);
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        frameIns.Attributes.Add(new AttributeEntity { Tag = "备注", Value = "深照型灯" });
        doc.Entities.Add(frameIns);
        var e = new BlockRecord("E");
        e.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 50 });
        doc.BlockRecords.Add(e);
        var ins = new Insert(e) { InsertPoint = new XYZ(200, 100, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "A", Value = "深照型灯" });
        doc.Entities.Add(ins);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("深照型灯", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(ins.Handle.ToString(), a.TargetHandle);
        Assert.Contains("块属性 A=深照型灯", a.Evidence);
    }

    [Fact]
    public void CrossCheck_HitPriority_FrameAttributeBeatsPlainText()
    {
        // 命中优先级第二档：无核心属性命中时，图框属性 > 普通文字（文字文档序在前也不取）。
        var doc = new CadDocument();
        var legend = new TextEntity { InsertPoint = new XYZ(100, 100, 0), Value = "深照型灯", Height = 3.5 };
        doc.Entities.Add(legend);
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 700, 0) });
        doc.BlockRecords.Add(frame);
        var frameIns = new Insert(frame) { InsertPoint = new XYZ(0, 0, 0) };
        frameIns.Attributes.Add(new AttributeEntity { Tag = "备注", Value = "深照型灯" });
        doc.Entities.Add(frameIns);
        var (sheets, walked) = Walk(doc);
        var vision = new VisionReadResult(new[] { new VisionClaim("深照型灯", "tile-1", 0.8) });

        var result = VisionCrossChecker.Check(vision, new SemanticOverlay(), walked, sheets[0].SheetId);

        var a = Assert.Single(result.Seeded);
        Assert.Equal(frameIns.Handle.ToString(), a.TargetHandle);
        Assert.Contains("图框属性 备注=深照型灯", a.Evidence);
    }

    [Fact]
    public void Scan_MultiSheet_SeededClaim_NoSupportAmbiguitySuppressed()
    {
        // 多幅面抑制：claim 在 A 幅吻合种子后，B 幅不得再产"无矢量佐证"假歧义污染人工纠偏清单；
        // 矛盾 claim（两幅均在）与全局无佐证 claim（未种子）照常上报。
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            PopulateVisionDoc(doc);
            var frame = doc.BlockRecords.First(b => b.Name == "院标准图框_A0");
            var frame2 = new Insert(frame) { InsertPoint = new XYZ(2000, 0, 0) };
            frame2.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E02" });
            frame2.Attributes.Add(new AttributeEntity { Tag = "日期", Value = "2018.10" });
            doc.Entities.Add(frame2);
        });
        try
        {
            var session = DrawingSession.Load(path);
            Assert.Equal(2, session.Sheets.Count);
            var vision = new VisionReadResult(new[]
            {
                new VisionClaim("深照型灯", "tile-1", 0.8),
                new VisionClaim("2023.06", "图签", 0.9),
                new VisionClaim("碳素波纹管", "说明区", 0.9),
            });

            var result = SemanticScanner.Scan(session, vision: vision);

            // A 幅佐证种子仍在；B 幅对同一 claim 的"无矢量佐证"被抑制（任何 reason 都不剩）
            var ins = session.Doc.Entities.OfType<Insert>().First(i => i.Block?.Name == "E");
            Assert.Contains(result.Overlay.Annotations,
                a => a.TargetHandle == ins.Handle.ToString() && a.Provenance == Provenance.LlmInference);
            Assert.DoesNotContain(result.VisionAmbiguous, v => v.Claim == "深照型灯");
            // 矛盾（真问题）与全局无佐证（未种子）保留
            Assert.Contains(result.VisionAmbiguous, v => v.Claim == "2023.06" && v.Reason.Contains("与矢量事实矛盾"));
            Assert.Contains(result.VisionAmbiguous, v => v.Claim == "碳素波纹管" && v.Reason.Contains("不可读/无矢量佐证"));
        }
        finally { File.Delete(path); }
    }
}
