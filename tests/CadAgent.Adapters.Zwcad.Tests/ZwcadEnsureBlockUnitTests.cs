using System.Runtime.InteropServices;
using CadAgent.Core.Cad;

namespace CadAgent.Adapters.Zwcad.Tests;

/// <summary>
/// Zwcad EnsureBlock 对齐单测（无 COM，无 RequiresZwcad 门控）：镜像 AcadRuntimeTests
/// created-set 守卫 + 重入补全语义（fake seam 注入，与 ZwcadRuntimeUnitTests 同款）。
/// Zwcad 无忙重试基础设施（RetryBusy 为 Acad 独有）——中途失败以 FakeBlock 第二个
/// AddLine 抛 COMException 模拟（单次失败即止，残留部分块形态与 Acad 中途耗尽同构）。
/// 静态 seam 用完即还原（本程序集 runner.json 已禁集合并行）。
/// 假类型必须 public：dynamic 绑定从适配器程序集发起，internal 成员不可达。
/// </summary>
public class ZwcadEnsureBlockUnitTests : IDisposable
{
    private readonly Func<string, Type?> _originalResolver = ZwcadRuntime.ProgIdResolver;
    private readonly Func<Type, object?> _originalCreate = ZwcadRuntime.CreateComInstance;
    private readonly Func<string[]> _originalNames = VendorGuard.ProcessNames;

    public ZwcadEnsureBlockUnitTests()
    {
        VendorGuard.ProcessNames = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        ZwcadRuntime.ProgIdResolver = _originalResolver;
        ZwcadRuntime.CreateComInstance = _originalCreate;
        VendorGuard.ProcessNames = _originalNames;
    }

    /// <summary>假块内实体：Delete 从属块移除（重入补全清除残留通道）。</summary>
    public sealed class FakeBlockEntity
    {
        private readonly FakeBlock _owner;
        public FakeBlockEntity(FakeBlock owner) { _owner = owner; }
        public void Delete() { _owner.DeleteCalls++; _owner.Entities.Remove(this); }
    }

    /// <summary>假块：RejectAfterFirst=true 时首个 AddLine 成功后再写即抛（模拟中途失败残留部分块）。</summary>
    public sealed class FakeBlock
    {
        public List<FakeBlockEntity> Entities { get; } = new();
        public bool RejectAfterFirst;
        public int AddLineAttempts;
        public int DeleteCalls;
        public FakeBlock(bool rejectAfterFirst) { RejectAfterFirst = rejectAfterFirst; }
        public int Count => Entities.Count;
        public FakeBlockEntity Item(int index) => Entities[index];
        public FakeBlockEntity AddLine(double[] a, double[] b)
        {
            AddLineAttempts++;
            if (RejectAfterFirst && Entities.Count >= 1)
                throw new COMException("块基元写入失败 (0x8000FFFF)", unchecked((int)0x8000FFFF));
            var e = new FakeBlockEntity(this);
            Entities.Add(e);
            return e;
        }
    }

    /// <summary>假块集合：Add 签名序=(origin, Name)（VendorProfiles.Zwcad.BlocksAddNameFirst=false 真机核定）。</summary>
    public sealed class FakeBlocks
    {
        private readonly bool _rejectAfterFirst;
        public Dictionary<string, FakeBlock> Map { get; } = new();
        public int AddCalls;
        public FakeBlocks(bool rejectAfterFirst) { _rejectAfterFirst = rejectAfterFirst; }
        public FakeBlock Item(string name) => Map.TryGetValue(name, out var b) ? b : throw new COMException("块不存在");
        public FakeBlock Add(double[] origin, string name) { AddCalls++; var b = new FakeBlock(_rejectAfterFirst); Map[name] = b; return b; }
    }

    public sealed class BlockDoc
    {
        public FakeBlocks Blocks { get; }
        public BlockDoc(FakeBlocks blocks) { Blocks = blocks; }
        public void Close(bool save) { }
    }

    public sealed class BlockDocuments
    {
        private readonly BlockDoc _doc;
        public BlockDocuments(BlockDoc doc) { _doc = doc; }
        public BlockDoc Open(string path) => _doc;
    }

    public sealed class BlockApp
    {
        private readonly BlockDoc _doc;
        public BlockApp(BlockDoc doc) { _doc = doc; }
        public bool Visible { get; set; }
        public BlockDocuments Documents => new(_doc);
        public void Quit() { }
    }

    private static ZwcadRuntime CreateRuntime(FakeBlocks blocks)
    {
        ZwcadRuntime.ProgIdResolver = _ => typeof(object);
        ZwcadRuntime.CreateComInstance = _ => new BlockApp(new BlockDoc(blocks));
        var rt = new ZwcadRuntime();
        rt.Open("fake.dxf");
        return rt;
    }

    [Fact]
    public void EnsureBlock_PreexistingUserBlock_CountMismatch_ReusedNoDelete()
    {
        // 重名碰撞守卫（镜像 Acad 复审微修复波）：预存用户块（本会话未经 EnsureBlock 创建）
        // count 与期望不一致→复用早退（非破坏），不 Delete 不重建——skill 符号名撞用户
        // 自有块（如 "DOOR"）不得毁用户内容。
        var blocks = new FakeBlocks(rejectAfterFirst: false);
        var userBlock = new FakeBlock(false);
        userBlock.AddLine(new[] { 0.0, 0.0, 0.0 }, new[] { 9.0, 9.0, 0.0 }); // 用户自有内容 1 实体
        blocks.Map["DOOR"] = userBlock;
        using var rt = CreateRuntime(blocks);
        var prims = new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new BlockLine(1, 1, 2, 2) };
        rt.EnsureBlock("DOOR", prims);             // count 1≠2，但非本方创建→复用
        Assert.Single(userBlock.Entities);         // 用户内容原样保留
        Assert.Equal(0, userBlock.DeleteCalls);    // 绝不 Delete
        Assert.Equal(1, userBlock.AddLineAttempts); // 未追加基元
        Assert.Equal(0, blocks.AddCalls);           // 未新建块定义
        rt.EnsureBlock("door", prims);             // 块名大小写不敏感（OrdinalIgnoreCase 同口径）
        Assert.Equal(0, userBlock.DeleteCalls);
        Assert.Single(userBlock.Entities);
    }

    [Fact]
    public void EnsureBlock_MidFailure_ReentryCompletesPartialBlock()
    {
        // 中途失败→部分块残留（1/2 基元）；重入不得静默早退——清除残留重建至完整。
        // （Zwcad 无忙重试：单次 COM 失败即上抛，残留形态与 Acad 中途耗尽同构。）
        var blocks = new FakeBlocks(rejectAfterFirst: true);
        using var rt = CreateRuntime(blocks);
        var prims = new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new BlockLine(1, 1, 2, 2) };
        var ex = Assert.Throws<CadRuntimeException>(() => rt.EnsureBlock("SYM", prims));
        Assert.Contains("EnsureBlock 失败", ex.Message);
        Assert.IsType<COMException>(ex.InnerException);
        var partial = blocks.Map["SYM"];
        Assert.Single(partial.Entities);         // 残留部分块
        partial.RejectAfterFirst = false;        // 故障窗口过去
        rt.EnsureBlock("SYM", prims);            // 重入补全
        Assert.Equal(2, partial.Entities.Count); // 基元完整不重复
        Assert.Equal(1, partial.DeleteCalls);    // 残留被清除
        Assert.Equal(1, blocks.AddCalls);        // 块定义复用，不新建
        Assert.Equal(4, partial.AddLineAttempts); // 1 成功 + 1 失败 + 2 重建
    }

    [Fact]
    public void EnsureBlock_ExistingComplete_ReusedIdempotent()
    {
        // 完整块重入→复用（幂等保持，不清除不重建）。
        var blocks = new FakeBlocks(rejectAfterFirst: false);
        using var rt = CreateRuntime(blocks);
        var prims = new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new BlockLine(1, 1, 2, 2) };
        rt.EnsureBlock("SYM", prims);
        rt.EnsureBlock("SYM", prims);
        var block = blocks.Map["SYM"];
        Assert.Equal(2, block.Entities.Count);
        Assert.Equal(2, block.AddLineAttempts); // 第二次未追加
        Assert.Equal(0, block.DeleteCalls);
        Assert.Equal(1, blocks.AddCalls);
    }

    /// <summary>假应用：逐次 Open 返回不同假文档（钉住 Open 清空本方创建集合的文档会话口径）。</summary>
    public sealed class SwitchingApp
    {
        private readonly Queue<BlockDoc> _docs;
        public SwitchingApp(params BlockDoc[] docs) { _docs = new Queue<BlockDoc>(docs); }
        public bool Visible { get; set; }
        public SwitchingApp Documents => this;
        public BlockDoc Open(string path) => _docs.Dequeue();
        public void Quit() { }
    }

    [Fact]
    public void EnsureBlock_DocSwitch_SameNamePreexisting_ReusedNoDelete()
    {
        // Open 换文档→本方创建集合清空：前一文档本方创建的 "SYM" 在新文档是预存块→复用不 Delete。
        var doc1 = new BlockDoc(new FakeBlocks(false));
        var blocks2 = new FakeBlocks(false);
        var preexisting = new FakeBlock(false);
        preexisting.AddLine(new[] { 0.0, 0.0, 0.0 }, new[] { 5.0, 5.0, 0.0 });
        blocks2.Map["SYM"] = preexisting;
        var doc2 = new BlockDoc(blocks2);
        ZwcadRuntime.ProgIdResolver = _ => typeof(object);
        ZwcadRuntime.CreateComInstance = _ => new SwitchingApp(doc1, doc2);
        using var rt = new ZwcadRuntime();
        rt.Open("a.dwg");
        var prims = new BlockPrimitive[] { new BlockLine(0, 0, 1, 1), new BlockLine(1, 1, 2, 2) };
        rt.EnsureBlock("SYM", prims);   // 文档 1 本方创建（2 基元）
        rt.Open("b.dwg");
        rt.EnsureBlock("SYM", prims);   // 文档 2 预存同名（1 实体≠2）→复用非破坏
        Assert.Single(preexisting.Entities);
        Assert.Equal(0, preexisting.DeleteCalls);
        Assert.Equal(0, blocks2.AddCalls);
    }
}
