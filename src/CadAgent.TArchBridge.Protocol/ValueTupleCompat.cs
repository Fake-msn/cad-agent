#if NET45
// net45 兼容垫片（仅 net45 腿编译）：net45 引用程序集不含 System.ValueTuple /
// System.Runtime.CompilerServices.TupleElementNamesAttribute，元组签名在该腿会 CS8179/CS8137（实证）。
//
// 为什么不用 NuGet System.ValueTuple 包：它对 net45 只解析出 netstandard1.0 运行时资产
//（需 facade 程序集，AutoCAD 2014 的 .NET 4.5 进程不可靠），且给桥多带一个部署 dll——
// 本项目在 PendingRequest（见 BridgeFiles）已因此改用具名 struct。此处语义不同：
// BlockDefAggregator 的**公开签名**要求元组（协议层 API 契约），故以零依赖垫片满足两腿同源。
// 垫片是普通 CLR 结构体，不引入任何外部引用；netstandard2.0 腿用 BCL 自带实现（本文件不编译）。
using System.Collections.Generic;

namespace System
{
    /// <summary>三元组元组垫片（等价于 .NET Framework 4.7+ 的 <c>System.ValueTuple&lt;T1,T2,T3&gt;</c>）。</summary>
    public struct ValueTuple<T1, T2, T3> : IEquatable<ValueTuple<T1, T2, T3>>
    {
        /// <summary>第一分量。</summary>
        public T1 Item1;

        /// <summary>第二分量。</summary>
        public T2 Item2;

        /// <summary>第三分量。</summary>
        public T3 Item3;

        /// <summary>构造。</summary>
        public ValueTuple(T1 item1, T2 item2, T3 item3)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }

        /// <inheritdoc/>
        public bool Equals(ValueTuple<T1, T2, T3> other) =>
            EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
            && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
            && EqualityComparer<T3>.Default.Equals(Item3, other.Item3);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3> other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            int h = EqualityComparer<T1>.Default.GetHashCode(Item1);
            h = (h * 397) ^ EqualityComparer<T2>.Default.GetHashCode(Item2);
            return (h * 397) ^ EqualityComparer<T3>.Default.GetHashCode(Item3);
        }

        /// <inheritdoc/>
        public override string ToString() => "(" + Item1 + ", " + Item2 + ", " + Item3 + ")";
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>元组元素名标注垫片（编译器为具名元组生成该特性）。</summary>
    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property
        | AttributeTargets.ReturnValue | AttributeTargets.Class | AttributeTargets.Struct
        | AttributeTargets.Event | AttributeTargets.Method,
        AllowMultiple = false, Inherited = false)]
    public sealed class TupleElementNamesAttribute : Attribute
    {
        /// <summary>构造。</summary>
        public TupleElementNamesAttribute(string?[] transformNames)
        {
            TransformNames = transformNames;
        }

        /// <summary>元素名（按编译器约定展开）。</summary>
        public IReadOnlyList<string?> TransformNames { get; }
    }
}
#endif
