using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 块属性语义种子（T3）：天正电气块 Insert.Attributes 携带构件语义（A=深照型灯 等），
/// 确定性原生对象数据 → Provenance.NativeObject 置信 0.95，先于图层/几何推断。
/// 只读 Insert.Attributes（AttributeDefinition 不消费——AttDef 文本噪声属轴号检测器既有 deferred）。
/// N/$TEXT$ 辅助属性拼入 Evidence 不单独成类；未知 A 值不种子（诚实：不猜）。
/// SheetId 经插入点世界坐标归属（WalkedEntity.SheetId，R6 字段前移）。
/// 人工确认优先（与 LayerInference 同口径）：已 HumanConfirmed 的句柄不再种子。
/// </summary>
public static class BlockAttributeSeeder
{
    public static void Seed(CadDocument doc, SemanticOverlay overlay, IReadOnlyList<SheetRecord> sheets) =>
        SeedWalked(BlockWalker.Walk(doc, sheets).ToList(), overlay);

    /// <summary>块遍历口径核心（扫描器复用同一次 Walk，先于 LayerInference）。</summary>
    internal static void SeedWalked(IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay)
    {
        var humanConfirmedHandles = overlay.Annotations
            .Where(a => a.Provenance == Provenance.HumanConfirmed)
            .Select(a => a.TargetHandle)
            .ToHashSet();

        foreach (var w in walked)
        {
            if (w.Entity is not Insert ins || humanConfirmedHandles.Contains(w.HandlePath)) continue;
            // R7（T4）：先清除该句柄陈旧 NativeObject 再种子——"当前原生数据"语义：
            // 属性改判（深照型灯→未知）后 re-scan 不得残留旧类标注放行闸口。
            // 当前仅 seeder 产 NativeObject（视觉管道 LlmInference/人工 HumanConfirmed 不受影响）；
            // HumanConfirmed 句柄整体跳过（上方 continue），人工确认不动。
            overlay.Annotations.RemoveAll(a => a.TargetHandle == w.HandlePath && a.Provenance == Provenance.NativeObject);
            string? aValue = null;
            var extras = new List<string>();
            foreach (var att in ins.Attributes)
            {
                if (string.Equals(att.Tag, "A", StringComparison.OrdinalIgnoreCase))
                    aValue = att.Value; // 同 Tag 多属性→后值覆盖（T1 裁定同口径，诚实降级）
                else if (att.Tag is "N" or "$TEXT$")
                    extras.Add($"{att.Tag}={att.Value}");
            }
            if (aValue is null) continue;
            var cls = AttributeSemantics.ClassForAttributeValue("A", aValue);
            if (cls == SemanticClass.Unknown) continue; // 未知不猜
            string blockName = ins.Block?.Name ?? "?";
            string evidence = $"块属性 A={aValue} (块 {blockName})";
            if (extras.Count > 0) evidence += "；" + string.Join("；", extras);
            overlay.UpsertAnnotation(new SemanticAnnotation(
                w.HandlePath, cls, Provenance.NativeObject, 0.95, evidence, w.SheetId));
        }
    }
}
