using System;
using System.Globalization;
using System.IO;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// 待处理请求三元路径与 id。以具名值类型承载（而非 <c>ValueTuple</c>）：net45 引用程序集不含
/// <c>System.ValueTuple</c>，而 <c>System.ValueTuple</c> 包对 net45 只解析出 netstandard1.0 运行时资产
/// （需 facade，AutoCAD 2014 的 .NET 4.5 进程不可靠），故本工程自带零依赖的等价载体。
/// </summary>
public readonly struct PendingRequest
{
    /// <summary>构造待处理请求。</summary>
    public PendingRequest(string reqPath, string resPath, string donePath, string id)
    {
        ReqPath = reqPath;
        ResPath = resPath;
        DonePath = donePath;
        Id = id;
    }

    /// <summary>请求文件 <c>req-&lt;id&gt;.txt</c> 全路径。</summary>
    public string ReqPath { get; }

    /// <summary>结果文件 <c>res-&lt;id&gt;.txt</c> 全路径。</summary>
    public string ResPath { get; }

    /// <summary>完成标记 <c>res-&lt;id&gt;.done</c> 全路径。</summary>
    public string DonePath { get; }

    /// <summary>请求 id 原文（十进制数字串）。</summary>
    public string Id { get; }
}

/// <summary>
/// 文件通道协议：宿主写 <c>req-&lt;id&gt;.txt</c>，桥读后写 <c>res-&lt;id&gt;.txt</c> 与
/// <c>res-&lt;id&gt;.done</c> 作为完成标记。仅触碰通道目录内文件，net45 兼容（不用 4.5 之后的 BCL 重载）。
/// </summary>
public static class BridgeFiles
{
    /// <summary>通道目录取自环境变量 <c>CADAGENT_TARCH_CHANNEL</c>；未设置时为 null。</summary>
    public static string? ChannelDir() => Environment.GetEnvironmentVariable("CADAGENT_TARCH_CHANNEL");

    /// <summary>
    /// 扫描通道目录中尚无对应 <c>.done</c> 的 <c>req-&lt;id&gt;.txt</c>，取数字最小者。
    /// 无非数字 id 或全部完成时返回 null。
    /// </summary>
    public static PendingRequest? NextPending(string channelDir)
    {
        string? best = null;
        int bestId = int.MaxValue;
        foreach (var f in Directory.GetFiles(channelDir, "req-*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length <= 4) continue;
            string id = name.Substring(4);
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
            if (File.Exists(DonePath(channelDir, id))) continue;
            if (n < bestId)
            {
                bestId = n;
                best = id;
            }
        }
        return best == null
            ? (PendingRequest?)null
            : new PendingRequest(ReqPath(channelDir, best), ResPath(channelDir, best), DonePath(channelDir, best), best);
    }

    /// <summary>
    /// 原子写结果：先落临时文件→替换 <c>res-&lt;id&gt;.txt</c>（UTF-8 无 BOM），最后写 <c>res-&lt;id&gt;.done</c>。
    /// done 后置保证宿主永不见半份结果；无 BOM 是硬要求——<see cref="BridgeResult.Parse"/> 不剥 BOM，
    /// 带 BOM 时首列会变成 "\uFEFFstatus" 而被判为未知记录。
    /// </summary>
    public static void WriteResultAtomic(string channelDir, string id, string text)
    {
        string res = ResPath(channelDir, id);
        string tmp = res + ".tmp";
        File.WriteAllText(tmp, text, Utf8NoBom);
        if (File.Exists(res)) File.Delete(res);
        File.Move(tmp, res);
        File.WriteAllText(DonePath(channelDir, id), "1", System.Text.Encoding.ASCII);
    }

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string ReqPath(string channelDir, string id) => Path.Combine(channelDir, "req-" + id + ".txt");

    private static string ResPath(string channelDir, string id) => Path.Combine(channelDir, "res-" + id + ".txt");

    private static string DonePath(string channelDir, string id) => Path.Combine(channelDir, "res-" + id + ".done");
}
