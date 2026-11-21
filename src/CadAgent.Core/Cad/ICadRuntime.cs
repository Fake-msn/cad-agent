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
}

/// <summary>厂商运行时调用失败。</summary>
public sealed class CadRuntimeException : Exception
{
    public CadRuntimeException(string message) : base(message) { }
    public CadRuntimeException(string message, Exception inner) : base(message, inner) { }
}