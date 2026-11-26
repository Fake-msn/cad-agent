using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// Plan-2 Task 2：set-text / set-attribute（实例属性与文字改值）。夹具复用
/// <see cref="PaperFixtures"/>；断言口径：值被改 + 失败时原值逐字不变（校验先于变更）。
/// </summary>
public class SetTextAttributeTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private (DrawingSession S, Layout Layout) New(Action<CadDocument>? extra = null)
    {
        var (s, layout, path) = PaperFixtures.PaperSession(extra);
        _paths.Add(path);
        return (s, layout);
    }

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o);

    private static TextEntity AddModelText(CadDocument doc, string value)
    {
        var text = new TextEntity { Value = value, InsertPoint = new XYZ(1, 2, 0), Height = 3.5 };
        doc.Entities.Add(text);
        return text;
    }

    private static TextEntity AddPaperText(CadDocument doc, string value)
    {
        var text = new TextEntity { Value = value, InsertPoint = new XYZ(100, 50, 0), Height = 3.5 };
        doc.Layouts.First(l => l.Name == PaperFixtures.LayoutName).AssociatedBlock!.Entities.Add(text);
        return text;
    }

    // ---- set-text 基本改值 ----

    [Fact]
    public void SetText_TextEntity_ChangesValue_ReturnsHandle()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "旧标题"));
        var r = new SetTextHandler().Apply(s, P(new { handle = text!.Handle.ToString(), text = "新标题" }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("新标题", text.Value);
        Assert.Equal(text.Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void SetText_MText_ChangesValue_AllowsNewline()
    {
        MText? mtext = null;
        var (s, _) = New(doc =>
        {
            mtext = new MText { Value = "旧", InsertPoint = new XYZ(0, 0, 0), Height = 3 };
            doc.Entities.Add(mtext);
        });
        var r = new SetTextHandler().Apply(s, P(new { handle = mtext!.Handle.ToString(), text = "第一行\n第二行" }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("第一行\n第二行", mtext.Value);
    }

    [Fact]
    public void SetText_Attribute_ChangesValue()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetTextHandler().Apply(s, P(new { handle = attr!.Handle.ToString(), text = "新图名" }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("新图名", attr.Value);
    }

    [Fact]
    public void SetText_PaperText_ChangesOnlyPaper_AndHashDiffers()
    {
        TextEntity? paperText = null;
        var (s, _) = New(doc => paperText = AddPaperText(doc, "纸面旧字"));
        string before = s.ComputeHash();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = paperText!.Handle.ToString(), text = "纸面新字",
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("纸面新字", paperText.Value);
        Assert.Empty(s.Doc.Entities); // 模型空间无写入
        Assert.NotEqual(before, s.ComputeHash()); // P| 行含文字值
    }

    [Fact]
    public void SetText_PaperAttribute_ChangesValue_AndHashDiffers()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc, inPaper: true));
        string before = s.ComputeHash();
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = attr!.Handle.ToString(), text = "纸面属性",
            space = "paper", layout = PaperFixtures.LayoutName, attribute = true
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("纸面属性", attr.Value);
        Assert.NotEqual(before, s.ComputeHash()); // P| + A| 行含属性值
    }

    [Fact]
    public void SetText_EmptyString_ClearsValue()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "待清空"));
        var r = new SetTextHandler().Apply(s, P(new { handle = text!.Handle.ToString(), text = "" }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("", text.Value);
    }

    // ---- set-text 校验（失败时值不变） ----

    [Fact]
    public void SetText_NewlineInTextEntity_Rejected()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "原文"));
        var r = new SetTextHandler().Apply(s, P(new { handle = text!.Handle.ToString(), text = "甲\n乙" }));
        Assert.False(r.Ok);
        Assert.Equal("单行文字不支持换行（多行请用 MText）", r.Error);
        Assert.Equal("原文", text.Value);
    }

    [Fact]
    public void SetText_NewlineInAttribute_Rejected()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetTextHandler().Apply(s, P(new { handle = attr!.Handle.ToString(), text = "甲\n乙" }));
        Assert.False(r.Ok);
        Assert.Equal("单行文字不支持换行（多行请用 MText）", r.Error);
        Assert.Equal("栖贤雅庭", attr.Value); // 夹具原值
    }

    [Fact]
    public void SetText_LengthBoundary_4096Ok_4097Rejected()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "原文"));
        var ok = new SetTextHandler().Apply(s, P(new
        {
            handle = text!.Handle.ToString(), text = new string('长', 4096)
        }));
        Assert.True(ok.Ok, ok.Error);
        var tooLong = new SetTextHandler().Apply(s, P(new
        {
            handle = text.Handle.ToString(), text = new string('长', 4097)
        }));
        Assert.False(tooLong.Ok);
        Assert.Equal("text 超长（上限 4096 字符）", tooLong.Error);
        Assert.Equal(4096, text.Value.Length); // 失败不改原值
    }

    [Fact]
    public void SetText_AttributeDefinition_Rejected()
    {
        var (s, _) = New();
        var adef = new AttributeDefinition { Tag = "T", Value = "默认值" };
        PaperFixtures.ForceHandle(adef, 9001); // 落盘后加：夹具不参与 DXF 写出
        s.Doc.Entities.Add(adef);
        var r = new SetTextHandler().Apply(s, P(new { handle = "9001", text = "改默认" }));
        Assert.False(r.Ok);
        Assert.Equal("属性定义默认值编辑未支持", r.Error);
        Assert.Equal("默认值", adef.Value);
    }

    [Fact]
    public void SetText_Viewport_Rejected()
    {
        var (s, layout) = New();
        var vp = layout.AssociatedBlock!.Entities.OfType<Viewport>().Single(v => !v.RepresentsPaper);
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = vp.Handle.ToString(), text = "视口文字",
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Equal("视口对象不支持编辑", r.Error);
    }

    [Fact]
    public void SetText_NonTextEntity_Rejected()
    {
        var (s, layout) = New();
        var line = layout.AssociatedBlock!.Entities.OfType<Line>().Single();
        var r = new SetTextHandler().Apply(s, P(new { handle = line.Handle.ToString(), text = "线" }));
        Assert.False(r.Ok);
        Assert.Contains("不是文字实体", r.Error);
    }

    [Fact]
    public void SetText_MissingOrNonStringText_Rejected()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "原文"));
        var missing = new SetTextHandler().Apply(s, P(new { handle = text!.Handle.ToString() }));
        Assert.False(missing.Ok);
        Assert.Equal("text 必填且须为字符串", missing.Error);
        var numeric = new SetTextHandler().Apply(s, P(new { handle = text.Handle.ToString(), text = 123 }));
        Assert.False(numeric.Ok);
        Assert.Equal("text 必填且须为字符串", numeric.Error);
        Assert.Equal("原文", text.Value);
    }

    [Fact]
    public void SetText_UnknownHandle_Rejected()
    {
        var (s, _) = New();
        var r = new SetTextHandler().Apply(s, P(new { handle = "DEADBEEF", text = "X" }));
        Assert.False(r.Ok);
        Assert.Contains("句柄不存在", r.Error);
    }

    [Fact]
    public void SetText_BlockParam_RejectedAsPlan3()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "原文"));
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = text!.Handle.ToString(), text = "改", block = "图框"
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内编辑将在后续切片提供（Plan-3）", r.Error);
        Assert.Equal("原文", text.Value);
    }

    [Fact]
    public void SetText_SpaceNotPaper_Rejected()
    {
        TextEntity? text = null;
        var (s, _) = New(doc => text = AddModelText(doc, "原文"));
        var r = new SetTextHandler().Apply(s, P(new
        {
            handle = text!.Handle.ToString(), text = "改",
            space = "foo", layout = PaperFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Equal("space 仅支持 paper", r.Error);
        Assert.Equal("原文", text.Value);
    }

    // ---- set-attribute ----

    [Fact]
    public void SetAttribute_HandleForm_ChangesValue()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new { handle = attr!.Handle.ToString(), value = "新值" }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("新值", attr.Value);
        Assert.Equal(attr.Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void SetAttribute_InsertTagForm_ChangesValue()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insert!.Handle.ToString(), tag = "图名", value = "栖贤雅庭二期"
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("栖贤雅庭二期", attr!.Value);
        Assert.Equal(attr.Handle.ToString(), r.TargetHandle);
    }

    [Fact]
    public void SetAttribute_InsertTagForm_PaperRestricted_FindsPaperInsert()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc, inPaper: true));
        string before = s.ComputeHash();
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insert!.Handle.ToString(), tag = "图名", value = "纸面值",
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("纸面值", attr!.Value);
        Assert.NotEqual(before, s.ComputeHash());
    }

    [Fact]
    public void SetAttribute_InsertTagForm_PaperRestrictionExcludesModelInsert()
    {
        Insert? insert = null;
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (insert, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insert!.Handle.ToString(), tag = "图名", value = "不该写入",
            space = "paper", layout = PaperFixtures.LayoutName
        }));
        Assert.False(r.Ok);
        Assert.Contains("insert 句柄不存在", r.Error);
        Assert.Equal("栖贤雅庭", attr!.Value);
    }

    [Fact]
    public void SetAttribute_DuplicateTag_Rejected()
    {
        Insert? insert = null;
        var (s, _) = New(doc =>
        {
            (insert, _) = PaperFixtures.AddAttributeInsert(doc);
            insert!.Attributes.Add(new AttributeEntity { Tag = "图名", Value = "重复项" });
        });
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insert!.Handle.ToString(), tag = "图名", value = "新值"
        }));
        Assert.False(r.Ok);
        Assert.Equal("同标签属性不唯一，请用句柄", r.Error);
        Assert.All(insert.Attributes, a => Assert.NotEqual("新值", a.Value));
    }

    [Fact]
    public void SetAttribute_MissingTag_Rejected()
    {
        Insert? insert = null;
        var (s, _) = New(doc => (insert, _) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insert!.Handle.ToString(), tag = "不存在", value = "新值"
        }));
        Assert.False(r.Ok);
        Assert.Equal("insert 上不存在标签：不存在", r.Error);
    }

    [Fact]
    public void SetAttribute_TagMatchIsCaseSensitive()
    {
        // CAD tag 大小写敏感：大小写不符按未找到处理（不静默改写近名属性）。
        AttributeEntity? attr = null;
        var (s, _) = New(doc =>
        {
            Insert insert = new(new BlockRecord("TBCASE_" + Guid.NewGuid().ToString("N")[..8]));
            attr = new AttributeEntity { Tag = "DWG_NO", Value = "A-101" };
            attr.InsertPoint = new XYZ(0, 0, 0);
            attr.AlignmentPoint = new XYZ(0, 0, 0);
            insert.Attributes.Add(attr);
            doc.Entities.Add(insert);
        });
        var insertHandle = s.Doc.Entities.OfType<Insert>().Single().Handle.ToString();
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insertHandle, tag = "dwg_no", value = "B-202"
        }));
        Assert.False(r.Ok);
        Assert.Contains("不存在标签", r.Error);
        Assert.Equal("A-101", attr!.Value);
    }

    [Fact]
    public void SetAttribute_HandleForm_NonAttributeTarget_Rejected()
    {
        Insert? insert = null;
        var (s, _) = New(doc => (insert, _) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new { handle = insert!.Handle.ToString(), value = "新值" }));
        Assert.False(r.Ok);
        Assert.Equal("目标不是实例属性", r.Error);
    }

    [Fact]
    public void SetAttribute_UnknownInsert_Rejected()
    {
        var (s, _) = New();
        var r = new SetAttributeHandler().Apply(s, P(new { insert = "DEADBEEF", tag = "图名", value = "X" }));
        Assert.False(r.Ok);
        Assert.Contains("insert 句柄不存在", r.Error);
    }

    [Fact]
    public void SetAttribute_ValueValidation_MissingNewlineTooLong()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var missing = new SetAttributeHandler().Apply(s, P(new { handle = attr!.Handle.ToString() }));
        Assert.Equal("value 必填且须为字符串", missing.Error);
        var newline = new SetAttributeHandler().Apply(s, P(new { handle = attr.Handle.ToString(), value = "甲\n乙" }));
        Assert.Equal("单行文字不支持换行（多行请用 MText）", newline.Error);
        var tooLong = new SetAttributeHandler().Apply(s, P(new
        {
            handle = attr.Handle.ToString(), value = new string('长', 4097)
        }));
        Assert.Equal("value 超长（上限 4096 字符）", tooLong.Error);
        Assert.Equal("栖贤雅庭", attr.Value);
    }

    [Fact]
    public void SetAttribute_EmptyString_ClearsValue()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new { handle = attr!.Handle.ToString(), value = "" }));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("", attr.Value);
    }

    [Fact]
    public void SetAttribute_BlockParam_RejectedAsPlan3()
    {
        AttributeEntity? attr = null;
        var (s, _) = New(doc => (_, attr) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            handle = attr!.Handle.ToString(), value = "新值", block = "图框"
        }));
        Assert.False(r.Ok);
        Assert.Equal("块内编辑将在后续切片提供（Plan-3）", r.Error);
        Assert.Equal("栖贤雅庭", attr.Value);
    }

    [Fact]
    public void SetAttribute_SpaceNotPaper_Rejected()
    {
        Insert? insert = null;
        var (s, _) = New(doc => (insert, _) = PaperFixtures.AddAttributeInsert(doc));
        var r = new SetAttributeHandler().Apply(s, P(new
        {
            insert = insert!.Handle.ToString(), tag = "图名", value = "新值", space = "foo"
        }));
        Assert.False(r.Ok);
        Assert.Equal("space 仅支持 paper", r.Error);
    }

    [Fact]
    public void SetAttribute_NeitherHandleNorInsertTag_Rejected()
    {
        var (s, _) = New();
        var r = new SetAttributeHandler().Apply(s, P(new { value = "新值" }));
        Assert.False(r.Ok);
        Assert.Equal("set-attribute 需要 handle 或 insert+tag", r.Error);
    }
}
