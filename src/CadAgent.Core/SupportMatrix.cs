namespace CadAgent.Core;

public interface ISupportMatrix
{
    bool IsSupported(string cppClassName);
}

public sealed class EmptySupportMatrix : ISupportMatrix
{
    public bool IsSupported(string cppClassName) => false;
}