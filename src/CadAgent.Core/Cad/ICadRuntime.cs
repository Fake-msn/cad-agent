namespace CadAgent.Core.Cad;

/// <summary>厂商运行时信息。</summary>
public sealed record RuntimeInfo(string Vendor, string Version, bool Available);

/// <summary>
/// CAD 厂商运行时契约（厂商无关，spec §3 适配器一等公民）。
/// 实现在独立适配器项目（如 CadAgent.Adapters.Zwcad，dynamic COM）；
/// Core 不引用任何厂商库。所有方法失败抛 CadRuntimeException。
/// </summary>
public interface ICadRuntime : IDisposable
{
    RuntimeInfo GetInfo();
    void Open(string drawingPath);
    void SaveAs(string outputPath);
    void Close(bool save);
    /// <summary>崩溃后重启运行时（spec §9）。</summary>
    void Restart();

    // 3j 绘制操作（计划级 COM 重放通道——"agent 操作 CAD 绘制"本义）：
    /// <summary>模型空间加直线（layer null=当前层）。</summary>
    void AddLine(double x1, double y1, double x2, double y2, string? layer = null);
    /// <summary>模型空间加单行文字。</summary>
    void AddText(double x, double y, string text, double height, string? layer = null);
    /// <summary>模型空间加圆。</summary>
    void AddCircle(double x, double y, double radius, string? layer = null);
    /// <summary>模型空间插入块引用（块须已存在，否则 CadRuntimeException）。</summary>
    void AddInsert(string blockName, double x, double y, double scale = 1, double rotation = 0, string? layer = null);
    /// <summary>块定义幂等创建（已存在则跳过）；基元局部坐标。</summary>
    void EnsureBlock(string blockName, IReadOnlyList<BlockPrimitive> primitives);
    /// <summary>模型空间实体计数（读回闸口用）。</summary>
    int EntityCount();

    // 切片A COM 原生读参 + 句柄寻址（默认实现=零厂商改动：读空/响亮拒绝）
    /// <summary>COM 原生读参（spec 切片A）：drawingPath!=null=自启+自开+读完关；null=读当前已开文档。
    /// 无厂商/不支持→空（默认实现）。</summary>
    IReadOnlyList<NativeEntityParam> ReadNativeParams(string? drawingPath = null) =>
        Array.Empty<NativeEntityParam>();

    /// <summary>句柄寻址修改（spec 切片A §4.2 native-live 通道）；不支持→响亮拒绝（默认实现）。</summary>
    void ChangeLayerByHandle(string hexHandle, string layer) =>
        throw new CadRuntimeException("运行时不支持句柄寻址操作（change-layer）");
    void MoveByHandle(string hexHandle, double dx, double dy) =>
        throw new CadRuntimeException("运行时不支持句柄寻址操作（move）");
    void DeleteByHandle(string hexHandle) =>
        throw new CadRuntimeException("运行时不支持句柄寻址操作（delete）");

    // 切片B 深度炸开（explode-native）；默认实现=零厂商改动：取证读空/写通道响亮拒绝
    /// <summary>深度炸开取证（spec 切片B §3）：逐实体递归炸开到叶并记录基元/XData/bbox。
    /// 无厂商/不支持→空（默认实现）。</summary>
    /// <param name="drawingPath">待读图纸路径（实现自开自读自关）。</param>
    /// <param name="includeBlockDefs">A3（技术债批次A）：是否随本次读取索取**块定义段**（BDEF）。
    /// 缺省 false——只有需要定义段事实的调用方（CLI <c>semantic scan --explode</c>）才付块表扫描成本；
    /// 未索取时实现不得缓存块定义表，后续 <see cref="ReadExplodeBlockDefs"/> 必响亮拒绝。</param>
    IReadOnlyList<ExplodeEvidence> ReadExplodeEvidence(string drawingPath, bool includeBlockDefs = false) =>
        Array.Empty<ExplodeEvidence>();

    /// <summary>块定义内 TDb 聚合（边界补齐 B1）：**定义段**（块名+类计数），与
    /// <see cref="ReadExplodeEvidence"/> 的模型空间实例段互不混同。契约：须紧接
    /// <c>ReadExplodeEvidence(path, includeBlockDefs: true)</c> 之后以同一 path 调用（实现在同一次桥会话内缓存，
    /// 缓存命中不二次开图；未请求块定义段或无缓存由实现响亮拒绝）。无厂商/不支持→空（默认实现）。</summary>
    IReadOnlyList<BlockDefTdbEntry> ReadExplodeBlockDefs(string drawingPath) =>
        Array.Empty<BlockDefTdbEntry>();

    /// <summary>句柄级炸开替换（spec 切片B §4）：炸开后原子替换原实体并另存 liveOut。
    /// 不支持→响亮拒绝（默认实现）。</summary>
    ExplodeReplaceResult ExplodeReplaceByHandle(string hexHandle, string liveOutPath) =>
        throw new CadRuntimeException("运行时不支持深度炸开（explode-native）");
}

/// <summary>厂商运行时调用失败。</summary>
public sealed class CadRuntimeException : Exception
{
    public CadRuntimeException(string message) : base(message) { }
    public CadRuntimeException(string message, Exception inner) : base(message, inner) { }
}