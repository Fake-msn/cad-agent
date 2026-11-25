using System;
using System.Collections.Generic;
using System.Globalization;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// 宿主→桥请求（TSV <c>key\tvalue</c>）：<c>op</c>/<c>dwg</c> 必填，
/// <c>explode-replace</c> 另需 <c>handle</c> 与 <c>liveout</c>。
/// </summary>
/// <param name="Op">操作名：read 或 explode-replace。</param>
/// <param name="Dwg">当前文档期望名，用于防错图。</param>
/// <param name="Handle">目标实体句柄（十六进制）。</param>
/// <param name="MaxDepth">递归读取深度，默认 4。</param>
/// <param name="LiveOut">替换产物 <c>.live.dwg</c> 保存路径。</param>
public sealed record BridgeRequest(string Op, string Dwg, string? Handle, int MaxDepth, string? LiveOut)
{
    /// <summary>序列化为 TSV 文本（每行一条 key/value，行尾 <c>\n</c>）。</summary>
    public string Write()
    {
        var lines = new List<string>
        {
            "op\t" + TsCodec.Escape(Op),
            "dwg\t" + TsCodec.Escape(Dwg),
            "maxdepth\t" + MaxDepth.ToString(CultureInfo.InvariantCulture),
        };
        if (Handle != null) lines.Add("handle\t" + TsCodec.Escape(Handle));
        if (LiveOut != null) lines.Add("liveout\t" + TsCodec.Escape(LiveOut));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>解析 TSV 请求；缺 key 或非法值一律 <see cref="FormatException"/>。</summary>
    public static BridgeRequest Parse(string text)
    {
        string? op = null, dwg = null, handle = null, liveout = null; int depth = 4;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith("\r") ? raw.Substring(0, raw.Length - 1) : raw;
            if (line.Length == 0) continue;
            var kv = TsCodec.SplitLine(line);
            if (kv.Length != 2) throw new FormatException($"请求行非法: {line}");
            switch (kv[0])
            {
                case "op": op = kv[1]; break;
                case "dwg": dwg = kv[1]; break;
                case "handle": handle = kv[1]; break;
                case "liveout": liveout = kv[1]; break;
                case "maxdepth":
                    if (!int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth))
                        throw new FormatException($"maxdepth 非法: {kv[1]}");
                    break;
                default: throw new FormatException($"未知请求键: {kv[0]}");
            }
        }
        if (op == null) throw new FormatException("缺少 op");
        if (dwg == null) throw new FormatException("缺少 dwg");
        if (op == "explode-replace" && (handle == null || liveout == null))
            throw new FormatException("explode-replace 需要 handle 与 liveout");
        return new BridgeRequest(op, dwg, handle, depth, liveout);
    }
}
