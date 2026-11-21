namespace CadAgent.Core.Cad;

/// <summary>
/// 块定义基元（3j，厂商无关）：跨文档符号经 COM 重建块定义时的几何描述。
/// 仅 Line/Circle/Text 三类（与 insert-symbol 白名单同源）；局部坐标。
/// </summary>
public abstract record BlockPrimitive;

public sealed record BlockLine(double X1, double Y1, double X2, double Y2) : BlockPrimitive;

public sealed record BlockCircle(double X, double Y, double Radius) : BlockPrimitive;

public sealed record BlockText(double X, double Y, string Value, double Height) : BlockPrimitive;
