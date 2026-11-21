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
}

/// <summary>厂商运行时调用失败。</summary>
public sealed class CadRuntimeException : Exception
{
    public CadRuntimeException(string message) : base(message) { }
    public CadRuntimeException(string message, Exception inner) : base(message, inner) { }
}