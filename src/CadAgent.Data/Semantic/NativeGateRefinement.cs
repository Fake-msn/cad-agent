using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

public enum NativeGateVerdict { Pass, NotRead, ReadIncomplete, StaleMismatch }

/// <summary>逐实体精准松绑判据（spec 切片A §4.2）：census Yellow 下目标句柄在 NativeEntities
/// 且 ReadOk 且本轮 live 重读一致才放行。重读=null（运行时句柄不存在）/ObjectName/bbox/Params
/// 任一项不符→StaleMismatch（stale overlay 防护）。安全关键：拒绝面全 reason 覆盖 100%。</summary>
public static class NativeGateRefinement
{
    public static NativeGateVerdict Evaluate(SemanticOverlay overlay, string handle, NativeEntityParam? reread)
    {
        var rec = overlay.NativeEntities.FirstOrDefault(n => n.Handle == handle);
        if (rec == null) return NativeGateVerdict.NotRead;
        if (!rec.ReadOk) return NativeGateVerdict.ReadIncomplete;
        if (reread == null) return NativeGateVerdict.StaleMismatch;
        if (reread.ObjectName != rec.ObjectName) return NativeGateVerdict.StaleMismatch;
        // Class 不比较：ObjectName 经 TDbClassMap 决定类，映射演进属代码变更而非数据漂移（T3 复审关注项）。
        // Layer 不比较：非原生真值身份，change-layer 操作在放行后可合法变更图层。
        if (!BboxEq(reread.Bbox, rec.Bbox)) return NativeGateVerdict.StaleMismatch;
        if (!ParamsEq(reread.Params, rec.Params)) return NativeGateVerdict.StaleMismatch;
        return NativeGateVerdict.Pass;
    }

    // bbox 逐字相等（精确序列相等；双 null 相等，单 null 即不符）。值均为字符串化读回，不做数值容差。
    private static bool BboxEq(double[]? a, double[]? b) =>
        (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b));

    // Params 字符串化值相等：键数相同，且每个键双方均存在且值完全一致（无解析/无 epsilon）。
    private static bool ParamsEq(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
