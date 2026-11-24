using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

/// <summary>COM 原生读参落 overlay（spec 切片A §3）：NativeEntities 幂等 + Class!=Unknown&&ReadOk
/// 同步产 Annotation(NativeParameter 0.95)，供语义闸口复用。重扫幂等（Upsert 键=handle+provenance）；
/// 重扫先清该句柄陈旧 NativeParameter（"当前原生数据"语义，镜像 BlockAttributeSeeder R7 先例），
/// 读参降级（ReadOk=false/Unknown）后旧类标注不得残留放行闸口。</summary>
public static class NativeParamSeeder
{
    private const double NativeConfidence = 0.95;

    public static void Seed(SemanticOverlay overlay, IReadOnlyList<NativeEntityParam> parms)
    {
        foreach (var p in parms)
        {
            overlay.UpsertNative(p);
            // 先清除该句柄陈旧 NativeParameter 再条件种子（BlockAttributeSeeder R7 同口径）：
            // 重扫读参降级（ReadOk=false/Unknown）后旧类标注必须消失——"当前原生数据"语义，
            // 陈旧 0.95 标注不得继续满足闸口。当前仅本 seeder 产 NativeParameter，无其他源受影响。
            overlay.Annotations.RemoveAll(a => a.TargetHandle == p.Handle && a.Provenance == Provenance.NativeParameter);
            if (p.Class != SemanticClass.Unknown && p.ReadOk)
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    p.Handle, p.Class, Provenance.NativeParameter, NativeConfidence,
                    $"COM 原生读参: {p.ObjectName}"));
        }
    }
}
