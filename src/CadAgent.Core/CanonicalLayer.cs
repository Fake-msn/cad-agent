namespace CadAgent.Core;

/// <summary>bound xref 层名链归一（a$0$b$0$C → C）；约定匹配用归一名，映射/歧义清单保留原名。</summary>
public static class CanonicalLayer
{
    public static string Normalize(string layerName)
    {
        int i = layerName.LastIndexOf("$0$", StringComparison.Ordinal);
        return i >= 0 ? layerName[(i + 3)..] : layerName;
    }
}
