using System.Text.Json;
using CadAgent.Core;

namespace CadAgent.Data.Vision;

/// <summary>单瓦片读图结果：claims + token 用量透传（llm-call 审计）+ 解析错误（诚实降级标志）。</summary>
public sealed record VisionTileRead(IReadOnlyList<VisionClaim> Claims, TokenUsage Usage, string? ParseError);

/// <summary>
/// 视觉读瓦片（生产接线 T1）：PNG→data-url→ILlmClient ImageUrl→结构化 claims。
/// 解析失败→ParseError+空 claims（诚实降级，不编造）；缺字段 claim 跳过。
/// 密钥不经本类（client 由调用方注入）；usage 透传供 llm-call 审计。
/// </summary>
public sealed class VisionReader
{
    public const string Prompt =
        "你是建筑电气施工图读图引擎。这张图是 CAD 渲染瓦片（深色背景为渲染底色非图纸内容）。" +
        "只输出一个 JSON 对象：{\"claims\":[{\"text\":\"可见文字或符号名称\",\"region\":\"瓦片内相对位置\",\"confidence\":0.0~1.0}]}。" +
        "不可读/模糊区域给低 confidence；看不到的不要编造；不要输出 JSON 以外文字。";

    private readonly ILlmClient _client;
    private readonly string _model;
    public VisionReader(ILlmClient client, string model) { _client = client; _model = model; }

    public async Task<VisionTileRead> ReadTileAsync(byte[] png, string sheetId,
        (double MinX, double MinY, double MaxX, double MaxY) rect, CancellationToken ct = default)
    {
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        var resp = await _client.ChatAsync(new ChatRequest(
            new[] { new ChatMessage("user", Prompt) }, 2048, ImageUrl: dataUrl), ct);
        var (claims, error) = ParseClaims(resp.Content);
        return new VisionTileRead(claims, resp.Usage, error);
    }

    internal static (IReadOnlyList<VisionClaim> Claims, string? Error) ParseClaims(string content)
    {
        string? json = TryExtractJson(content);
        if (json == null) return (Array.Empty<VisionClaim>(), "视觉输出非 JSON: " + Truncate(content));
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claims", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (Array.Empty<VisionClaim>(), "视觉 JSON 缺 claims 数组");
            var list = new List<VisionClaim>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("region", out var r) || r.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("confidence", out var c) || c.ValueKind != JsonValueKind.Number) continue;
                // R5：1e999 等语法合法但 double 不可表示→TryGetDouble 失败（部分运行时抛 FormatException/返回 ∞）；
                // 越界 [0,1]（含 ∞）一律与缺字段同通道跳过——模型任意输出不抛、非法置信度不流向种子。
                if (!c.TryGetDouble(out var conf) || conf < 0 || conf > 1) continue;
                list.Add(new VisionClaim(t.GetString()!, r.GetString()!, conf));
            }
            return (list, null);
        }
        catch (JsonException ex)
        {
            return (Array.Empty<VisionClaim>(), "视觉 JSON 解析失败: " + ex.Message);
        }
    }

    private static string? TryExtractJson(string content)
    {
        string s = content.Trim();
        if (s.StartsWith("{")) return s;
        int i = s.IndexOf('{'), j = s.LastIndexOf('}');
        return i >= 0 && j > i ? s[i..(j + 1)] : null;
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
