using System.Reflection;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Objects.Evaluations;
using ACadSharp.Tables;
using CadAgent.Data.Handlers;
using CSMath;

namespace CadAgent.Data.Tests;

/// <summary>
/// 块定义夹具（Plan-3 Task 1）：Layout（先 AddViewport 再取 AssociatedBlock，纸面夹具口径）
/// + 命名块（内含一条 Line）+ 模型顶层 Insert 引用该块。保护标志（匿名/XRef/未加载）经
/// <see cref="Protected"/> 在落盘后强置——避免写入器按标志改写块内容而干扰句柄。
/// </summary>
internal static class BlockFixtures
{
    public const string BlockName = "TITLE_BLOCK";
    public const string LayoutName = "布局1";

    public static (DrawingSession Session, BlockRecord Block, Line BlockLine, string Path) BlockSession()
    {
        var doc = new CadDocument();
        var layout = new Layout(LayoutName);
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0),
            Width = 1000,
            Height = 700,
            ViewTarget = new XYZ(0, 0, 0),
            ViewHeight = 200
        });
        layout.AssociatedBlock!.Entities.Add(new Line
        {
            StartPoint = new XYZ(0, 0, 0),
            EndPoint = new XYZ(10, 0, 0)
        });

        var block = new BlockRecord(BlockName);
        var blockLine = new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(5, 5, 0) };
        block.Entities.Add(blockLine);
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(0, 0, 0) });

        string path = Path.Combine(Path.GetTempPath(), "block-" + Guid.NewGuid().ToString("N") + ".dxf");
        using (var fs = File.Create(path)) DxfWriter.Write(fs, doc);
        return (new DrawingSession(path, doc), block, blockLine, path);
    }

    /// <summary>匿名/XRef*/未加载标志落盘后强置（写入器对匿名/XRef 块可能另作处理）。</summary>
    public static void Protected(BlockRecord block, BlockTypeFlags flag) => block.Flags |= flag;

    /// <summary>
    /// 动态块标志：ACadSharp 3.8.35 的 <see cref="BlockRecord.IsDynamic"/> 只读
    /// （= XDictionary 中 "ACAD_ENHANCEDBLOCK" 登记 EvaluationGraph），无公开写入路径，
    /// 故经反射注入 XDictionary（同 ForceHandle 先例；仅测试夹具使用）。
    /// </summary>
    public static void ForceDynamic(BlockRecord block)
    {
        var xd = new CadDictionary();
        xd.Add("ACAD_ENHANCEDBLOCK", new EvaluationGraph());
        typeof(CadObject).GetField("_xdictionary", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(block, xd);
    }
}

/// <summary>
/// Plan-3 Task 1 块定义目标解析矩阵：block+handle 仅检索命名块 Entities、块名大小写不敏感、
/// 未知/受保护块拒绝、block 与 space/layout/attribute 互斥、默认（无 block）不检索块定义。
/// 处理器仍在入口预拒 block（Task 5 才接通），本文件直接考解析器。
/// </summary>
public class BlockEditResolverTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string p in _paths)
        {
            try { File.Delete(p); } catch (IOException) { } // 临时夹具清理：失败不掩盖测试结果
        }
    }

    private (DrawingSession S, BlockRecord Block, Line BlockLine) New()
    {
        var (s, block, line, path) = BlockFixtures.BlockSession();
        _paths.Add(path);
        return (s, block, line);
    }

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o);

    // ---- 命中 ----

    [Fact]
    public void Resolve_BlockEntity_ByHandle_ReturnsBlockDefinitionWithName()
    {
        var (s, block, line) = New();
        Assert.NotEqual(0UL, line.Handle); // 反空断言：落盘后块内实体必须获得句柄

        var target = EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString(), block = BlockFixtures.BlockName }));

        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.BlockDefinition, target!.Owner);
        Assert.Equal(BlockFixtures.BlockName, target.BlockName);
        Assert.Same(line, target.Entity);
        Assert.Null(target.LayoutName);
    }

    [Fact]
    public void Resolve_BlockName_IsCaseInsensitive()
    {
        var (s, _, line) = New();
        var target = EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            block = BlockFixtures.BlockName.ToLowerInvariant()
        }));

        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.BlockDefinition, target!.Owner);
        Assert.Equal(BlockFixtures.BlockName, target.BlockName); // 回填块定义真名
        Assert.Same(line, target.Entity);
    }

    [Fact]
    public void FindBlock_NormalBlock_FoundAndEditable()
    {
        var (s, block, _) = New();
        Assert.Same(block, EditTargetResolver.FindBlock(s, BlockFixtures.BlockName));
        Assert.Same(block, EditTargetResolver.FindBlock(s, BlockFixtures.BlockName.ToLowerInvariant()));
        Assert.Null(EditTargetResolver.FindBlock(s, "不存在的块"));
        Assert.Null(EditTargetResolver.FindBlock(s, ""));
        Assert.True(EditTargetResolver.IsBlockEditable(block));
    }

    // ---- 拒绝面：未知块 / 受保护块 ----

    [Fact]
    public void Resolve_UnknownBlock_ReturnsNull()
    {
        var (s, _, line) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            block = "不存在的块"
        })));
    }

    [Fact]
    public void IsBlockEditable_Anonymous_False_AndResolveRejects()
    {
        var (s, block, line) = New();
        BlockFixtures.Protected(block, BlockTypeFlags.Anonymous);
        Assert.True(block.IsAnonymous);
        Assert.False(EditTargetResolver.IsBlockEditable(block));
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString(), block = block.Name })));
    }

    [Fact]
    public void IsBlockEditable_Dynamic_False_AndResolveRejects()
    {
        var (s, block, line) = New();
        BlockFixtures.ForceDynamic(block);
        Assert.True(block.IsDynamic); // 反空断言：动态标志确已生效
        Assert.False(EditTargetResolver.IsBlockEditable(block));
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString(), block = block.Name })));
    }

    [Theory]
    [InlineData(BlockTypeFlags.XRef)]
    [InlineData(BlockTypeFlags.XRefOverlay)]
    [InlineData(BlockTypeFlags.XRefDependent)]
    public void IsBlockEditable_XRefVariants_False_AndResolveRejects(BlockTypeFlags flag)
    {
        var (s, block, line) = New();
        BlockFixtures.Protected(block, flag);
        Assert.False(EditTargetResolver.IsBlockEditable(block));
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString(), block = block.Name })));
    }

    [Fact]
    public void IsBlockEditable_Unloaded_False_AndResolveRejects()
    {
        var (s, block, line) = New();
        block.IsUnloaded = true;
        Assert.True(block.IsUnloaded);
        Assert.False(EditTargetResolver.IsBlockEditable(block));
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString(), block = block.Name })));
    }

    // ---- 互斥限定（fail-closed） ----

    [Fact]
    public void Resolve_BlockWithSpace_ReturnsNull()
    {
        var (s, block, line) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            block = block.Name,
            space = "paper",
            layout = "布局1"
        })));
    }

    [Fact]
    public void Resolve_BlockWithLayout_ReturnsNull()
    {
        var (s, block, line) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            block = block.Name,
            layout = "布局1"
        })));
    }

    [Fact]
    public void Resolve_BlockWithAttribute_ReturnsNull()
    {
        var (s, block, line) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new
        {
            handle = line.Handle.ToString(),
            block = block.Name,
            attribute = true
        })));
    }

    [Fact]
    public void Resolve_BlockWithoutHandle_ReturnsNull()
    {
        var (s, block, _) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new { block = block.Name })));
        Assert.Null(EditTargetResolver.Resolve(s, P(new { block = block.Name, handle = "not-a-number" })));
    }

    // ---- 默认检索序（无 block）保持 Plan-2 行为 ----

    [Fact]
    public void Resolve_BlockEntity_HandleOnly_NotSearched()
    {
        // 默认不检索块定义（防误伤全实例）：块内句柄无 block 限定 → 未命中，不回退块。
        var (s, _, line) = New();
        Assert.Null(EditTargetResolver.Resolve(s, P(new { handle = line.Handle.ToString() })));
    }

    [Fact]
    public void Resolve_SameHandleInModelAndBlock_HandleOnly_ModelWins()
    {
        var (s, block, line) = New();
        var modelLine = new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(2, 20, 0) };
        s.Doc.Entities.Add(modelLine);
        PaperFixtures.ForceHandle(modelLine, 5151);
        PaperFixtures.ForceHandle(line, 5151);
        Assert.Same(block, EditTargetResolver.FindBlock(s, block.Name));

        var target = EditTargetResolver.Resolve(s, P(new { handle = "5151" }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Model, target!.Owner);
        Assert.Same(modelLine, target.Entity);
        Assert.Null(target.BlockName);
    }

    [Fact]
    public void Resolve_SameHandleInModelAndBlock_WithBlock_BlockWins()
    {
        // 显式 block 即显式指定空间：与模型同句柄不构成歧义。
        var (s, block, line) = New();
        var modelLine = new Line { StartPoint = new XYZ(0, 20, 0), EndPoint = new XYZ(2, 20, 0) };
        s.Doc.Entities.Add(modelLine);
        PaperFixtures.ForceHandle(modelLine, 5151);
        PaperFixtures.ForceHandle(line, 5151);

        var target = EditTargetResolver.Resolve(s, P(new { handle = "5151", block = block.Name }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.BlockDefinition, target!.Owner);
        Assert.Same(line, target.Entity);
        Assert.Equal(block.Name, target.BlockName);
    }

    [Fact]
    public void Resolve_PaperEntityWithoutBlock_StillPaper()
    {
        // 回归：块定义存在时默认路径仍按模型→纸面检索（块定义不参与）。
        var (s, block, _) = New();
        var paperLine = s.Doc.Layouts.First(l => l.Name == BlockFixtures.LayoutName)
            .AssociatedBlock!.Entities.OfType<Line>().Single();
        var target = EditTargetResolver.Resolve(s, P(new { handle = paperLine.Handle.ToString() }));
        Assert.NotNull(target);
        Assert.Equal(EditOwnerKind.Paper, target!.Owner);
        Assert.Equal(BlockFixtures.LayoutName, target.LayoutName);
        Assert.Null(target.BlockName);
        Assert.Same(block, EditTargetResolver.FindBlock(s, BlockFixtures.BlockName));
    }
}
