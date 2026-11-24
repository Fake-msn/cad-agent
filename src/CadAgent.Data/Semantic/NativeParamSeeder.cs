using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

/// <summary>COM 原生读参落 overlay（spec 切片A §3）：NativeEntities 幂等 + Class!=Unknown&&ReadOk
/// 同步产 Annotation(NativeParameter 0.95)，供语义闸口复用。重扫幂等（Upsert 键=handle+provenance）。</summary>
public static class NativeParamSeeder
{
    private const double NativeConfidence = 0.95;

    public static void Seed(SemanticOverlay overlay, IReadOnlyList<NativeEntityParam> parms)
    {
        foreach (var p in parms)
        {
            overlay.UpsertNative(p);
            if (p.Class != SemanticClass.Unknown && p.ReadOk)
                overlay.UpsertAnnotation(new SemanticAnnotation(
                    p.Handle, p.Class, Provenance.NativeParameter, NativeConfidence,
                    $"COM 原生读参: {p.ObjectName}"));
        }
    }
}
