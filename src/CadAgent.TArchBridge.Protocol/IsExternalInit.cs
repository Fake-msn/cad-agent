// netstandard2.0 引用程序集缺少 records 所需的 IsExternalInit（.NET 5+ 才内置），
// 由本文件补齐，保持零外部依赖。
namespace System.Runtime.CompilerServices
{
    /// <summary>记录类型 init 访问器所需的编译器标记类型。</summary>
    internal static class IsExternalInit
    {
    }
}
