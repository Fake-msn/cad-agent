using System;
using System.Collections.Generic;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// 块定义行聚合（技术债批次A·协议下沉）：桥内 B1 口径（布局块剔除 + 按 (块名, 类) 计数 +
/// Ordinal 定序）从命令实现提到协议层，桥与宿主/测试共用同一份判据。
/// 语义：**定义内**实体计数（定义 ≠ 模型空间实例数）；嵌套块只统计定义内的直接实体。
/// </summary>
public static class BlockDefAggregator
{
    /// <summary>
    /// 聚合扫描行：跳过布局块（<c>IsLayout</c>——模型/图纸空间不是块定义），
    /// 按 (块名, 类) 计数，输出按块名 → 类名 <see cref="StringComparison.Ordinal"/> 定序
    /// （同一图纸多次读取行序稳定，不随区域文化漂移）。空/缺省输入 → 空表。
    /// </summary>
    public static IReadOnlyList<(string BlockName, string RxClass, int Count)> Aggregate(
        IEnumerable<(string BlockName, string RxClass, bool IsLayout)> rows)
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

        var outp = new List<(string BlockName, string RxClass, int Count)>(counts.Count);
        foreach (var kv in counts)
            outp.Add((kv.Key.BlockName, kv.Key.RxClass, kv.Value));
        outp.Sort(Compare);
        return outp;
    }

    private static int Compare((string BlockName, string RxClass, int Count) a,
        (string BlockName, string RxClass, int Count) b)
    {
        int byName = string.CompareOrdinal(a.BlockName, b.BlockName);
        return byName != 0 ? byName : string.CompareOrdinal(a.RxClass, b.RxClass);
    }

    /// <summary>聚合键（块名 + 类名，Ordinal）。net45 无 ValueTuple 依赖的字典键不受影响。</summary>
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
