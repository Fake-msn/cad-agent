using System.Security.Cryptography;
using CadAgent.Data.Rules;

namespace CadAgent.Data.Tests;

/// <summary>
/// 内置规则治理测试（2b-ii-b Task 2）：钉死全部 builtin YAML 嵌入资源原始字节
/// （资源名 Ordinal 序拼接、无分隔符）的 SHA256。内置规则内容任何变更→本测试红
/// →强制有意识升 RulePackVersion revision（"gb50001-2017.r1"→r2，格式由既有
/// SkillSet 测试守卫）。放在 Data.Tests：BuiltinRules 位于 Data（Core 不能引用 Data）。
///
/// 哈希推导（2b-ii-b Task 2 一次性计算，7 个文件共 4327 字节）：
/// typeof(BuiltinRules).Assembly.GetManifestResourceNames() 过滤前缀
/// "CadAgent.Data.Rules.builtin." 且后缀 ".yaml"→OrderBy(StringComparer.Ordinal)→
/// 依次读取各资源原始字节直接拼接→SHA256→小写 hex。等价 shell 推导：
/// ls *.yaml | LC_ALL=C sort | xargs cat | sha256sum（嵌入资源即文件原始字节）。
/// </summary>
public class BuiltinGovernanceTests
{
    private const string PinnedSha256 = "73a6039a1c09892973181b3039255b1c29507d9d48d362e757893faffd2c0484";

    [Fact]
    public void BuiltinYamlContents_Sha256_MatchesPinnedGovernanceHash()
    {
        var assembly = typeof(BuiltinRules).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("CadAgent.Data.Rules.builtin.", StringComparison.Ordinal)
                        && n.EndsWith(".yaml", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(7, names.Count);

        using var buffer = new MemoryStream();
        foreach (string name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"嵌入资源缺失: {name}");
            stream.CopyTo(buffer);
        }
        string hash = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        Assert.Equal(PinnedSha256, hash);
    }
}
