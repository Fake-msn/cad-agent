using System;
using System.Collections.Generic;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// B0（技术债批次B）：块定义扫描行（聚合器输入）。具名值类型替代元组——公开签名不再
/// 引用 <c>System.ValueTuple</c>，net45 腿无需 System 命名空间垫片（协议层零依赖契约）。
/// </summary>
public readonly record struct BlockDefScanRow(string BlockName, string RxClass, bool IsLayout);

/// <summary>
/// B0（技术债批次B）：块定义聚合行（聚合器输出）。具名记录替代元组——公开签名不再引用
/// <c>System.ValueTuple</c>；具名属性亦可被 System.Text.Json 正常序列化（元组字段会退化为 {}）。
/// </summary>
public sealed record BlockDefAggRow(string BlockName, string RxClass, int Count);

/// <summary>
/// 块定义行聚合（技术债批次A·协议下沉）：桥内 B1 口径（布局块剔除 + 按 (块名, 类) 计数 +
/// Ordinal 定序）从命令实现提到协议层，桥与宿主/测试共用同一份判据。
/// 语义：**定义内**实体计数（定义 ≠ 模型空间实例数）；嵌套块只统计定义内的直接实体。
/// B0（技术债批次B）：输入/输出改为具名记录（<see cref="BlockDefScanRow"/> /
/// <see cref="BlockDefAggRow"/>），聚合语义逐项不变。
/// </summary>
public static class BlockDefAggregator
{
    /// <summary>
    /// 聚合扫描行：跳过布局块（<c>IsLayout</c>——模型/图纸空间不是块定义），
    /// 按 (块名, 类) 计数，输出按块名 → 类名 <see cref="StringComparison.Ordinal"/> 定序
    /// （同一图纸多次读取行序稳定，不随区域文化漂移）。空/缺省输入 → 空表。
    /// </summary>
    public static IReadOnlyList<BlockDefAggRow> Aggregate(IEnumerable<BlockDefScanRow> rows)
    {
        var counts = new Dictionary<Key, int>();
        if (rows != null)
        {
            foreach (var row in rows)
            {
                if (row.IsLayout) continue;
                if (row.BlockName == null || row.RxClass == null) continue;
                var key = new Key(row.BlockName, row.RxClass);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
        }

        var outp = new List<BlockDefAggRow>(counts.Count);
        foreach (var kv in counts)
            outp.Add(new BlockDefAggRow(kv.Key.BlockName, kv.Key.RxClass, kv.Value));
        outp.Sort(Compare);
        return outp;
    }

    private static int Compare(BlockDefAggRow a, BlockDefAggRow b)
    {
        int byName = string.CompareOrdinal(a.BlockName, b.BlockName);
        return byName != 0 ? byName : string.CompareOrdinal(a.RxClass, b.RxClass);
    }

    /// <summary>聚合键（块名 + 类名，Ordinal）。</summary>
    private readonly struct Key : IEquatable<Key>
    {
        public Key(string blockName, string rxClass)
        {
            BlockName = blockName;
            RxClass = rxClass;
        }

        public string BlockName { get; }

        public string RxClass { get; }

        public bool Equals(Key other) =>
            string.Equals(BlockName, other.BlockName, StringComparison.Ordinal)
            && string.Equals(RxClass, other.RxClass, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode()
            => (StringComparer.Ordinal.GetHashCode(BlockName) * 397)
               ^ StringComparer.Ordinal.GetHashCode(RxClass);
    }
}
