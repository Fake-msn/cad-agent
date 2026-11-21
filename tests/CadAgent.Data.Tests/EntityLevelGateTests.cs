using CadAgent.Core;

namespace CadAgent.Data.Tests;

/// <summary>
/// Plan 3a T2: entity-level gate — target handle must carry an annotation of each
/// required class at ≥minConfidence; null handle degrades to operation-level Check.
/// </summary>
public class EntityLevelGateTests
{
    [Fact]
    public void ContextGate_EntityAnnotationRequired()
    {
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(overlay);
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, "h1").Allowed);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "h2").Allowed);  // 无标注实体
        Assert.True(gate.CheckContext(new[] { SemanticClass.Lighting }, null).Allowed);   // 操作级退化
        Assert.False(gate.CheckContext(new[] { SemanticClass.Wall }, null).Allowed);      // 操作级缺类
    }

    [Fact]
    public void ContextGate_LowConfidenceEntity_Refused()
    {
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Lighting, Provenance.GeometryInference, 0.5, "推断"));
        var gate = new CadAgent.Data.Semantic.SemanticGate(overlay);
        Assert.False(gate.CheckContext(new[] { SemanticClass.Lighting }, "h1").Allowed);
    }

    [Fact]
    public void LightingClass_Parseable()
    {
        Assert.Equal(SemanticClass.Lighting, Enum.Parse<SemanticClass>("Lighting", true));
    }
}