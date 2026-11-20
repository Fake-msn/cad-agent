using CadAgent.Core;

namespace CadAgent.Data.Tests;

public class SemanticGateImplTests
{
    [Fact]
    public void RealGate_AllowsHumanConfirmed_RefusesMissing()
    {
        var o = new SemanticOverlay();
        var gate = new CadAgent.Data.Semantic.SemanticGate(o);
        Assert.False(gate.Check(new[] { SemanticClass.Wall }).Allowed);
        o.UpsertAnnotation(new SemanticAnnotation("h1", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "人工"));
        Assert.True(gate.Check(new[] { SemanticClass.Wall }).Allowed);
        Assert.Contains("Door", gate.Check(new[] { SemanticClass.Door }).Reason);
    }
}
