using CadAgent.Core;
using CadAgent.Core.Cad;
using Xunit;

namespace CadAgent.Core.Tests;

public class TDbClassMapTests
{
    [Theory]
    [InlineData("TDbWall", SemanticClass.Wall)]
    [InlineData("TDbColumn", SemanticClass.Column)]
    [InlineData("TDbEquipment", SemanticClass.Equipment)]
    [InlineData("TDbText", SemanticClass.Annotation)]
    [InlineData("TDbDrawingName", SemanticClass.Annotation)]
    [InlineData("TDbSymbArrow", SemanticClass.Annotation)]
    [InlineData("TDbUnknownNew", SemanticClass.Unknown)]
    [InlineData("AcDbLine", SemanticClass.Unknown)]
    public void ClassFor_MapsObjectName(string objectName, SemanticClass expected)
        => Assert.Equal(expected, TDbClassMap.ClassFor(objectName));

    [Fact]
    public void Provenance_HasNativeParameter()
        => Assert.True(Enum.IsDefined(typeof(Provenance), "NativeParameter"));
}
