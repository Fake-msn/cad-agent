using CadAgent.Core;

namespace CadAgent.Llm;

/// <summary>探针结果：连通/补全/视觉（未测=null）/脱敏错误/用量。</summary>
public sealed record ProbeResult(bool Connected, bool CompletionOk, bool? VisionOk, string? Error, TokenUsage? Usage);

/// <summary>
/// 连通探针（§8.3 llm test 消费）：小补全（"回复 OK"，maxTokens 16）；
/// testVision 时发 16×16 PNG data-url 图片消息。补全异常→ProbeResult(false,...,脱敏 Error)；
/// 视觉异常不清零补全成功→ProbeResult(true, true, false, 脱敏 Error, 补全usage)。
/// </summary>
public sealed class LlmProbe
{
    // 16×16 红色 PNG（视觉探针载荷；qwen3-vl 系要求宽高 >10px，1×1 会被拒绝）
    public const string TestImageUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAF0lEQVR4nGP4z8BAEiJN9aiGUQ1DSgMAkPn/Afnh+ngAAAAASUVORK5CYII=";

    private readonly ILlmClient _client;
    public LlmProbe(ILlmClient client) => _client = client;

    public async Task<ProbeResult> RunAsync(bool testVision, CancellationToken ct = default)
    {
        ChatResponse resp;
        try
        {
            resp = await _client.ChatAsync(
                new ChatRequest(new[] { new ChatMessage("user", "回复 OK") }, 16), ct);
        }
        catch (Exception ex)
        {
            // 补全自身失败：连通即失败
            return new ProbeResult(false, false, null, Redact(ex.Message), null);
        }

        bool? visionOk = null;
        string? error = null;
        TokenUsage usage = resp.Usage;
        if (testVision)
        {
            // 视觉调用独立 try/catch：失败时保留补全成功与补全 usage（部分结果保真）
            try
            {
                var vresp = await _client.ChatAsync(
                    new ChatRequest(new[] { new ChatMessage("user", "这张图是什么颜色？回复 OK") }, 16, ImageUrl: TestImageUrl), ct);
                visionOk = true;
                usage = new TokenUsage(usage.PromptTokens + vresp.Usage.PromptTokens,
                    usage.CompletionTokens + vresp.Usage.CompletionTokens);
            }
            catch (Exception ex)
            {
                visionOk = false;
                error = Redact(ex.Message);
            }
        }
        return new ProbeResult(true, true, visionOk, error, usage);
    }

    // 脱敏：sk- 模式替换（含 workspace 键的 . 与 - 字符）；绝不回显密钥
    private static string Redact(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"sk-[A-Za-z0-9._-]{8,}", "***");
}
