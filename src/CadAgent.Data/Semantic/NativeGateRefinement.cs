using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

// 顺序即语义：前三值是切片A参数门（不得重排，消费侧/持久化可能按名或按序）；切片B炸开门三值只许末尾追加。
public enum NativeGateVerdict
{
    Pass,
    NotRead,
    ReadIncomplete,
    StaleMismatch,
    ExplodeNotRead,       // 炸开通道无该句柄取证行
    NotFullyExplodable,   // 递归炸开未到叶（残留异常）
    StaleExplode          // 重读炸开为空/类型漂移/bbox 漂移（stale 防护）
}

/// <summary>逐实体精准松绑判据（spec 切片A §4.2）：census Yellow 下目标句柄在 NativeEntities
/// 且 ReadOk 且本轮 live 重读一致才放行。重读=null（运行时句柄不存在）/ObjectName/bbox/Params
/// 任一项不符→StaleMismatch（stale overlay 防护）。安全关键：拒绝面全 reason 覆盖 100%。</summary>
public static class NativeGateRefinement
{
    // 炸开 bbox 逐分量容差（spec 切片B §4）：取值级浮点读回，非字符串化，需 epsilon。
    private const double ExplodeBboxTol = 1e-3;

    // XData 键前缀：TarchDerive 经炸开通道并入；live 重读白名单永不含（R13 裁定）。
    private const string XDataKeyPrefix = "XData:";

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

    /// <summary>切片B 合并准入：参数门 ∨ 炸开门（任一 Pass 即 Pass）。
    /// 参数门 Pass 直接放行（不取证炸开）；否则炸开门逐阶段返回拒因。两门均不过时**炸开门拒因优先**：
    /// 它定位到具体取证阶段（未取证/未炸透/重读漂移），信息量大于参数门的 NotRead/StaleMismatch
    /// （brief 7 facts 钉死，如"参数缺行 + bbox 漂移"→StaleExplode 而非 NotRead）。</summary>
    public static NativeGateVerdict EvaluateCombined(SemanticOverlay overlay, string handle,
        NativeEntityParam? rereadParams, ExplodeEvidence? rereadExplode)
    {
        var paramsVerdict = Evaluate(overlay, handle, rereadParams);
        if (paramsVerdict == NativeGateVerdict.Pass) return NativeGateVerdict.Pass;  // 参数门胜出，炸开门不再取证

        var stored = overlay.ExplodeEvidence?.FirstOrDefault(e => e.Handle == handle);
        if (stored == null) return NativeGateVerdict.ExplodeNotRead;
        if (!stored.FullyExplodable) return NativeGateVerdict.NotFullyExplodable;
        if (rereadExplode == null) return NativeGateVerdict.StaleExplode;
        // WrapperType/Layer 不比较：与参数门同理（类别映射/图层属可合法变更面）。
        if (rereadExplode.RxClass != stored.RxClass) return NativeGateVerdict.StaleExplode;
        if (!BboxNearEq(rereadExplode.Bbox, stored.Bbox)) return NativeGateVerdict.StaleExplode;
        return NativeGateVerdict.Pass;
    }

    // bbox 逐字相等（精确序列相等；双 null 相等，单 null 即不符）。值均为字符串化读回，不做数值容差。
    private static bool BboxEq(double[]? a, double[]? b) =>
        (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b));

    // 炸开 bbox 近等：双 null 相等，单 null 即不符（裁定：不视同通配）；长度/维度不符即不符。
    private static bool BboxNearEq(double[]? a, double[]? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > ExplodeBboxTol) return false;
        return true;
    }

    // Params 字符串化值相等：双侧剔除 "XData:*" 后（R13：炸开通道并入、重读白名单不含），
    // 键数相同，且每个键双方均存在且值完全一致（无解析/无 epsilon）。
    private static bool ParamsEq(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        var fa = NonXData(a);
        var fb = NonXData(b);
        return fa.Count == fb.Count && fa.All(kv => fb.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static Dictionary<string, string> NonXData(IReadOnlyDictionary<string, string> d)
    {
        var r = new Dictionary<string, string>(d.Count, StringComparer.Ordinal);
        foreach (var kv in d)
            if (!kv.Key.StartsWith(XDataKeyPrefix, StringComparison.Ordinal)) r[kv.Key] = kv.Value;
        return r;
    }
}
